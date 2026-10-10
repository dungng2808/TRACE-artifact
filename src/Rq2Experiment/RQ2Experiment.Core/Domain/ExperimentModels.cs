using System.Text.Json.Serialization;

namespace RQ2Experiment.Core.Domain;

public sealed record ExperimentTask(
    string TaskId,
    string RepositoryPath,
    string ScenarioId,
    string TestId,
    string FailingTestMethod,
    string ScenarioSpecification,
    string TraceabilityLink,
    string InitialEvidence,
    string TestFilePath = "",
    string TestClassName = "",
    string ModulePath = "",
    string BuildSystem = "maven",
    IReadOnlyList<string>? FocalSourceFiles = null,
    IReadOnlyList<string>? PreviouslyVerifiedTests = null,
    string CoverageReportPath = "target/site/jacoco/jacoco.xml",
    string MutationReportPath = "target/pit-reports/mutations.xml",
    string BuggyRepositoryPath = "",
    string ProjectId = "",
    string DatasetId = "",
    string ArtifactId = "",
    string SourceUri = "",
    string FixedRevision = "",
    string BuggyRevision = "",
    string IndependenceUnitId = "",
    string SamplingStratum = "",
    string JavaHome = "")
{
    public IReadOnlyList<string> FocalSources => FocalSourceFiles ?? [];
    public IReadOnlyList<string> VerifiedTests => PreviouslyVerifiedTests ?? [];
    public bool RequiresBuggyOracle => !string.IsNullOrWhiteSpace(BuggyRepositoryPath);

    public void ValidateForRealExecution()
    {
        if (string.IsNullOrWhiteSpace(TaskId) || string.IsNullOrWhiteSpace(RepositoryPath))
        {
            throw new ArgumentException("TaskId and RepositoryPath are required.");
        }

        if (string.IsNullOrWhiteSpace(TestFilePath) || string.IsNullOrWhiteSpace(TestClassName))
        {
            throw new ArgumentException($"Task {TaskId} requires TestFilePath and TestClassName.");
        }

        if (!BuildSystem.Equals("maven", StringComparison.OrdinalIgnoreCase) &&
            !BuildSystem.Equals("gradle", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Task {TaskId} has unsupported BuildSystem '{BuildSystem}'.");
        }

        if (!string.IsNullOrWhiteSpace(JavaHome) &&
            !Directory.Exists(JavaHome))
        {
            throw new DirectoryNotFoundException($"Task {TaskId} JavaHome does not exist: {JavaHome}");
        }
    }
}

public sealed record ExperimentConfiguration(
    string ModelName,
    int MaxIterations,
    int TokenBudget,
    int Repetitions,
    IReadOnlyList<RepairStrategy> Strategies,
    string ReasoningEffort = "medium",
    string ProviderName = "OpenAI",
    string ApiProtocol = "Responses",
    string BatchId = "",
    int ScheduleSeed = 20260814,
    StudyPlanEvidence? StudyPlan = null,
    string RunPlanFingerprint = "",
    int MaxParallelRuns = 1)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ModelName))
        {
            throw new ArgumentException("Model name is required.", nameof(ModelName));
        }

        if (MaxIterations <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxIterations));
        }

        if (TokenBudget <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(TokenBudget));
        }

        if (Repetitions <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Repetitions));
        }

        // Java build, JaCoCo and PIT are CPU/disk intensive.  Keep the upper
        // bound conservative so a local workstation does not turn parallelism
        // into systematic infrastructure failures.
        if (MaxParallelRuns is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxParallelRuns),
                "Parallel runs must be between 1 and 4.");
        }

        if (Strategies.Count == 0)
        {
            throw new ArgumentException("Select at least one repair strategy.", nameof(Strategies));
        }

        if (Strategies.Distinct().Count() != Strategies.Count)
        {
            throw new ArgumentException("Repair strategies must not contain duplicates.", nameof(Strategies));
        }

        var allowedEfforts = new[] { "none", "low", "medium", "high", "xhigh", "max" };
        if (!allowedEfforts.Contains(ReasoningEffort, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Unsupported reasoning effort.", nameof(ReasoningEffort));
        }
    }
}

public enum RunStatus
{
    Queued,
    Running,
    Success,
    Failed,
    BudgetExhausted,
    Cancelled
}

public enum RunFailureCategory
{
    None,
    RepairFailure,
    RegressionFailure,
    BudgetExhausted,
    ApiRateLimit,
    ProviderUnavailable,
    AuthenticationFailure,
    NetworkFailure,
    InvalidModelOutput,
    TreatmentIsolationViolation,
    Unexpected
}

public sealed class ExperimentExecutionException : Exception
{
    public ExperimentExecutionException(
        RunFailureCategory category,
        string message,
        Exception? innerException = null,
        int apiCalls = 0,
        int apiRetries = 0,
        int keyFailovers = 0,
        string apiKeyFingerprint = "",
        int inputTokens = 0,
        int outputTokens = 0,
        int cachedInputTokens = 0,
        int cacheWriteInputTokens = 0,
        int reasoningTokens = 0,
        int unreportedUsageResponses = 0,
        RepairStageEvidence? stageEvidence = null,
        PromptAuditEvidence? promptAuditEvidence = null)
        : base(message, innerException)
    {
        Category = category;
        ApiCalls = apiCalls;
        ApiRetries = apiRetries;
        KeyFailovers = keyFailovers;
        ApiKeyFingerprint = apiKeyFingerprint;
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        CachedInputTokens = cachedInputTokens;
        CacheWriteInputTokens = cacheWriteInputTokens;
        ReasoningTokens = reasoningTokens;
        UnreportedUsageResponses = unreportedUsageResponses;
        StageEvidence = stageEvidence;
        PromptAuditEvidence = promptAuditEvidence;
    }

    public RunFailureCategory Category { get; }
    public int ApiCalls { get; }
    public int ApiRetries { get; }
    public int KeyFailovers { get; }
    public string ApiKeyFingerprint { get; }
    public int InputTokens { get; }
    public int OutputTokens { get; }
    public int TotalTokens => InputTokens + OutputTokens;
    public int CachedInputTokens { get; }
    public int CacheWriteInputTokens { get; }
    public int ReasoningTokens { get; }
    public int UnreportedUsageResponses { get; }
    public RepairStageEvidence? StageEvidence { get; }
    public PromptAuditEvidence? PromptAuditEvidence { get; }
}

public enum MeasurementStatus
{
    NotRun,
    Succeeded,
    ToolFailed,
    ReportMissing,
    ReportInvalid,
    ConfigurationError,
    ToolTimedOut,
    ToolUnavailable,
    ReportStale
}

public sealed record TokenUsage(
    int InputTokens = 0,
    int OutputTokens = 0,
    int CachedInputTokens = 0,
    int CacheWriteInputTokens = 0,
    int ReasoningTokens = 0,
    int UnreportedUsageResponses = 0,
    bool AccountingComplete = true,
    bool UsageReported = true,
    int? LegacyTotalTokens = null)
{
    public int TotalTokens => LegacyTotalTokens ?? InputTokens + OutputTokens;
}

public sealed record ApiUsage(
    int Calls = 0,
    int Retries = 0,
    int KeyFailovers = 0,
    string LastKeyFingerprint = "");

public sealed record RepairVerification(
    int ChangedLines = 0,
    int TouchedMethods = 0,
    int RegressedTests = 0,
    bool FocalTestPassed = false,
    bool AdequacyVerified = false,
    bool BuggyOracleRequired = false,
    bool BugReproduced = false);

public sealed record QualityMeasurement(
    double? LineCoverage = null,
    double? MutationScore = null,
    MeasurementStatus CoverageStatus = MeasurementStatus.NotRun,
    MeasurementStatus MutationStatus = MeasurementStatus.NotRun,
    string CoverageDetail = "",
    string MutationDetail = "",
    string CoverageReportPath = "",
    string MutationReportPath = "");

public sealed record LlmOutputEvidence(
    bool FirstPassJsonValid = true,
    string Normalization = "None");

public sealed record PromptAuditEvidence
{
    // Version 2 freezes the common compiler-feedback contract used in the
    // development campaign.  It is deliberately shared by every treatment:
    // scenario context remains the only contextual difference between TGSLR,
    // FTMR, and FCR.
    public const string CurrentContractVersion = "RQ2-TREATMENT-CONTRACT-2";

    public string ContractVersion { get; init; } = "";
    public string ContextPolicy { get; init; } = "";
    public string EditScope { get; init; } = "";
    public bool ScenarioIncluded { get; init; }
    public bool TraceabilityIncluded { get; init; }
    public bool FocalMethodIncluded { get; init; }
    public bool FullTestClassIncluded { get; init; }
    public bool ToolEvidenceIncluded { get; init; }
    public bool ProductionContextIncluded { get; init; }
    public bool ForbiddenContextDetected { get; init; }
    public bool ContractSatisfied { get; init; }
    public bool InferredFromLegacy { get; init; }
    public string PromptSha256 { get; init; } = "";
    public string InstructionsSha256 { get; init; } = "";
    public string ToolEvidenceSha256 { get; init; } = "";
    public string TargetSourceSha256 { get; init; } = "";
    public string ProductionContextSha256 { get; init; } = "";
    public string AuditArtifact { get; init; } = "";
    public string Violation { get; init; } = "";
}

public enum RepairStageStatus
{
    NotRun,
    Succeeded,
    Failed,
    Skipped
}

public sealed record RepairStageOutcome(
    RepairStageStatus Status = RepairStageStatus.NotRun,
    string Detail = "")
{
    public static RepairStageOutcome Success(string detail = "") => new(RepairStageStatus.Succeeded, detail);
    public static RepairStageOutcome Failure(string detail) => new(RepairStageStatus.Failed, detail);
    public static RepairStageOutcome Skip(string detail) => new(RepairStageStatus.Skipped, detail);
}

public sealed record RepairStageEvidence
{
    public RepairStageOutcome LlmOutput { get; init; } = new();
    public RepairStageOutcome SourceApplication { get; init; } = new();
    public RepairStageOutcome Compilation { get; init; } = new();
    public RepairStageOutcome FocalTest { get; init; } = new();
    public RepairStageOutcome BuggyOracle { get; init; } = new();
    public RepairStageOutcome Regression { get; init; } = new();
    public RepairStageOutcome Coverage { get; init; } = new();
    public RepairStageOutcome Mutation { get; init; } = new();
    public bool InferredFromLegacy { get; init; }

    [JsonIgnore]
    public string FailedStage => new (string Name, RepairStageOutcome Outcome)[]
        {
            ("LLM output", LlmOutput),
            ("Source application", SourceApplication),
            ("Compilation", Compilation),
            ("Focal test", FocalTest),
            ("Buggy oracle", BuggyOracle),
            ("Regression", Regression),
            ("Coverage", Coverage),
            ("Mutation", Mutation)
        }
        .FirstOrDefault(item => item.Outcome.Status == RepairStageStatus.Failed).Name ?? "";
}

public sealed record RepairIterationResult
{
    public TokenUsage Tokens { get; init; } = new();
    public ApiUsage Api { get; init; } = new();
    public RepairVerification Verification { get; init; } = new();
    public QualityMeasurement Quality { get; init; } = new();
    public LlmOutputEvidence Output { get; init; } = new();
    public PromptAuditEvidence PromptAudit { get; init; } = new();
    public RepairStageEvidence Stages { get; init; } = new();
    public string Summary { get; init; } = string.Empty;

    [JsonIgnore] public int InputTokens => Tokens.InputTokens;
    [JsonIgnore] public int OutputTokens => Tokens.OutputTokens;
    [JsonIgnore] public int TotalTokens => Tokens.TotalTokens;
    [JsonIgnore] public int CachedInputTokens => Tokens.CachedInputTokens;
    [JsonIgnore] public int CacheWriteInputTokens => Tokens.CacheWriteInputTokens;
    [JsonIgnore] public int ReasoningTokens => Tokens.ReasoningTokens;
    [JsonIgnore] public bool TokenUsageReported => Tokens.UsageReported;
    [JsonIgnore] public int ApiCalls => Api.Calls;
    [JsonIgnore] public int ApiRetryCount => Api.Retries;
    [JsonIgnore] public int KeyFailoverCount => Api.KeyFailovers;
    [JsonIgnore] public string ApiKeyFingerprint => Api.LastKeyFingerprint;
    [JsonIgnore] public int ChangedLines => Verification.ChangedLines;
    [JsonIgnore] public int TouchedMethods => Verification.TouchedMethods;
    [JsonIgnore] public int RegressedTests => Verification.RegressedTests;
    [JsonIgnore] public bool FocalTestPassed => Verification.FocalTestPassed;
    [JsonIgnore] public bool AdequacyVerified => Verification.AdequacyVerified;
    [JsonIgnore] public bool BuggyOracleRequired => Verification.BuggyOracleRequired;
    [JsonIgnore] public bool BugReproduced => Verification.BugReproduced;
    [JsonIgnore] public double? LineCoverage => Quality.LineCoverage;
    [JsonIgnore] public double? MutationScore => Quality.MutationScore;
    [JsonIgnore] public MeasurementStatus CoverageStatus => Quality.CoverageStatus;
    [JsonIgnore] public MeasurementStatus MutationStatus => Quality.MutationStatus;
    [JsonIgnore] public string CoverageDetail => Quality.CoverageDetail;
    [JsonIgnore] public string MutationDetail => Quality.MutationDetail;
    [JsonIgnore] public string CoverageReportPath => Quality.CoverageReportPath;
    [JsonIgnore] public string MutationReportPath => Quality.MutationReportPath;
    [JsonIgnore] public bool FirstPassJsonValid => Output.FirstPassJsonValid;
    [JsonIgnore] public string OutputNormalization => Output.Normalization;
}

public sealed record IterationRecord(
    int Iteration,
    DateTimeOffset StartedAt,
    RepairIterationResult Result);

public sealed record RunIdentity(
    Guid RunId,
    string TaskId,
    string ScenarioId,
    RepairStrategy Strategy,
    int Repetition);

public sealed record RunProvenance(
    string BatchId = "",
    string ProviderName = "",
    string ModelName = "",
    string ApiProtocol = "",
    int ConfiguredTokenBudget = 0,
    int ConfiguredMaxIterations = 0,
    string ProjectId = "",
    string DatasetId = "",
    string ArtifactId = "",
    int SourceSchemaVersion = 11,
    string IndependenceUnitId = "",
    string SamplingStratum = "",
    string RunPlanFingerprint = "");

public sealed record ExperimentDesignMetadata(
    int ScheduleSeed = 0,
    int ScheduleIndex = 0,
    string BlockId = "",
    int TreatmentPosition = 0,
    int ExpectedBatchRuns = 0,
    StudyPlanEvidence? StudyPlan = null);

public sealed record ExecutionTiming(
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt);

public sealed record ExperimentRunResult
{
    public int SchemaVersion { get; init; } = 11;
    public RunIdentity Identity { get; init; } = new(Guid.Empty, "", "", default, 0);
    public RunProvenance Provenance { get; init; } = new();
    public ExperimentDesignMetadata Design { get; init; } = new();
    public ExecutionTiming Timing { get; init; } = new(default, default);
    public TokenUsage Tokens { get; init; } = new();
    public ApiUsage Api { get; init; } = new();
    public RepairVerification Verification { get; init; } = new();
    public QualityMeasurement Quality { get; init; } = new();
    public RepairStageEvidence Stages { get; init; } = new();
    public PromptAuditEvidence PromptAudit { get; init; } = new();
    public RunStatus Status { get; init; }
    public RunFailureCategory FailureCategory { get; init; }
    public IReadOnlyList<IterationRecord> Iterations { get; init; } = [];
    public string StopReason { get; init; } = string.Empty;

    [JsonIgnore] public Guid RunId => Identity.RunId;
    [JsonIgnore] public string TaskId => Identity.TaskId;
    [JsonIgnore] public string ScenarioId => Identity.ScenarioId;
    [JsonIgnore] public RepairStrategy Strategy => Identity.Strategy;
    [JsonIgnore] public int Repetition => Identity.Repetition;
    [JsonIgnore] public DateTimeOffset StartedAt => Timing.StartedAt;
    [JsonIgnore] public DateTimeOffset CompletedAt => Timing.CompletedAt;
    [JsonIgnore] public int TotalInputTokens => Tokens.InputTokens;
    [JsonIgnore] public int TotalOutputTokens => Tokens.OutputTokens;
    [JsonIgnore] public int TotalTokens => Tokens.TotalTokens;
    [JsonIgnore] public int TotalCachedInputTokens => Tokens.CachedInputTokens;
    [JsonIgnore] public int TotalCacheWriteInputTokens => Tokens.CacheWriteInputTokens;
    [JsonIgnore] public int TotalReasoningTokens => Tokens.ReasoningTokens;
    [JsonIgnore] public int UnreportedUsageResponses => Tokens.UnreportedUsageResponses;
    [JsonIgnore] public bool TokenAccountingComplete => Tokens.AccountingComplete;
    [JsonIgnore] public int TotalApiCalls => Api.Calls;
    [JsonIgnore] public int TotalApiRetries => Api.Retries;
    [JsonIgnore] public int TotalKeyFailovers => Api.KeyFailovers;
    [JsonIgnore] public string LastApiKeyFingerprint => Api.LastKeyFingerprint;
    [JsonIgnore] public int TotalChangedLines => Verification.ChangedLines;
    [JsonIgnore] public int TotalTouchedMethods => Verification.TouchedMethods;
    [JsonIgnore] public int RegressedTests => Verification.RegressedTests;
    [JsonIgnore] public bool BuggyOracleRequired => Verification.BuggyOracleRequired;
    [JsonIgnore] public bool FinalBugReproduced => Verification.BugReproduced;
    [JsonIgnore] public double? FinalLineCoverage => Quality.LineCoverage;
    [JsonIgnore] public double? FinalMutationScore => Quality.MutationScore;
    [JsonIgnore] public MeasurementStatus FinalCoverageStatus => Quality.CoverageStatus;
    [JsonIgnore] public MeasurementStatus FinalMutationStatus => Quality.MutationStatus;
    [JsonIgnore] public string FinalCoverageDetail => Quality.CoverageDetail;
    [JsonIgnore] public string FinalMutationDetail => Quality.MutationDetail;
    [JsonIgnore] public string FinalCoverageReportPath => Quality.CoverageReportPath;
    [JsonIgnore] public string FinalMutationReportPath => Quality.MutationReportPath;
    [JsonIgnore] public string BatchId => Provenance.BatchId;
    [JsonIgnore] public string ProviderName => Provenance.ProviderName;
    [JsonIgnore] public string ModelName => Provenance.ModelName;
    [JsonIgnore] public string ApiProtocol => Provenance.ApiProtocol;
    [JsonIgnore] public int ConfiguredTokenBudget => Provenance.ConfiguredTokenBudget;
    [JsonIgnore] public int ConfiguredMaxIterations => Provenance.ConfiguredMaxIterations;
    [JsonIgnore] public string ProjectId => Provenance.ProjectId;
    [JsonIgnore] public string DatasetId => Provenance.DatasetId;
    [JsonIgnore] public string ArtifactId => Provenance.ArtifactId;
    [JsonIgnore] public string IndependenceUnitId => string.IsNullOrWhiteSpace(Provenance.IndependenceUnitId)
        ? TaskId
        : Provenance.IndependenceUnitId;
    [JsonIgnore] public string SamplingStratum => Provenance.SamplingStratum;
    [JsonIgnore] public string RunPlanFingerprint => Provenance.RunPlanFingerprint;
    [JsonIgnore] public int SourceSchemaVersion => Provenance.SourceSchemaVersion;
    [JsonIgnore] public int ScheduleSeed => Design.ScheduleSeed;
    [JsonIgnore] public int ScheduleIndex => Design.ScheduleIndex;
    [JsonIgnore] public string BlockId => Design.BlockId;
    [JsonIgnore] public int TreatmentPosition => Design.TreatmentPosition;
    [JsonIgnore] public int ExpectedBatchRuns => Design.ExpectedBatchRuns;
    [JsonIgnore] public StudyPlanEvidence StudyPlan => Design.StudyPlan ?? new();
}

public sealed record ExperimentProgress(
    Guid RunId,
    string TaskId,
    string ScenarioId,
    RepairStrategy Strategy,
    int Repetition,
    RunStatus Status,
    int Iteration,
    int TotalTokens,
    int ChangedLines,
    int RegressedTests,
    string Message);
