namespace Rq1Experiment.Infrastructure.Java;

public static class BundledBuildToolResolver
{
    public static string ResolveMaven(params string[] anchors) =>
        Resolve("Maven", "apache-maven-", OperatingSystem.IsWindows() ? "mvn.cmd" : "mvn", anchors)
        ?? "mvn";

    public static string ResolveGradle(params string[] anchors) =>
        Resolve("Gradle", "gradle-", OperatingSystem.IsWindows() ? "gradle.bat" : "gradle", anchors)
        ?? "gradle";

    private static string? Resolve(
        string toolDirectory,
        string distributionPrefix,
        string executable,
        IEnumerable<string> anchors)
    {
        var roots = anchors
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Append(AppContext.BaseDirectory)
            .Append(Environment.CurrentDirectory)
            .SelectMany(Ancestors)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            var distributionRoot = Path.Combine(root, toolDirectory);
            if (!Directory.Exists(distributionRoot)) continue;
            foreach (var distribution in Directory.EnumerateDirectories(distributionRoot, distributionPrefix + "*")
                         .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var candidate = Path.Combine(distribution, "bin", executable);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    private static IEnumerable<string> Ancestors(string anchor)
    {
        var path = File.Exists(anchor) ? Path.GetDirectoryName(anchor) : anchor;
        if (string.IsNullOrWhiteSpace(path)) yield break;
        DirectoryInfo? directory;
        try
        {
            directory = new DirectoryInfo(Path.GetFullPath(path));
        }
        catch
        {
            yield break;
        }
        while (directory is not null)
        {
            yield return directory.FullName;
            directory = directory.Parent;
        }
    }
}
