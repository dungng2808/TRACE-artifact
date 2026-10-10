using RQ2Experiment.Core.Domain;

namespace RQ2Experiment.Core.Abstractions;

public interface IRunRepository
{
    Task SaveAsync(ExperimentRunResult result, CancellationToken cancellationToken);
}
