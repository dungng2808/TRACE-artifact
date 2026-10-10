namespace Rq1Experiment.Contracts.Experiments;

public enum PipelineStageState
{
    Pending,
    Running,
    Passed,
    Failed,
    WaitingForApproval,
    WaitingForApiKey,
    Skipped,
    Cancelled
}

public sealed record PipelineStageDefinition(string Id, string DisplayName);

public sealed record PipelineProgress(
    string StageId,
    string StageName,
    PipelineStageState State,
    int CompletedStages,
    int TotalStages,
    string Message,
    DateTimeOffset Timestamp);

public sealed record PipelineRunResult(
    bool IsCompleted,
    bool IsCancelled,
    string Message,
    DateTimeOffset FinishedAt);
