using RQ2Experiment.Core.Domain;

namespace RQ2Experiment.Core.Abstractions;

public interface IRepairExecutor
{
    Task<IRepairSession> CreateSessionAsync(
        Guid runId,
        ExperimentTask task,
        RepairStrategy strategy,
        int repetition,
        ExperimentConfiguration configuration,
        CancellationToken cancellationToken);
}

public interface IRepairSession : IAsyncDisposable
{
    Task<RepairIterationResult> ExecuteIterationAsync(
        int iteration,
        CancellationToken cancellationToken);
}
