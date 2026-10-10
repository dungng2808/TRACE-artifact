using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FullChain.Contracts;

namespace FullChain.Infrastructure.Workspace;

public sealed class WorkspaceManager : IWorkspaceManager
{
    private readonly string _baseRunsDirectory;
    private readonly string _repoRoot;

    public WorkspaceManager(string baseRunsDirectory, string? repoRoot = null)
    {
        _baseRunsDirectory = Path.GetFullPath(baseRunsDirectory);
        _repoRoot = !string.IsNullOrEmpty(repoRoot) ? Path.GetFullPath(repoRoot) : ResolveRepoRoot(_baseRunsDirectory);
    }

    public Task<string> PrepareIsolatedWorkspaceAsync(
        string projectPath,
        string campaignId,
        string targetId,
        int repetitionId,
        string treatment,
        string stage,
        CancellationToken ct = default)
    {
        var resolvedProjectPath = ResolveSourceProjectPath(projectPath);

        var sanitizedCampaign = Sanitize(campaignId);
        var baseDir = Path.GetFileName(_baseRunsDirectory).Equals(sanitizedCampaign, StringComparison.OrdinalIgnoreCase)
            ? _baseRunsDirectory
            : Path.Combine(_baseRunsDirectory, sanitizedCampaign);

        var targetDir = Path.Combine(
            baseDir,
            Sanitize(targetId),
            $"rep_{repetitionId}",
            Sanitize(treatment),
            Sanitize(stage),
            "workspace"
        );

        if (Directory.Exists(targetDir))
        {
            Directory.Delete(targetDir, recursive: true);
        }

        Directory.CreateDirectory(targetDir);
        CopyDirectory(resolvedProjectPath, targetDir);

        return Task.FromResult(targetDir);
    }

    public Task CleanupWorkspaceAsync(string workspacePath, CancellationToken ct = default)
    {
        if (Directory.Exists(workspacePath))
        {
            try
            {
                Directory.Delete(workspacePath, recursive: true);
            }
            catch (Exception)
            {
                // Best effort cleanup
            }
        }
        return Task.CompletedTask;
    }

    private string ResolveSourceProjectPath(string rawPath)
    {
        if (Directory.Exists(rawPath))
        {
            return Path.GetFullPath(rawPath);
        }

        var projectName = Path.GetFileName(rawPath.TrimEnd('/', '\\'));

        // Collect possible repo root candidates
        var searchRoots = new List<string>();
        if (!string.IsNullOrEmpty(_repoRoot) && Directory.Exists(_repoRoot))
        {
            searchRoots.Add(_repoRoot);
        }

        var fromRuns = ResolveRepoRoot(_baseRunsDirectory);
        if (!string.IsNullOrEmpty(fromRuns) && !searchRoots.Contains(fromRuns, StringComparer.OrdinalIgnoreCase))
        {
            searchRoots.Add(fromRuns);
        }

        var fromCwd = ResolveRepoRoot(Directory.GetCurrentDirectory());
        if (!string.IsNullOrEmpty(fromCwd) && !searchRoots.Contains(fromCwd, StringComparer.OrdinalIgnoreCase))
        {
            searchRoots.Add(fromCwd);
        }

        var fromApp = ResolveRepoRoot(AppContext.BaseDirectory);
        if (!string.IsNullOrEmpty(fromApp) && !searchRoots.Contains(fromApp, StringComparer.OrdinalIgnoreCase))
        {
            searchRoots.Add(fromApp);
        }

        foreach (var root in searchRoots)
        {
            // 1. Direct relative combine
            var direct = Path.Combine(root, rawPath);
            if (Directory.Exists(direct)) return direct;

            // 2. Map "datasets/sf110/..." -> "datasets/rq1/sf110/..."
            if (rawPath.StartsWith("datasets/sf110", StringComparison.OrdinalIgnoreCase) ||
                rawPath.StartsWith("datasets\\sf110", StringComparison.OrdinalIgnoreCase))
            {
                var relPath = rawPath.Substring("datasets/".Length).TrimStart('/', '\\');
                var rq1Candidate = Path.Combine(root, "datasets", "rq1", relPath);
                if (Directory.Exists(rq1Candidate)) return rq1Candidate;
            }

            // 3. Resolve by project name under datasets/rq1/sf110/java-ready/projects/<projectName>
            var sf110Rq1 = Path.Combine(root, "datasets", "rq1", "sf110", "java-ready", "projects", projectName);
            if (Directory.Exists(sf110Rq1)) return sf110Rq1;

            // 4. Resolve by project name under datasets/sf110/java-ready/projects/<projectName>
            var sf110Direct = Path.Combine(root, "datasets", "sf110", "java-ready", "projects", projectName);
            if (Directory.Exists(sf110Direct)) return sf110Direct;
        }

        // Return original so CopyDirectory throws an informative DirectoryNotFoundException
        return rawPath;
    }

    public static string ResolveRepoRoot(string? startDir = null)
    {
        var current = !string.IsNullOrEmpty(startDir) ? Path.GetFullPath(startDir) : Directory.GetCurrentDirectory();
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "docs", "FULL_CHAIN_REBUILD_PLAN.md")) ||
                Directory.Exists(Path.Combine(current, "datasets", "rq1", "sf110")) ||
                Directory.Exists(Path.Combine(current, "datasets", "sf110")))
            {
                return current;
            }
            var parent = Directory.GetParent(current);
            if (parent == null) break;
            current = parent.FullName;
        }

        // Fallback to searching upwards from AppContext.BaseDirectory
        current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "docs", "FULL_CHAIN_REBUILD_PLAN.md")) ||
                Directory.Exists(Path.Combine(current, "datasets", "rq1", "sf110")) ||
                Directory.Exists(Path.Combine(current, "datasets", "sf110")))
            {
                return current;
            }
            var parent = Directory.GetParent(current);
            if (parent == null) break;
            current = parent.FullName;
        }

        return Directory.GetCurrentDirectory();
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        var dir = new DirectoryInfo(sourceDir);
        if (!dir.Exists)
        {
            throw new DirectoryNotFoundException($"Source directory not found: {sourceDir}");
        }

        Directory.CreateDirectory(destinationDir);

        foreach (var file in dir.GetFiles())
        {
            // Skip transient local build outputs if any
            if (file.Extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            var targetFilePath = Path.Combine(destinationDir, file.Name);
            file.CopyTo(targetFilePath, overwrite: true);
        }

        foreach (var subDir in dir.GetDirectories())
        {
            // Ignore target, .git, .idea, .vscode to keep isolation lightweight and pristine
            if (subDir.Name.Equals("target", StringComparison.OrdinalIgnoreCase) ||
                subDir.Name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                subDir.Name.Equals(".idea", StringComparison.OrdinalIgnoreCase) ||
                subDir.Name.Equals(".vs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var nextTargetSubDir = Path.Combine(destinationDir, subDir.Name);
            CopyDirectory(subDir.FullName, nextTargetSubDir);
        }
    }

    private static string Sanitize(string name)
    {
        return string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
    }
}
