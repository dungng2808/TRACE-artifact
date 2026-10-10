using System.Text.Json;
using System.Text.Json.Nodes;

namespace RQ2Experiment.Infrastructure.Benchmark;

/// <summary>Filters a candidate manifest using the CSV emitted by benchmark preflight.</summary>
public static class ReadyManifestFilter
{
    public static async Task<ReadyManifestFilterResult> FilterAsync(
        string candidateManifestPath,
        string readinessCsvPath,
        string outputManifestPath,
        CancellationToken cancellationToken)
    {
        var readyIds = (await File.ReadAllLinesAsync(Path.GetFullPath(readinessCsvPath), cancellationToken))
            .Skip(1)
            .Select(line => line.Split(','))
            .Where(columns => columns.Length >= 11 && columns[10].Equals("TRUE", StringComparison.OrdinalIgnoreCase))
            .Select(columns => columns[0])
            .ToHashSet(StringComparer.Ordinal);
        if (readyIds.Count == 0)
        {
            throw new InvalidOperationException("Preflight did not mark any task as READY.");
        }

        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.GetFullPath(candidateManifestPath), cancellationToken)) as JsonObject
            ?? throw new InvalidOperationException("Candidate manifest is not a JSON object.");
        var tasks = manifest["tasks"]?.AsArray()
            ?? throw new InvalidOperationException("Candidate manifest has no tasks array.");
        var readyTasks = tasks
            .Where(task => task?["taskId"]?.GetValue<string>() is { } id && readyIds.Contains(id))
            .Select(task => task!.DeepClone())
            .ToArray();
        if (readyTasks.Length != readyIds.Count)
        {
            throw new InvalidOperationException("Readiness report contains task IDs that are absent from the candidate manifest.");
        }

        manifest["tasks"] = new JsonArray(readyTasks);
        var outputPath = Path.GetFullPath(outputManifestPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(
            outputPath,
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
        return new ReadyManifestFilterResult(outputPath, tasks.Count, readyTasks.Length);
    }
}

public sealed record ReadyManifestFilterResult(string OutputManifestPath, int CandidateTasks, int ReadyTasks);
