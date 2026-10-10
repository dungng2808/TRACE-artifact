namespace Rq1Experiment.Contracts.Config;

public sealed class ExperimentConfig
{
    public string ExperimentId { get; set; } = "rq1-draft";
    public string Mode { get; set; } = "Smoke";
    public string RepositoryRoot { get; set; } = string.Empty;
    public string OutputRoot { get; set; } = "experiments/rq1";
    public SubjectSettings Subjects { get; set; } = new();
    public GenerationSettings Generation { get; set; } = new();
    public LlmSettings Llm { get; set; } = new();
    public ToolSettings Tools { get; set; } = new();
    public bool PilotWaived { get; set; }
    public string PilotWaiverReason { get; set; } = string.Empty;
    public bool IsFrozen { get; set; }
}

public sealed class SubjectSettings
{
    public string DatasetProjectsRoot { get; set; } = "datasets/rq1/projecttest/java-ready/projects";
    public string BenchmarkRevision { get; set; } = "a893144b017c44e9a042d605a04baa447f525433";
    public int PilotCount { get; set; } = 5;
    public int OfficialCount { get; set; } = 40;
    public int MaxPerProject { get; set; } = 3;
    public int Repetitions { get; set; } = 1;
    public int SelectionSeed { get; set; } = 4902026;
    public string SmokeCandidateKey { get; set; } = string.Empty;
    public string SmokeFocalClass { get; set; } = string.Empty;
    public string SmokeFocalMethodSignature { get; set; } = string.Empty;
}

public sealed class GenerationSettings
{
    public string PipelineContractVersion { get; set; } = PipelineContractVersions.Current;
    public int MaxTestMethods { get; set; } = 6;
    public int MaxOutputTokensPerCall { get; set; } = 65_536;
    public int TotalTokenCeilingPerRun { get; set; } = 65_536;
    public int MaxParallelLlmCalls { get; set; } = 4;
    public bool RepairEnabled { get; set; }
}

public static class PipelineContractVersions
{
    public const string Current = "rq1-pipeline-contract-v5-cfg";
}

public sealed class LlmSettings
{
    public string Provider { get; set; } = ProviderIds.Gemini;
    public string BaseUrl { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public double Temperature { get; set; }
    public int TimeoutSeconds { get; set; } = 180;
    public string DeepSeekThinkingMode { get; set; } = "enabled";
    public int TransportRetryCount { get; set; } = 2;
    public string ApiKeyStrategy { get; set; } = "round-robin-failover";
    public string CodexExecutablePath { get; set; } = string.Empty;
    public string CodexCliVersion { get; set; } = string.Empty;
    public string CodexReasoningEffort { get; set; } = "low";
    public string CodexServiceTier { get; set; } = "default";
}

public sealed class ToolSettings
{
    public string JavaHome { get; set; } = string.Empty;
    public string ModulePath { get; set; } = ".";
    public int CompileTimeoutSeconds { get; set; } = 300;
    public int TestTimeoutSeconds { get; set; } = 300;
    public int JacocoTimeoutSeconds { get; set; } = 300;
    public int PitTimeoutSeconds { get; set; } = 900;
    public int MaxParallelToolRuns { get; set; } = 2;
    public string JacocoVersion { get; set; } = "0.8.15";
    public string PitVersion { get; set; } = "1.25.3";
}

public static class ProviderIds
{
    public const string CodexCli = "codex-cli";
    public const string Gemini = "gemini";
    public const string DeepSeek = "deepseek";
    public const string OpenRouter = "openrouter";
    public const string TeamoRouter = "teamorouter";
    public const string JustWoker = "justwoker";

    public static IReadOnlyList<string> All { get; } =
        [CodexCli, Gemini, DeepSeek, OpenRouter, TeamoRouter, JustWoker];
}
