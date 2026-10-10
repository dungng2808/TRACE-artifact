using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rq1Experiment.Contracts.Config;
using Rq1Experiment.Core.Abstractions;

namespace Rq1Experiment.Infrastructure.Persistence;

public sealed class OfficialConfigLockService(WorkspaceLayout layout) : IOfficialConfigLockService
{
    public async Task<OfficialConfigLockResult> ValidateOrCreateAsync(
        ExperimentConfig config,
        CancellationToken cancellationToken = default)
    {
        if (!config.Mode.Equals("Official", StringComparison.OrdinalIgnoreCase))
            return new OfficialConfigLockResult(true, false, ComputeFingerprint(config), "Not an Official run; no lock required.");

        var fingerprint = ComputeFingerprint(config);
        var lockPath = layout.GetOfficialConfigLockPath(config.ExperimentId);
        var existing = await JsonSupport.ReadAsync<OfficialConfigLockDocument>(
            lockPath,
            cancellationToken);

        // Migrate the old global lock only when it belongs to this exact
        // experiment. A legacy lock from another experiment must not block a
        // new Official experiment.
        if (existing is null)
        {
            var legacy = await JsonSupport.ReadAsync<OfficialConfigLockDocument>(
                layout.OfficialConfigLockPath,
                cancellationToken);
            if (legacy is not null
                && string.Equals(legacy.ExperimentId, config.ExperimentId, StringComparison.Ordinal))
            {
                existing = legacy;
                await JsonSupport.WriteAsync(lockPath, legacy, cancellationToken);
            }
        }

        if (existing is null)
        {
            await JsonSupport.WriteAsync(lockPath, new OfficialConfigLockDocument
            {
                ExperimentId = config.ExperimentId,
                Fingerprint = fingerprint,
                ProviderId = config.Llm.Provider,
                ModelId = config.Llm.ModelId,
                CreatedAt = DateTimeOffset.UtcNow
            }, cancellationToken);
            return new OfficialConfigLockResult(true, true, fingerprint, "Official provider/model/configuration frozen.");
        }

        if (existing.ExperimentId != config.ExperimentId || existing.Fingerprint != fingerprint)
        {
            var legacyFingerprint = ComputeLegacyFingerprint(config);
            var legacyThinkingCompatible = !config.Llm.Provider.Equals(
                                               ProviderIds.DeepSeek, StringComparison.OrdinalIgnoreCase)
                                           || config.Llm.DeepSeekThinkingMode.Equals(
                                               "enabled", StringComparison.OrdinalIgnoreCase);
            if (existing.ExperimentId == config.ExperimentId
                && existing.Fingerprint == legacyFingerprint
                && legacyThinkingCompatible)
            {
                existing.Fingerprint = fingerprint;
                await JsonSupport.WriteAsync(lockPath, existing, cancellationToken);
                return new OfficialConfigLockResult(
                    true, false, fingerprint,
                    "Legacy Official lock migrated; DeepSeek behavior remains thinking=enabled.");
            }
            return new OfficialConfigLockResult(
                false,
                false,
                fingerprint,
                $"Official configuration for experiment '{config.ExperimentId}' differs from its frozen fingerprint. Create a new experiment ID.");
        }

        return new OfficialConfigLockResult(true, false, fingerprint, "Official configuration matches the frozen fingerprint.");
    }

    public async Task UpdateLockAsync(
        ExperimentConfig config,
        CancellationToken cancellationToken = default)
    {
        if (!config.Mode.Equals("Official", StringComparison.OrdinalIgnoreCase)) return;
        var fingerprint = ComputeFingerprint(config);
        var lockPath = layout.GetOfficialConfigLockPath(config.ExperimentId);
        await JsonSupport.WriteAsync(lockPath, new OfficialConfigLockDocument
        {
            ExperimentId = config.ExperimentId,
            Fingerprint = fingerprint,
            ProviderId = config.Llm.Provider,
            ModelId = config.Llm.ModelId,
            CreatedAt = DateTimeOffset.UtcNow
        }, cancellationToken);
    }

    public static string ComputeFingerprint(ExperimentConfig config)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            config.ExperimentId,
            config.RepositoryRoot,
            config.OutputRoot,
            config.Subjects,
            config.Generation,
            provider = config.Llm.Provider.ToLowerInvariant(),
            config.Llm.BaseUrl,
            config.Llm.ModelId,
            config.Llm.Temperature,
            config.Llm.TimeoutSeconds,
            config.Llm.DeepSeekThinkingMode,
            config.Llm.TransportRetryCount,
            config.Llm.ApiKeyStrategy,
            codex = config.Llm.Provider.Equals(ProviderIds.CodexCli, StringComparison.OrdinalIgnoreCase)
                ? new
                {
                    config.Llm.CodexExecutablePath,
                    config.Llm.CodexCliVersion,
                    config.Llm.CodexReasoningEffort,
                    config.Llm.CodexServiceTier,
                    config.PilotWaived,
                    config.PilotWaiverReason
                }
                : null,
            config.Tools
        }, JsonSupport.Options);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string ComputeLegacyFingerprint(ExperimentConfig config)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            config.ExperimentId,
            config.RepositoryRoot,
            config.OutputRoot,
            config.Subjects,
            config.Generation,
            provider = config.Llm.Provider.ToLowerInvariant(),
            config.Llm.BaseUrl,
            config.Llm.ModelId,
            config.Llm.Temperature,
            config.Llm.TimeoutSeconds,
            config.Llm.TransportRetryCount,
            config.Llm.ApiKeyStrategy,
            config.Tools
        }, JsonSupport.Options);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private sealed class OfficialConfigLockDocument
    {
        public string ExperimentId { get; set; } = string.Empty;
        public string Fingerprint { get; set; } = string.Empty;
        public string ProviderId { get; set; } = string.Empty;
        public string ModelId { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
    }
}
