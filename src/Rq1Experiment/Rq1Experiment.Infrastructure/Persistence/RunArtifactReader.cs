using System.Text.Json;
using Rq1Experiment.Core.Abstractions;

namespace Rq1Experiment.Infrastructure.Persistence;

public sealed class RunArtifactReader : IRunArtifactReader
{
    public async Task<IReadOnlyList<RunArtifactSummary>> ReadAsync(
        string experimentOutputRoot,
        CancellationToken cancellationToken = default)
    {
        var runsRoot = Path.Combine(experimentOutputRoot, "runs");
        if (!Directory.Exists(runsRoot))
            return [];

        var rows = new List<RunArtifactSummary>();
        foreach (var path in Directory.EnumerateFiles(runsRoot, "run.json", SearchOption.AllDirectories).Order())
        {
            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            rows.Add(new RunArtifactSummary(
                Get(root, "experimentId"),
                Get(root, "kind"),
                Get(root, "providerId"),
                Get(root, "requestedModelId"),
                path));
        }
        return rows;
    }

    private static string Get(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) ? value.ToString() : string.Empty;
}
