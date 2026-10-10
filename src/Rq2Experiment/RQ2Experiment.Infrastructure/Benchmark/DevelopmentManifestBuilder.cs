using System.Text.Json;
using System.Text.Json.Nodes;

namespace RQ2Experiment.Infrastructure.Benchmark;

/// <summary>
/// Produces a development-only task manifest that excludes every task already
/// present in a held-out evaluation manifest.  The output is intentionally a
/// candidate: it must pass normal benchmark preflight before it can be run.
/// </summary>
public static class DevelopmentManifestBuilder
{
    public static async Task<DevelopmentManifestBuild> BuildAsync(
        string candidateManifestPath,
        string heldOutManifestPath,
        string outputManifestPath,
        int? maximumTasks,
        CancellationToken cancellationToken)
    {
        if (maximumTasks is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumTasks));
        }

        var candidate = await LoadObjectAsync(candidateManifestPath, cancellationToken);
        var heldOut = await LoadObjectAsync(heldOutManifestPath, cancellationToken);
        var candidateTasks = candidate["tasks"]?.AsArray()
            ?? throw new InvalidOperationException("Candidate manifest has no tasks array.");
        var heldOutTasks = heldOut["tasks"]?.AsArray()
            ?? throw new InvalidOperationException("Held-out manifest has no tasks array.");

        var heldOutIds = heldOutTasks
            .Select(TaskId)
            .ToHashSet(StringComparer.Ordinal);
        var candidateDirectory = Path.GetDirectoryName(Path.GetFullPath(candidateManifestPath))!;
        var disjoint = candidateTasks
            .Where(task => task is not null && !heldOutIds.Contains(TaskId(task)))
            .ToArray();
        var structurallyRunnable = disjoint
            .Where(task => IsStructurallyRunnable(task!, candidateDirectory))
            .OrderBy(TaskId, StringComparer.Ordinal)
            .Take(maximumTasks ?? int.MaxValue)
            .Select(task => task!.DeepClone())
            .ToArray();
        if (structurallyRunnable.Length == 0)
        {
            throw new InvalidOperationException(
                "No structurally runnable disjoint candidates remain after excluding the held-out manifest.");
        }

        candidate["tasks"] = new JsonArray(structurallyRunnable);
        var design = candidate["studyDesign"]?.AsObject();
        if (design is not null)
        {
            design["studyId"] = "RQ2-DEVELOPMENT-DISJOINT";
            design["selectionMethod"] =
                "Development-only tasks deterministically excluded from the held-out evaluation manifest; preflight required.";
            if (design["powerPlan"] is JsonObject powerPlan)
            {
                powerPlan["plannedIndependentUnits"] = structurallyRunnable.Length;
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
            candidate.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);

        return new DevelopmentManifestBuild(
            outputPath,
            candidateTasks.Count,
            heldOutIds.Count,
            structurallyRunnable.Length,
            disjoint.Length - structurallyRunnable.Length);
    }

    private static async Task<JsonObject> LoadObjectAsync(string path, CancellationToken cancellationToken)
    {
        var document = JsonNode.Parse(await File.ReadAllTextAsync(Path.GetFullPath(path), cancellationToken)) as JsonObject;
        return document ?? throw new InvalidOperationException($"Manifest is not a JSON object: {path}");
    }

    private static string TaskId(JsonNode? task) =>
        task?["taskId"]?.GetValue<string>()
        ?? throw new InvalidOperationException("Manifest task has no taskId.");

    private static bool IsStructurallyRunnable(JsonNode task, string manifestDirectory)
    {
        var repository = Resolve(manifestDirectory, task["repositoryPath"]?.GetValue<string>());
        var buggyRepository = Resolve(manifestDirectory, task["buggyRepositoryPath"]?.GetValue<string>());
        var module = task["modulePath"]?.GetValue<string>() ?? string.Empty;
        var testFile = task["testFilePath"]?.GetValue<string>() ?? string.Empty;
        var buildSystem = task["buildSystem"]?.GetValue<string>() ?? string.Empty;
        if (!Directory.Exists(repository) || !Directory.Exists(buggyRepository)) return false;

        var moduleDirectory = Resolve(repository, module);
        var buggyModuleDirectory = Resolve(buggyRepository, module);
        if (!Directory.Exists(moduleDirectory) || !Directory.Exists(buggyModuleDirectory) ||
            !File.Exists(Resolve(repository, testFile)) || !File.Exists(Resolve(buggyRepository, testFile)))
        {
            return false;
        }

        var isMaven = buildSystem.Equals("maven", StringComparison.OrdinalIgnoreCase);
        var hasBuildFile = isMaven
            ? File.Exists(Path.Combine(moduleDirectory, "pom.xml")) && File.Exists(Path.Combine(buggyModuleDirectory, "pom.xml"))
            : (File.Exists(Path.Combine(moduleDirectory, "build.gradle")) || File.Exists(Path.Combine(moduleDirectory, "build.gradle.kts"))) &&
              (File.Exists(Path.Combine(buggyModuleDirectory, "build.gradle")) || File.Exists(Path.Combine(buggyModuleDirectory, "build.gradle.kts")));
        return hasBuildFile;
    }

    private static string Resolve(string root, string? path) =>
        Path.IsPathRooted(path ?? string.Empty)
            ? Path.GetFullPath(path!)
            : Path.GetFullPath(Path.Combine(root, path ?? string.Empty));
}

public sealed record DevelopmentManifestBuild(
    string OutputManifestPath,
    int CandidateTasks,
    int HeldOutTasks,
    int DevelopmentCandidates,
    int StructurallyExcludedCandidates);
