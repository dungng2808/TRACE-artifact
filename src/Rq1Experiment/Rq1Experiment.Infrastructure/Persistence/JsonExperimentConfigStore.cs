using Rq1Experiment.Contracts.Config;
using Rq1Experiment.Core.Abstractions;

namespace Rq1Experiment.Infrastructure.Persistence;

public sealed class JsonExperimentConfigStore(WorkspaceLayout layout) : IExperimentConfigStore
{
    public async Task<ExperimentConfig> LoadAsync(CancellationToken cancellationToken = default) =>
        await JsonSupport.ReadAsync<ExperimentConfig>(layout.ExperimentConfigPath, cancellationToken)
        ?? new ExperimentConfig();

    public async Task SaveAsync(ExperimentConfig config, CancellationToken cancellationToken = default)
    {
        ValidateId(config.ExperimentId);
        await JsonSupport.WriteAsync(layout.ExperimentConfigPath, config, cancellationToken);
        await JsonSupport.WriteAsync(HistoryPath(config.ExperimentId), config, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ListExperimentIdsAsync(CancellationToken cancellationToken = default)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var active = await LoadAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(active.ExperimentId)) ids.Add(active.ExperimentId);
        if (Directory.Exists(layout.ExperimentConfigHistoryDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(layout.ExperimentConfigHistoryDirectory, "*.json"))
            {
                var config = await JsonSupport.ReadAsync<ExperimentConfig>(path, cancellationToken);
                if (config is not null && !string.IsNullOrWhiteSpace(config.ExperimentId)) ids.Add(config.ExperimentId);
            }
        }
        return ids.OrderByDescending(value => value, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<ExperimentConfig?> LoadExperimentAsync(
        string experimentId, CancellationToken cancellationToken = default)
    {
        ValidateId(experimentId);
        var historical = await JsonSupport.ReadAsync<ExperimentConfig>(HistoryPath(experimentId), cancellationToken);
        if (historical is not null) return historical;
        var active = await LoadAsync(cancellationToken);
        return active.ExperimentId.Equals(experimentId, StringComparison.OrdinalIgnoreCase) ? active : null;
    }

    public async Task DeleteExperimentAsync(
        string experimentId, CancellationToken cancellationToken = default)
    {
        ValidateId(experimentId);
        var historyPath = HistoryPath(experimentId);
        if (File.Exists(historyPath)) File.Delete(historyPath);

        var active = await JsonSupport.ReadAsync<ExperimentConfig>(
            layout.ExperimentConfigPath, cancellationToken);
        if (active?.ExperimentId.Equals(experimentId, StringComparison.OrdinalIgnoreCase) == true
            && File.Exists(layout.ExperimentConfigPath))
            File.Delete(layout.ExperimentConfigPath);
    }

    private string HistoryPath(string experimentId) =>
        Path.Combine(layout.ExperimentConfigHistoryDirectory, experimentId + ".json");

    private static void ValidateId(string experimentId)
    {
        if (string.IsNullOrWhiteSpace(experimentId)
            || experimentId.Any(character => !(char.IsLetterOrDigit(character) || character is '-' or '_' or '.')))
            throw new ArgumentException("Experiment ID may contain only letters, digits, '.', '_' and '-'.", nameof(experimentId));
    }
}
