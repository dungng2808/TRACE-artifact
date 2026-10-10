namespace Rq1Experiment.Infrastructure.Persistence;

public sealed class WorkspaceLayout
{
    public WorkspaceLayout(string workspaceRoot)
    {
        WorkspaceRoot = Path.GetFullPath(workspaceRoot);
    }

    public string WorkspaceRoot { get; }
    public string ExperimentConfigPath => Path.Combine(WorkspaceRoot, "experiments", "rq1", "config", "rq1.json");
    public string ExperimentConfigHistoryDirectory => Path.Combine(WorkspaceRoot, "experiments", "rq1", "config", "experiments");
    public string PhaseProgressPath => Path.Combine(WorkspaceRoot, "experiments", "rq1", "state", "phase-progress.json");
    public string PipelineCheckpointPath => Path.Combine(WorkspaceRoot, "experiments", "rq1", "state", "pipeline-checkpoint.json");
    // Legacy single-experiment lock. Kept read-only for migration of runs
    // created before locks were scoped by experiment ID.
    public string OfficialConfigLockPath => Path.Combine(WorkspaceRoot, "experiments", "rq1", "state", "official-config-lock.json");
    public string OfficialConfigLockDirectory => Path.Combine(WorkspaceRoot, "experiments", "rq1", "state", "official-config-locks");
    public string GetOfficialConfigLockPath(string experimentId)
    {
        var idHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(experimentId.Trim())))
            .ToLowerInvariant();
        return Path.Combine(OfficialConfigLockDirectory, idHash + ".json");
    }
    public string GetPhase5PromptContextLockPath(string experimentId)
    {
        var idHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(experimentId.Trim())))
            .ToLowerInvariant();
        return Path.Combine(WorkspaceRoot, "experiments", "rq1", "state",
            "phase5-prompt-context-locks", idHash + ".json");
    }
    public string ModelCatalogPath => Path.Combine(WorkspaceRoot, "src", "Rq1Experiment", "Rq1Experiment.Wpf", "data", "model-catalog.json");
    public string ApiKeyDirectory => Path.Combine(WorkspaceRoot, "src", "Rq1Experiment", "Rq1Experiment.Wpf", "data", "api-keys");
    public string ApiKeyStatePath => Path.Combine(WorkspaceRoot, "src", "Rq1Experiment", "Rq1Experiment.Wpf", "data", "api-key-state.json");

    public string ResolveOutputRoot(string outputRoot) =>
        ResolveRepositoryPath(WorkspaceRoot, outputRoot);

    // Historical subject manifests retain their original paths and hashes.
    // Resolve their dataset location against the current checkout at read time.
    public static string ResolveRepositoryPath(string workspaceRoot, string repositoryPath)
    {
        var portable = repositoryPath.Replace('\\', '/');
        var datasetIndex = portable.StartsWith("datasets/", StringComparison.Ordinal)
            ? 0 : portable.IndexOf("/datasets/", StringComparison.Ordinal);
        if (datasetIndex >= 0)
        {
            var relative = portable[(datasetIndex == 0 ? 0 : datasetIndex + 1)..];
            foreach (var dataset in new[] { "projecttest", "sf110", "dronology" })
            {
                var oldPrefix = "datasets/" + dataset;
                if (relative == oldPrefix || relative.StartsWith(oldPrefix + "/", StringComparison.Ordinal))
                    return Path.Combine(workspaceRoot,
                        ("datasets/rq1/" + relative["datasets/".Length..]).Replace('/', Path.DirectorySeparatorChar));
            }
            if (relative.StartsWith("datasets/rq1/", StringComparison.Ordinal))
                return Path.Combine(workspaceRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        }
        return Path.IsPathRooted(repositoryPath)
            ? repositoryPath
            : Path.Combine(workspaceRoot, portable.Replace('/', Path.DirectorySeparatorChar));
    }

    public static string FindWorkspaceRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RQ1_EXPERIMENT_PHASES.md")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Cannot find workspace root containing RQ1_EXPERIMENT_PHASES.md. Start the app from the repository or pass an explicit root.");
    }
}
