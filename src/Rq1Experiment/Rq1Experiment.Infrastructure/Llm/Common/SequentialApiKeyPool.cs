using Rq1Experiment.Contracts.Config;
using Rq1Experiment.Core.Abstractions;
using Rq1Experiment.Infrastructure.Persistence;

namespace Rq1Experiment.Infrastructure.Llm.Common;

public sealed class SequentialApiKeyPool(
    IApiKeyStore keyStore,
    WorkspaceLayout layout) : IApiKeyPool
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ApiKeyLease?> AcquireAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var provider = NormalizeProvider(providerId);
            var keys = await keyStore.LoadAsync(provider, cancellationToken);
            var state = await LoadStateAsync(cancellationToken);
            var providerState = state.GetOrCreate(provider);
            providerState.LegacyUnavailableSlots.Clear();

            for (var offset = 0; offset < keys.Count; offset++)
            {
                var index = (Math.Max(0, providerState.NextIndex) + offset) % keys.Count;
                var slotId = ToSlotId(index);
                if (!providerState.QuotaExhaustedSlots.Contains(slotId, StringComparer.Ordinal)
                    && !providerState.InvalidSlots.Contains(slotId, StringComparer.Ordinal))
                {
                    // Persist the cursor for the *next* semantic/transport call.
                    // This makes successful calls rotate KEY_001 -> KEY_002 -> ... -> KEY_001.
                    providerState.NextIndex = (index + 1) % keys.Count;
                    await SaveStateAsync(state, cancellationToken);
                    return new ApiKeyLease(provider, slotId, keys[index]);
                }
            }

            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task MarkQuotaLimitedAsync(ApiKeyLease lease, CancellationToken cancellationToken = default) =>
        MarkUnavailableAsync(lease, quotaExhausted: true, cancellationToken);

    public Task MarkUnusableAsync(ApiKeyLease lease, CancellationToken cancellationToken = default) =>
        MarkUnavailableAsync(lease, quotaExhausted: false, cancellationToken);

    public async Task ResetProviderPoolAsync(string providerId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadStateAsync(cancellationToken);
            var providerState = state.GetOrCreate(NormalizeProvider(providerId));
            providerState.QuotaExhaustedSlots.Clear();
            providerState.InvalidSlots.Clear();
            providerState.LegacyUnavailableSlots.Clear();
            providerState.NextIndex = 0;
            await SaveStateAsync(state, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task MarkUnavailableAsync(
        ApiKeyLease lease,
        bool quotaExhausted,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadStateAsync(cancellationToken);
            var providerState = state.GetOrCreate(NormalizeProvider(lease.ProviderId));
            var slots = quotaExhausted
                ? providerState.QuotaExhaustedSlots
                : providerState.InvalidSlots;
            if (!slots.Contains(lease.SlotId, StringComparer.Ordinal)) slots.Add(lease.SlotId);
            await SaveStateAsync(state, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ApiKeyPoolState> LoadStateAsync(CancellationToken cancellationToken) =>
        await JsonSupport.ReadAsync<ApiKeyPoolState>(layout.ApiKeyStatePath, cancellationToken)
        ?? new ApiKeyPoolState();

    private Task SaveStateAsync(ApiKeyPoolState state, CancellationToken cancellationToken) =>
        JsonSupport.WriteAsync(layout.ApiKeyStatePath, state, cancellationToken);

    private static string NormalizeProvider(string providerId) =>
        ProviderIds.All.FirstOrDefault(value => value.Equals(providerId, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException("Unsupported provider.", nameof(providerId));

    private static string ToSlotId(int zeroBasedIndex) => $"KEY_{zeroBasedIndex + 1:000}";

    private sealed class ApiKeyPoolState
    {
        public Dictionary<string, ProviderKeyState> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public ProviderKeyState GetOrCreate(string provider)
        {
            if (!Providers.TryGetValue(provider, out var state))
            {
                state = new ProviderKeyState();
                Providers[provider] = state;
            }

            return state;
        }
    }

    private sealed class ProviderKeyState
    {
        public int NextIndex { get; set; }
        public List<string> QuotaExhaustedSlots { get; set; } = [];
        public List<string> InvalidSlots { get; set; } = [];

        // Compatibility only. Older builds placed every HTTP 429 here even
        // when it was a temporary RPM/TPM limit. Such ambiguous marks are
        // intentionally cleared instead of permanently suppressing the key.
        [System.Text.Json.Serialization.JsonPropertyName("unavailableSlots")]
        public List<string> LegacyUnavailableSlots { get; set; } = [];
    }
}
