using Rq1Experiment.Contracts.Config;

namespace Rq1Experiment.Core.Config;

public static class ExperimentConfigValidator
{
    public static ValidationResult Validate(ExperimentConfig config, int apiKeyCount)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(config.ExperimentId))
            errors.Add("Experiment ID is required.");
        else if (config.ExperimentId.Any(character =>
                     !(char.IsLetterOrDigit(character) || character is '-' or '_' or '.')))
            errors.Add("Experiment ID may contain only letters, digits, '.', '_' and '-'.");
        if (string.IsNullOrWhiteSpace(config.RepositoryRoot))
            errors.Add("Repository root is required.");
        else if (!Directory.Exists(config.RepositoryRoot))
            errors.Add("Repository root does not exist.");
        if (config.Mode.Equals("Smoke", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(config.Subjects.SmokeCandidateKey))
            errors.Add("Smoke focal method is required. Analyze the repository and select a method.");
        if (!ProviderIds.All.Contains(config.Llm.Provider, StringComparer.OrdinalIgnoreCase))
            errors.Add($"Provider must be one of: {string.Join(", ", ProviderIds.All)}.");
        if (string.IsNullOrWhiteSpace(config.Llm.ModelId))
            errors.Add("Exact model ID is required.");
        if (config.Llm.TimeoutSeconds is < 30 or > 3600)
            errors.Add("LLM request timeout must be between 30 and 3,600 seconds.");
        if (config.Llm.Provider.Equals(ProviderIds.DeepSeek, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(config.Llm.DeepSeekThinkingMode, "enabled", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(config.Llm.DeepSeekThinkingMode, "disabled", StringComparison.OrdinalIgnoreCase))
            errors.Add("DeepSeek thinking mode must be enabled or disabled.");
        if (config.Llm.Provider.Equals(ProviderIds.CodexCli, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(config.Llm.CodexExecutablePath)
                || !File.Exists(config.Llm.CodexExecutablePath))
                errors.Add("Codex CLI executable path is required and must exist.");
            if (string.IsNullOrWhiteSpace(config.Llm.CodexCliVersion))
                errors.Add("Codex CLI version must be frozen for an Official Codex run.");
            if (config.Llm.CodexReasoningEffort is not ("low" or "medium" or "high" or "xhigh" or "max" or "ultra"))
                errors.Add("Codex reasoning effort must be low, medium, high, xhigh, max, or ultra.");
            if (!config.Llm.CodexServiceTier.Equals("default", StringComparison.OrdinalIgnoreCase))
                errors.Add("Codex service tier must be default for this experiment harness.");
        }
        else if (apiKeyCount == 0)
            errors.Add("At least one API key is required for the selected provider.");
        if (config.Generation.RepairEnabled)
            errors.Add("Repair must remain disabled for RQ1.");
        if (!string.Equals(config.Generation.PipelineContractVersion,
                PipelineContractVersions.Current, StringComparison.Ordinal))
            errors.Add($"Pipeline contract must be {PipelineContractVersions.Current}. Create a new experiment for a different contract.");
        if (config.Generation.MaxTestMethods <= 0)
            errors.Add("Max test methods must be greater than zero.");
        if (config.Generation.MaxOutputTokensPerCall is < 256 or > 1_048_576)
            errors.Add("Max output tokens per API call must be between 256 and 1,048,576.");
        if (config.Generation.TotalTokenCeilingPerRun is < 256 or > 4_194_304)
            errors.Add("Total token ceiling per run must be between 256 and 4,194,304.");
        if (config.Generation.MaxParallelLlmCalls is < 1 or > 32)
            errors.Add("Parallel LLM calls must be between 1 and 32.");
        if (config.Tools.MaxParallelToolRuns is < 1 or > 8)
            errors.Add("Parallel tool runs must be between 1 and 8.");
        if (config.Subjects.Repetitions <= 0)
            errors.Add("Repetitions must be greater than zero.");

        return errors.Count == 0
            ? ValidationResult.Success
            : new ValidationResult(false, errors);
    }
}
