namespace Rq1Experiment.Core.Abstractions;

public sealed record FailedRunSummary(
    string SubjectId,
    string Approach,
    int Repetition,
    string Status,
    string? FailureCode,
    string? FailureStage,
    string RunDirectory);

public sealed record ResetRunsRequest(
    string ExperimentOutputRoot,
    string ExperimentId,
    IReadOnlyList<string>? SubjectIds = null,
    IReadOnlyList<string>? Approaches = null,
    bool OnlyTokenFailures = false);

public sealed record ResetRunsReport(
    string ExperimentId,
    int TotalRunsScanned,
    int ResetRunCount,
    int RetainedRunCount,
    IReadOnlyList<string> ResetRunIds);

public interface IRunManagementService
{
    Task<IReadOnlyList<FailedRunSummary>> GetFailedRunsAsync(
        string experimentOutputRoot,
        string experimentId,
        CancellationToken cancellationToken = default);

    Task<ResetRunsReport> ResetFailedRunsAsync(
        ResetRunsRequest request,
        CancellationToken cancellationToken = default);

    Task<bool> ResetSpecificRunAsync(
        string runDirectory,
        CancellationToken cancellationToken = default);
}
