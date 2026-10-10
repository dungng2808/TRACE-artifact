namespace Rq1Experiment.Contracts.Generation;

public class DirectGenerationOutput
{
    public string TestClassName { get; set; } = string.Empty;
    public string SourceCode { get; set; } = string.Empty;
    public List<string> DeclaredTestMethods { get; set; } = [];
}

public sealed record DirectBaselineRequest(
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

public sealed class DirectRunRecord
{
    public string PipelineContractVersion { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string SubjectId { get; set; } = string.Empty;
    public string Split { get; set; } = string.Empty;
    public int Repetition { get; set; }
    public string Approach { get; set; } = "DIRECT";
    public string Status { get; set; } = string.Empty;
    public string? FailureCode { get; set; }
    public string ContextHash { get; set; } = string.Empty;
    public string SystemTemplateHash { get; set; } = string.Empty;
    public string UserTemplateHash { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public string BaseEndpoint { get; set; } = string.Empty;
    public string RequestedModelId { get; set; } = string.Empty;
    public string? ResponseModelId { get; set; }
    public int SemanticLlmCalls { get; set; }
    public int TransportAttempts { get; set; }
    public List<string> KeySlots { get; set; } = [];
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int? TotalTokens { get; set; }
    public string? FinishReason { get; set; }
    public int? HttpStatusCode { get; set; }
    public int DeclaredTestMethodCount { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
}

public sealed class DirectBaselineReport
{
    public string Mode { get; set; } = string.Empty;
    public int SelectedSubjectCount { get; set; }
    public int ExpectedRunCount { get; set; }
    public int CompletedRunCount { get; set; }
    public int ValidOutputCount { get; set; }
    public int InvalidOutputCount { get; set; }
    public int WaitingForApiKeyCount { get; set; }
    public int SemanticLlmCallCount { get; set; }
    public int PlannerCallCount { get; set; }
    public int RepairCallCount { get; set; }
    public int TokenCeilingViolationCount { get; set; }
    public bool NoHiddenRetryInvariant { get; set; }
    public bool NoPlannerOrRepairTrace { get; set; }
    public bool Complete { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }
}
