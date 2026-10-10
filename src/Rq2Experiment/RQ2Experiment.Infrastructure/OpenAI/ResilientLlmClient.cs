using System.Net;
using System.Text.Json;
using RQ2Experiment.Core.Domain;
using RQ2Experiment.Infrastructure.LLM;

namespace RQ2Experiment.Infrastructure.OpenAI;

public sealed record LlmResilienceOptions(
    int MaxTransientRetries = 3,
    int MaxRateLimitWaits = 1,
    TimeSpan? BaseDelay = null,
    TimeSpan? MaximumDelay = null,
    TimeSpan? DefaultRateLimitDelay = null,
    TimeSpan? MinimumRequestInterval = null)
{
    internal TimeSpan EffectiveBaseDelay => BaseDelay ?? TimeSpan.FromSeconds(2);
    internal TimeSpan EffectiveMaximumDelay => MaximumDelay ?? TimeSpan.FromSeconds(30);
    internal TimeSpan EffectiveRateLimitDelay => DefaultRateLimitDelay ?? TimeSpan.FromSeconds(30);
    // A shared Google project can rate-limit every key in a pool.  Pace requests
    // globally, rather than treating additional keys as additional quota.
    internal TimeSpan EffectiveMinimumRequestInterval => MinimumRequestInterval ?? TimeSpan.FromSeconds(4);
}

/// <summary>
/// Keeps one key sticky to a run, retries transient provider failures on that key,
/// and fails over only for rate-limit or authentication failures.
/// </summary>
public sealed class ResilientLlmClient : ILlmRepairClient
{
    private readonly object gate = new();
    private readonly List<KeySlot> slots;
    private readonly Dictionary<string, RunKeyState> runStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly LlmResilienceOptions options;
    private readonly SemaphoreSlim requestPacingGate = new(1, 1);
    private DateTimeOffset nextRequestStartUtc = DateTimeOffset.MinValue;
    private int nextInitialKeyIndex;
    private bool disposed;

    public ResilientLlmClient(
        IReadOnlyList<string> apiKeys,
        string baseUrl,
        LlmApiProtocol protocol,
        bool requiresApiKey,
        bool useJsonMode,
        LlmResilienceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(apiKeys);
        var distinctKeys = apiKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (requiresApiKey && distinctKeys.Length == 0)
        {
            throw new ArgumentException("At least one API key is required.", nameof(apiKeys));
        }

        if (distinctKeys.Length == 0) distinctKeys = [string.Empty];
        slots = distinctKeys
            .Select(key => new KeySlot(
                new CompatibleLlmClient(key, baseUrl, protocol, requiresApiKey, useJsonMode),
                ApiKeyFingerprint.Create(key)))
            .ToList();
        this.options = options ?? new LlmResilienceOptions();
    }

    public IReadOnlyList<string> KeyFingerprints => slots.Select(slot => slot.Fingerprint).ToArray();

    public async Task<LlmRepairResponse> GenerateRepairAsync(
        string model,
        string reasoningEffort,
        string instructions,
        string input,
        string artifactDirectory,
        int iteration,
        CancellationToken cancellationToken,
        string? requestTag = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Directory.CreateDirectory(artifactDirectory);
        var state = GetOrCreateRunState(Path.GetFullPath(artifactDirectory));
        var attempts = 0;
        var retries = 0;
        var failovers = 0;
        var transientRetries = 0;
        var rateLimitWaits = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var slot = GetSlot(state.KeyIndex);
            attempts++;
            var tag = $"attempt-{attempts:00}-key-{slot.Fingerprint}";

            try
            {
                var pacingDelay = await WaitForRequestWindowAsync(cancellationToken);
                if (pacingDelay > TimeSpan.Zero)
                {
                    await WriteAttemptLogAsync(
                        artifactDirectory,
                        iteration,
                        attempts,
                        slot.Fingerprint,
                        "Paced",
                        "global request pacing",
                        pacingDelay,
                        cancellationToken);
                }
                var response = await slot.Client.GenerateRepairAsync(
                    model,
                    reasoningEffort,
                    instructions,
                    input,
                    artifactDirectory,
                    iteration,
                    cancellationToken,
                    tag);
                await WriteAttemptLogAsync(
                    artifactDirectory,
                    iteration,
                    attempts,
                    slot.Fingerprint,
                    "Success",
                    response.FirstPassJsonValid
                        ? "response accepted"
                        : $"response normalized: {response.OutputNormalization}",
                    TimeSpan.Zero,
                    cancellationToken);
                return response with
                {
                    ApiCalls = attempts,
                    RetryCount = retries,
                    KeyFailoverCount = failovers,
                    ApiKeyFingerprint = slot.Fingerprint
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (LlmHttpException exception) when (
                exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                DisableSlot(state.KeyIndex);
                await WriteAttemptLogAsync(
                    artifactDirectory,
                    iteration,
                    attempts,
                    slot.Fingerprint,
                    ((int)exception.StatusCode!.Value).ToString(),
                    "key disabled",
                    TimeSpan.Zero,
                    cancellationToken);
                if (TrySwitchToAvailableKey(state))
                {
                    failovers++;
                    transientRetries = 0;
                    continue;
                }

                throw new ExperimentExecutionException(
                    RunFailureCategory.AuthenticationFailure,
                    "All API keys were rejected with 401/403. Revalidate the provider credentials.",
                    exception,
                    attempts,
                    retries,
                    failovers,
                    slot.Fingerprint);
            }
            catch (LlmHttpException exception) when (exception.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var delay = ClampDelay(exception.RetryAfter ?? options.EffectiveRateLimitDelay);
                CoolDownSlot(state.KeyIndex, delay);
                await WriteAttemptLogAsync(
                    artifactDirectory,
                    iteration,
                    attempts,
                    slot.Fingerprint,
                    "429",
                    "key placed in cooldown",
                    delay,
                    cancellationToken);
                if (TrySwitchToAvailableKey(state))
                {
                    failovers++;
                    transientRetries = 0;
                    continue;
                }

                if (rateLimitWaits < options.MaxRateLimitWaits && TrySelectEarliestCoolingKey(state, out var wait))
                {
                    rateLimitWaits++;
                    retries++;
                    await Task.Delay(ClampDelay(wait), cancellationToken);
                    continue;
                }

                throw new ExperimentExecutionException(
                    RunFailureCategory.ApiRateLimit,
                    "All API keys are rate-limited. Retry after the provider cooldown window.",
                    exception,
                    attempts,
                    retries,
                    failovers,
                    slot.Fingerprint);
            }
            catch (LlmHttpException exception) when (IsTransientProviderStatus(exception.StatusCode))
            {
                if (transientRetries >= options.MaxTransientRetries)
                {
                    throw new ExperimentExecutionException(
                        RunFailureCategory.ProviderUnavailable,
                        $"Provider remained unavailable after {attempts} API attempt(s); the repair iteration was not consumed.",
                        exception,
                        attempts,
                        retries,
                        failovers,
                        slot.Fingerprint);
                }

                var delay = ExponentialDelay(transientRetries++);
                retries++;
                await WriteAttemptLogAsync(
                    artifactDirectory,
                    iteration,
                    attempts,
                    slot.Fingerprint,
                    ((int)exception.StatusCode!.Value).ToString(),
                    "retry same key",
                    delay,
                    cancellationToken);
                await Task.Delay(delay, cancellationToken);
            }
            catch (HttpRequestException exception)
            {
                if (transientRetries >= options.MaxTransientRetries)
                {
                    throw new ExperimentExecutionException(
                        RunFailureCategory.NetworkFailure,
                        $"Network request failed after {attempts} API attempt(s); the repair iteration was not consumed.",
                        exception,
                        attempts,
                        retries,
                        failovers,
                        slot.Fingerprint);
                }

                var delay = ExponentialDelay(transientRetries++);
                retries++;
                await WriteAttemptLogAsync(
                    artifactDirectory,
                    iteration,
                    attempts,
                    slot.Fingerprint,
                    "NetworkError",
                    "retry same key",
                    delay,
                    cancellationToken);
                await Task.Delay(delay, cancellationToken);
            }
            catch (LlmModelOutputException exception)
            {
                await WriteAttemptLogAsync(
                    artifactDirectory,
                    iteration,
                    attempts,
                    slot.Fingerprint,
                    "InvalidModelOutput",
                    exception.Usage.Reported
                        ? $"response rejected; accounted {exception.Usage.TotalTokens} tokens"
                        : "response rejected; provider usage unavailable",
                    TimeSpan.Zero,
                    cancellationToken);
                throw new ExperimentExecutionException(
                    RunFailureCategory.InvalidModelOutput,
                    exception.Message,
                    exception,
                    attempts,
                    retries,
                    failovers,
                    slot.Fingerprint,
                    exception.Usage.InputTokens,
                    exception.Usage.OutputTokens,
                    exception.Usage.CachedInputTokens,
                    exception.Usage.CacheWriteInputTokens,
                    exception.Usage.ReasoningTokens,
                    exception.Usage.Reported ? 0 : 1);
            }
            catch (InvalidOperationException exception)
            {
                throw new ExperimentExecutionException(
                    RunFailureCategory.InvalidModelOutput,
                    exception.Message,
                    exception,
                    attempts,
                    retries,
                    failovers,
                    slot.Fingerprint,
                    unreportedUsageResponses: 1);
            }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var slot in slots) slot.Client.Dispose();
    }

    private RunKeyState GetOrCreateRunState(string artifactDirectory)
    {
        lock (gate)
        {
            if (runStates.TryGetValue(artifactDirectory, out var existing)) return existing;
            var selected = FindInitialKeyIndex();
            var state = new RunKeyState(selected);
            runStates.Add(artifactDirectory, state);
            return state;
        }
    }

    private int FindInitialKeyIndex()
    {
        var now = DateTimeOffset.UtcNow;
        for (var offset = 0; offset < slots.Count; offset++)
        {
            var index = (nextInitialKeyIndex + offset) % slots.Count;
            if (!slots[index].Disabled && slots[index].CooldownUntil <= now)
            {
                nextInitialKeyIndex = (index + 1) % slots.Count;
                return index;
            }
        }

        var fallback = slots
            .Select((slot, index) => (slot, index))
            .Where(item => !item.slot.Disabled)
            .OrderBy(item => item.slot.CooldownUntil)
            .Select(item => item.index)
            .FirstOrDefault(-1);
        if (fallback < 0)
        {
            throw new ExperimentExecutionException(
                RunFailureCategory.AuthenticationFailure,
                "No usable API key remains in the pool.");
        }

        nextInitialKeyIndex = (fallback + 1) % slots.Count;
        return fallback;
    }

    private bool TrySwitchToAvailableKey(RunKeyState state)
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            for (var offset = 1; offset < slots.Count; offset++)
            {
                var candidate = (state.KeyIndex + offset) % slots.Count;
                if (slots[candidate].Disabled || slots[candidate].CooldownUntil > now) continue;
                state.KeyIndex = candidate;
                return true;
            }

            return false;
        }
    }

    private bool TrySelectEarliestCoolingKey(RunKeyState state, out TimeSpan wait)
    {
        lock (gate)
        {
            var candidate = slots
                .Select((slot, index) => (slot, index))
                .Where(item => !item.slot.Disabled)
                .OrderBy(item => item.slot.CooldownUntil)
                .FirstOrDefault();
            if (candidate.slot is null)
            {
                wait = TimeSpan.Zero;
                return false;
            }

            state.KeyIndex = candidate.index;
            wait = candidate.slot.CooldownUntil - DateTimeOffset.UtcNow;
            if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
            return true;
        }
    }

    private KeySlot GetSlot(int index)
    {
        lock (gate) return slots[index];
    }

    private void DisableSlot(int index)
    {
        lock (gate) slots[index].Disabled = true;
    }

    private void CoolDownSlot(int index, TimeSpan delay)
    {
        lock (gate) slots[index].CooldownUntil = DateTimeOffset.UtcNow + delay;
    }

    private TimeSpan ExponentialDelay(int retryIndex)
    {
        var multiplier = Math.Pow(2, retryIndex);
        var milliseconds = options.EffectiveBaseDelay.TotalMilliseconds * multiplier;
        var jitter = Random.Shared.NextDouble() * Math.Min(500, milliseconds * 0.25);
        return ClampDelay(TimeSpan.FromMilliseconds(milliseconds + jitter));
    }

    private TimeSpan ClampDelay(TimeSpan delay)
    {
        if (delay < TimeSpan.Zero) return TimeSpan.Zero;
        return delay <= options.EffectiveMaximumDelay ? delay : options.EffectiveMaximumDelay;
    }

    private async Task<TimeSpan> WaitForRequestWindowAsync(CancellationToken cancellationToken)
    {
        var interval = options.EffectiveMinimumRequestInterval;
        if (interval <= TimeSpan.Zero) return TimeSpan.Zero;

        await requestPacingGate.WaitAsync(cancellationToken);
        TimeSpan delay;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var scheduled = nextRequestStartUtc > now ? nextRequestStartUtc : now;
            delay = scheduled - now;
            nextRequestStartUtc = scheduled + interval;
        }
        finally
        {
            requestPacingGate.Release();
        }

        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken);
        }

        return delay;
    }

    private static bool IsTransientProviderStatus(HttpStatusCode? statusCode) => statusCode is
        HttpStatusCode.InternalServerError or
        HttpStatusCode.BadGateway or
        HttpStatusCode.ServiceUnavailable or
        HttpStatusCode.GatewayTimeout;

    private static async Task WriteAttemptLogAsync(
        string artifactDirectory,
        int iteration,
        int attempt,
        string fingerprint,
        string status,
        string action,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        var record = JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.UtcNow,
            iteration,
            attempt,
            keyFingerprint = fingerprint,
            status,
            action,
            delayMs = (int)delay.TotalMilliseconds
        });
        await File.AppendAllTextAsync(
            Path.Combine(artifactDirectory, $"iteration-{iteration:00}-api-attempts.jsonl"),
            record + Environment.NewLine,
            cancellationToken);
    }

    private sealed class KeySlot(CompatibleLlmClient client, string fingerprint)
    {
        public CompatibleLlmClient Client { get; } = client;
        public string Fingerprint { get; } = fingerprint;
        public bool Disabled { get; set; }
        public DateTimeOffset CooldownUntil { get; set; }
    }

    private sealed class RunKeyState(int keyIndex)
    {
        public int KeyIndex { get; set; } = keyIndex;
    }
}
