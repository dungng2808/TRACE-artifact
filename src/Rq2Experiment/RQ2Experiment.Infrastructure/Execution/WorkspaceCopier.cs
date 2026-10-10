namespace RQ2Experiment.Infrastructure.Execution;

internal static class WorkspaceCopier
{
    private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".gradle", ".idea", ".vs", "target", "build", "bin", "obj",
        "experiment-output", "artifacts"
    };

    public static Task CopyAsync(
        string sourceDirectory,
        string targetDirectory,
        string outputRoot,
        CancellationToken cancellationToken) => Task.Run(
            () => CopyDirectory(
                Path.GetFullPath(sourceDirectory),
                Path.GetFullPath(targetDirectory),
                Path.GetFullPath(outputRoot),
                cancellationToken),
            cancellationToken);

    private static void CopyDirectory(
        string sourceDirectory,
        string targetDirectory,
        string outputRoot,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException($"Repository does not exist: {sourceDirectory}");
        }

        Directory.CreateDirectory(targetDirectory);
        foreach (var file in Directory.EnumerateFiles(sourceDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileInfo = new FileInfo(file);
            if ((fileInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            File.Copy(file, Path.Combine(targetDirectory, fileInfo.Name), overwrite: false);
        }

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directoryInfo = new DirectoryInfo(directory);
            if ((directoryInfo.Attributes & FileAttributes.ReparsePoint) != 0 ||
                ExcludedDirectoryNames.Contains(directoryInfo.Name))
            {
                continue;
            }

            var absoluteDirectory = Path.GetFullPath(directory);
            if (IsWithin(absoluteDirectory, outputRoot) || IsWithin(outputRoot, absoluteDirectory))
            {
                continue;
            }

            CopyDirectory(
                absoluteDirectory,
                Path.Combine(targetDirectory, directoryInfo.Name),
                outputRoot,
                cancellationToken);
        }
    }

    private static bool IsWithin(string candidate, string parent)
    {
        var normalizedParent = parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedCandidate = candidate.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase);
    }
}
