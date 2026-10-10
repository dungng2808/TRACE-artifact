namespace Rq1Experiment.Contracts.Experiments;

public sealed record MetricsLedgerRequest(
    string ExperimentId,
    string Mode,
    string ExperimentOutputRoot,
    string WorkspaceRoot,
    string ConfigSnapshotPath);

public sealed class RunMetricKey
{
    public string ExperimentId { get; set; } = string.Empty;
    public string SubjectId { get; set; } = string.Empty;
    public string Approach { get; set; } = string.Empty;
    public int Repetition { get; set; }

    public override string ToString() => $"{ExperimentId}|{SubjectId}|{Approach}|{Repetition}";
}

public sealed class MutationMetric
{
    public int Killed { get; set; }
    public int Survived { get; set; }
    public int NoCoverage { get; set; }
    public int Excluded { get; set; }
    public int CommonViableMutantCount { get; set; }
    public double? Score { get; set; }
}

public sealed class RunCostMetric
{
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int? TotalTokens { get; set; }
    public int SemanticLlmCalls { get; set; }
    public int TransportAttempts { get; set; }
    public double WallClockSeconds { get; set; }
}

public sealed class RunMetricsRecord
{
    public RunMetricKey Key { get; set; } = new();
    public string ProjectId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool? CompileSuccess { get; set; }
    public bool? ExecutionSuccess { get; set; }
    public double? EndToEndTargetSatisfaction { get; set; }
    public double? EndToEndBranchCoverage { get; set; }
    public double? EndToEndMutationScore { get; set; }
    public double? PlanFidelity { get; set; }
    public double? PlanObligationCoverage { get; set; }
    public double? ScenarioValidity { get; set; }
    public double? PassRate { get; set; }
    public double? ConditionalTargetSatisfaction { get; set; }
    public double? ConditionalBranchCoverage { get; set; }
    public double? ConditionalMutationScore { get; set; }
    public int SatisfiedTargetCount { get; set; }
    public int TargetCount { get; set; }
    public int GeneratedTestCount { get; set; }
    public int? ScenarioCount { get; set; }
    public bool? MappingValid { get; set; }
    public MutationMetric Mutation { get; set; } = new();
    public RunCostMetric Cost { get; set; } = new();
    public string ProviderId { get; set; } = string.Empty;
    public string RequestedModelId { get; set; } = string.Empty;
    public string ResponseModelId { get; set; } = string.Empty;
    public string PromptHash { get; set; } = string.Empty;
    public string ContextHash { get; set; } = string.Empty;
    public string ConfigHash { get; set; } = string.Empty;
    public string SourceHash { get; set; } = string.Empty;
    public string TargetHash { get; set; } = string.Empty;
    public string RunDirectory { get; set; } = string.Empty;
    public List<string> ValidationErrors { get; set; } = [];
}

public sealed class ApproachAggregate
{
    public string Approach { get; set; } = string.Empty;
    public int RunCount { get; set; }
    public double CompilationSuccessRate { get; set; }
    public double ExecutionSuccessRate { get; set; }
    public double? MeanEndToEndTargetSatisfaction { get; set; }
    public double? MeanEndToEndBranchCoverage { get; set; }
    public double? MeanEndToEndMutationScore { get; set; }
    public double? MeanPlanFidelity { get; set; }
    public double? MeanPlanObligationCoverage { get; set; }
    public double? MeanScenarioValidity { get; set; }
    public double? MeanPassRate { get; set; }
    public long? TotalInputTokens { get; set; }
    public long? TotalOutputTokens { get; set; }
    public long? TotalReportedTokens { get; set; }
    public int TokenSampleCount { get; set; }
    public int ToolErrorCount { get; set; }
}

public sealed class MetricsLedgerReport
{
    public string ExperimentId { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
    public int RunCount { get; set; }
    public int DirectRunCount { get; set; }
    public int PlannedRunCount { get; set; }
    public int InvalidRowCount { get; set; }
    public int DuplicateKeyCount { get; set; }
    public int FailedRunRetainedCount { get; set; }
    public int ToolErrorCount { get; set; }
    public bool UniqueKeys { get; set; }
    public bool FailedRunsRetained { get; set; }
    public bool NoMissingRequiredHashes { get; set; }
    public bool FailureScoringValid { get; set; }
    public bool TreatmentContractsValid { get; set; }
    public bool RebuildIndependentOfUiState { get; set; }
    public bool Complete { get; set; }
    public string RunsCsvPath { get; set; } = string.Empty;
    public string MetricsJsonlPath { get; set; } = string.Empty;
    public string DataQualityReportPath { get; set; } = string.Empty;
    public string LedgerSha256 { get; set; } = string.Empty;
    public List<ApproachAggregate> Approaches { get; set; } = [];
    public List<string> Errors { get; set; } = [];
    public DateTimeOffset GeneratedAt { get; set; }
}
