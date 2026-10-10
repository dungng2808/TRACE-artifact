namespace Rq1Experiment.Contracts.Generation;

public sealed class ScenarioPlan
{
    public string SubjectId { get; set; } = string.Empty;
    public List<AtomicScenario> Scenarios { get; set; } = [];
}

public sealed class AtomicScenario
{
    public string ScenarioId { get; set; } = string.Empty;
    public string Technique { get; set; } = string.Empty;
    public List<string> Preconditions { get; set; } = [];
    public string ConcreteInput { get; set; } = string.Empty;
    public string ExpectedObservation { get; set; } = string.Empty;
    public OracleProvenance OracleProvenance { get; set; } = new();
    public List<string> TargetIds { get; set; } = [];
    public List<string> CfgPathNodeIds { get; set; } = [];
}

public sealed class OracleProvenance
{
    public string Type { get; set; } = string.Empty;
    public List<int> SourceLines { get; set; } = [];
}

public sealed class PlannedGenerationOutput : DirectGenerationOutput
{
    public List<ScenarioTestMapping> Mappings { get; set; } = [];
}

public sealed class ScenarioTestMapping
{
    public string ScenarioId { get; set; } = string.Empty;
    public string TestClass { get; set; } = string.Empty;
    public string TestMethod { get; set; } = string.Empty;
}

public sealed record PlannedPipelineRequest(
    string ExperimentId,
    string Mode,
    string ExperimentOutputRoot,
    string ProviderId,
    string BaseEndpoint,
    string RequestedModelId,
    double Temperature,
    int TimeoutSeconds,
    int MaxTestMethods,
    int TotalTokenCeilingPerRun,
    int Repetitions,
    int TransportRetryCount = 2,
    int MaxOutputTokensPerCall = 65_536,
    string DeepSeekThinkingMode = "enabled",
    int MaxParallelLlmCalls = 1);

public sealed class PlannedRunRecord
{
    public string PipelineContractVersion { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string SubjectId { get; set; } = string.Empty;
    public string Split { get; set; } = string.Empty;
    public int Repetition { get; set; }
    public string Approach { get; set; } = "PLANNED";
    public string Status { get; set; } = string.Empty;
    public string? FailureStage { get; set; }
    public string? FailureCode { get; set; }
    public string ContextHash { get; set; } = string.Empty;
    public string TargetInventoryHash { get; set; } = string.Empty;
    public string PlannerRequestHash { get; set; } = string.Empty;
    public string GeneratorRequestHash { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public string BaseEndpoint { get; set; } = string.Empty;
    public string RequestedModelId { get; set; } = string.Empty;
    public string? PlannerResponseModelId { get; set; }
    public string? GeneratorResponseModelId { get; set; }
    public int PlannerSemanticCalls { get; set; }
    public int GeneratorSemanticCalls { get; set; }
    public int RepairCalls { get; set; }
    public int ReviewerCalls { get; set; }
    public int TransportAttempts { get; set; }
    public List<string> KeySlots { get; set; } = [];
    public int? PlannerInputTokens { get; set; }
    public int? PlannerOutputTokens { get; set; }
    public int? PlannerTotalTokens { get; set; }
    public int? GeneratorInputTokens { get; set; }
    public int? GeneratorOutputTokens { get; set; }
    public int? GeneratorTotalTokens { get; set; }
    public int? TotalTokens { get; set; }
    public int ScenarioCount { get; set; }
    public int TestMethodCount { get; set; }
    public bool BijectionValid { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
}

public sealed class PlannedPipelineReport
{
    public string Mode { get; set; } = string.Empty;
    public int SelectedSubjectCount { get; set; }
    public int ExpectedRunCount { get; set; }
    public int CompletedRunCount { get; set; }
    public int ValidOutputCount { get; set; }
    public int InvalidPlannerCount { get; set; }
    public int InvalidGeneratorCount { get; set; }
    public int WaitingForApiKeyCount { get; set; }
    public int PlannerSemanticCallCount { get; set; }
    public int GeneratorSemanticCallCount { get; set; }
    public int RepairCallCount { get; set; }
    public int ReviewerCallCount { get; set; }
    public int ContractViolationCount { get; set; }
    public bool NoHiddenRetryInvariant { get; set; }
    public bool NoRepairOrReviewerTrace { get; set; }
    public bool Complete { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }
}
