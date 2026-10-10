using System.Text.Json;
using RQ2Experiment.Core.Domain;
using RQ2Experiment.Infrastructure.Execution;

namespace RQ2Experiment.Infrastructure.Benchmark;

public sealed record LoadedBenchmarkManifest(
    IReadOnlyList<ExperimentTask> Tasks,
    BenchmarkStudyDesign StudyDesign);

public static class BenchmarkManifestLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<IReadOnlyList<ExperimentTask>> LoadAsync(
        string manifestPath,
        string? repositoryOverride,
        CancellationToken cancellationToken) =>
        (await LoadDefinitionAsync(manifestPath, repositoryOverride, cancellationToken)).Tasks;

    public static async Task<LoadedBenchmarkManifest> LoadDefinitionAsync(
        string manifestPath,
        string? repositoryOverride,
        CancellationToken cancellationToken)
    {
        var absoluteManifestPath = Path.GetFullPath(manifestPath);
        if (!File.Exists(absoluteManifestPath))
        {
            throw new FileNotFoundException("Benchmark manifest was not found.", absoluteManifestPath);
        }

        await using var stream = File.OpenRead(absoluteManifestPath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var tasksElement = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement
            : document.RootElement.TryGetProperty("tasks", out var tasks)
                ? tasks
                : throw new InvalidOperationException("Manifest must be an array or contain a 'tasks' array.");

        var studyDesign = document.RootElement.ValueKind == JsonValueKind.Object &&
                          document.RootElement.TryGetProperty("studyDesign", out var designElement)
            ? designElement.Deserialize<BenchmarkStudyDesign>(JsonOptions) ?? new BenchmarkStudyDesign()
            : new BenchmarkStudyDesign();
        var loaded = tasksElement.Deserialize<List<ExperimentTask>>(JsonOptions) ?? [];
        if (loaded.Count == 0)
        {
            throw new InvalidOperationException("Benchmark manifest contains no tasks.");
        }

        var manifestDirectory = Path.GetDirectoryName(absoluteManifestPath)!;
        var normalized = new List<ExperimentTask>(loaded.Count);
        var validationErrors = new List<string>();
        foreach (var task in loaded)
        {
            try
            {
                var configuredRepository = !string.IsNullOrWhiteSpace(repositoryOverride)
                    ? repositoryOverride
                    : task.RepositoryPath;
                var repositoryPath = Path.IsPathRooted(configuredRepository)
                    ? Path.GetFullPath(configuredRepository)
                    : Path.GetFullPath(Path.Combine(manifestDirectory, configuredRepository));
                var buggyRepositoryPath = string.IsNullOrWhiteSpace(task.BuggyRepositoryPath)
                    ? string.Empty
                    : Path.IsPathRooted(task.BuggyRepositoryPath)
                        ? Path.GetFullPath(task.BuggyRepositoryPath)
                        : Path.GetFullPath(Path.Combine(manifestDirectory, task.BuggyRepositoryPath));
                var updated = task with
                {
                    RepositoryPath = repositoryPath,
                    BuggyRepositoryPath = buggyRepositoryPath
                };
                updated.ValidateForRealExecution();
                ValidatePaths(updated);
                normalized.Add(updated);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var identity = string.IsNullOrWhiteSpace(task.TaskId) ? "(missing taskId)" : task.TaskId;
                validationErrors.Add($"{identity}: {exception.Message}");
            }
        }

        if (validationErrors.Count > 0)
        {
            throw new InvalidOperationException(
                $"Manifest contains {validationErrors.Count} structurally invalid task(s): " +
                string.Join(" | ", validationErrors));
        }

        return new LoadedBenchmarkManifest(normalized, studyDesign);
    }

    private static void ValidatePaths(ExperimentTask task)
    {
        if (!Directory.Exists(task.RepositoryPath))
        {
            throw new DirectoryNotFoundException($"Task {task.TaskId} repository does not exist: {task.RepositoryPath}");
        }

        var moduleDirectory = JavaBuildRunner.ResolveWithin(task.RepositoryPath, task.ModulePath);
        if (!Directory.Exists(moduleDirectory))
        {
            throw new DirectoryNotFoundException($"Task {task.TaskId} module does not exist: {moduleDirectory}");
        }

        var testFile = JavaBuildRunner.ResolveWithin(task.RepositoryPath, task.TestFilePath);
        if (!File.Exists(testFile))
        {
            throw new FileNotFoundException($"Task {task.TaskId} test file does not exist.", testFile);
        }

        foreach (var sourceFile in task.FocalSources)
        {
            var sourcePath = JavaBuildRunner.ResolveWithin(task.RepositoryPath, sourceFile);
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException($"Task {task.TaskId} focal source does not exist.", sourcePath);
            }
        }

        var buildFileExists = task.BuildSystem.Equals("maven", StringComparison.OrdinalIgnoreCase)
            ? File.Exists(Path.Combine(moduleDirectory, "pom.xml"))
            : File.Exists(Path.Combine(moduleDirectory, "build.gradle")) ||
              File.Exists(Path.Combine(moduleDirectory, "build.gradle.kts"));
        if (!buildFileExists)
        {
            throw new FileNotFoundException($"Task {task.TaskId} module has no {task.BuildSystem} build file.");
        }

        if (task.RequiresBuggyOracle)
        {
            if (task.RepositoryPath.Equals(task.BuggyRepositoryPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Task {task.TaskId} points fixed and buggy revisions to the same repository.");
            }

            ValidateBuggyRepositoryPaths(task);
        }
    }

    private static void ValidateBuggyRepositoryPaths(ExperimentTask task)
    {
        if (!Directory.Exists(task.BuggyRepositoryPath))
        {
            throw new DirectoryNotFoundException(
                $"Task {task.TaskId} buggy repository does not exist: {task.BuggyRepositoryPath}");
        }

        var moduleDirectory = JavaBuildRunner.ResolveWithin(task.BuggyRepositoryPath, task.ModulePath);
        if (!Directory.Exists(moduleDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Task {task.TaskId} buggy module does not exist: {moduleDirectory}");
        }

        var testFile = JavaBuildRunner.ResolveWithin(task.BuggyRepositoryPath, task.TestFilePath);
        if (!File.Exists(testFile))
        {
            throw new FileNotFoundException(
                $"Task {task.TaskId} buggy revision has no matching test file.", testFile);
        }

        var buildFileExists = task.BuildSystem.Equals("maven", StringComparison.OrdinalIgnoreCase)
            ? File.Exists(Path.Combine(moduleDirectory, "pom.xml"))
            : File.Exists(Path.Combine(moduleDirectory, "build.gradle")) ||
              File.Exists(Path.Combine(moduleDirectory, "build.gradle.kts"));
        if (!buildFileExists)
        {
            throw new FileNotFoundException(
                $"Task {task.TaskId} buggy module has no {task.BuildSystem} build file.");
        }
    }
}
