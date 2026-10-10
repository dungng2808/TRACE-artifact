using System.Text.Json;
using System.Text.Json.Nodes;

namespace RQ2Experiment.Infrastructure.Benchmark;

/// <summary>
/// Labels a previously used READY manifest as a post-development evaluation.
/// This preserves its tasks while making the non-confirmatory status explicit
/// in provenance, rather than silently presenting a reused task set as held out.
/// </summary>
public static class PostDevelopmentEvaluationManifestBuilder
{
    public static async Task<string> BuildAsync(
        string sourceManifestPath,
        string outputManifestPath,
        CancellationToken cancellationToken)
    {
        var sourcePath = Path.GetFullPath(sourceManifestPath);
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(sourcePath, cancellationToken)) as JsonObject
            ?? throw new InvalidOperationException("Source manifest is not a JSON object.");
        var taskCount = manifest["tasks"]?.AsArray().Count
            ?? throw new InvalidOperationException("Source manifest has no tasks array.");
        if (taskCount == 0) throw new InvalidOperationException("Source manifest has no tasks.");

        var design = manifest["studyDesign"]?.AsObject();
        if (design is not null)
        {
            design["studyId"] = "RQ2-POSTDEVELOPMENT-EVALUATION";
            design["selectionMethod"] =
                "Previously observed READY task set reused only for post-development exploratory evaluation; not an independent confirmatory holdout.";
            if (design["powerPlan"] is JsonObject powerPlan)
            {
                powerPlan["plannedIndependentUnits"] = taskCount;
                powerPlan["provisional"] = true;
            }
            if (design["outcomePlan"] is JsonObject outcomePlan)
            {
                outcomePlan["provisional"] = true;
            }
        }

        var outputPath = Path.GetFullPath(outputManifestPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(
            outputPath,
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
        return outputPath;
    }
}
