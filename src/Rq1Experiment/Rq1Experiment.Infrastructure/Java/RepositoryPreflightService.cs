using System.Globalization;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rq1Experiment.Contracts.Experiments;
using Rq1Experiment.Core.Abstractions;
using Rq1Experiment.Infrastructure.Persistence;

namespace Rq1Experiment.Infrastructure.Java;

public sealed class RepositoryPreflightService(
    IProcessRunner processRunner) : IRepositoryPreflightService
{
    private static readonly SemaphoreSlim PersistenceGate = new(1, 1);
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".gradle", ".idea", ".br-analyzer", "target", "build", "bin", "obj"
    };

    public async Task<PreflightReport> RunAsync(
        PreflightRequest request,
        IProgress<PreflightProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var report = new PreflightReport
        {
            ProjectId = request.ProjectId,
            RepositoryRoot = Path.GetFullPath(request.RepositoryRoot),
            ModulePath = NormalizeModulePath(request.ModulePath),
            StartedAt = DateTimeOffset.UtcNow
        };
        var evidenceDirectory = Path.Combine(
            request.ExperimentOutputRoot,
            "subjects",
            "preflight",
            SanitizeId(request.ProjectId),
            report.StartedAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(evidenceDirectory);
        report.EvidenceDirectory = evidenceDirectory;

        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "rq1-preflight",
            SanitizeId(request.ProjectId) + "-" + Guid.NewGuid().ToString("N"));

        try
        {
            Report(progress, "repository", "Validating repository and revision...", 0);
            if (!Directory.Exists(report.RepositoryRoot))
                return await FinishExcludedAsync(report, "Repository directory does not exist.", request, evidenceDirectory, cancellationToken);

            var moduleRoot = report.ModulePath == "."
                ? report.RepositoryRoot
                : Path.GetFullPath(Path.Combine(report.RepositoryRoot, report.ModulePath));
            if (!IsPathInside(report.RepositoryRoot, moduleRoot) || !Directory.Exists(moduleRoot))
                return await FinishExcludedAsync(report, "Configured module path does not exist inside the repository.", request, evidenceDirectory, cancellationToken);

            var buildEnvironment = BuildJavaEnvironment(request.JavaHome);
            report.JavaHome = buildEnvironment.TryGetValue("JAVA_HOME", out var javaHome)
                ? javaHome ?? string.Empty
                : string.Empty;
            if (!string.IsNullOrWhiteSpace(request.JavaHome) && !HasJavaExecutable(report.JavaHome))
                return await FinishExcludedAsync(report, "Configured JAVA_HOME does not contain bin/java.", request, evidenceDirectory, cancellationToken);

            var build = DetectBuildTool(report.RepositoryRoot, report.RepositoryRoot);
            if (build is null)
                return await FinishExcludedAsync(report, "No Maven/Gradle build file or wrapper was found.", request, evidenceDirectory, cancellationToken);
            report.BuildTool = build.Value.Id;

            report.Revision = await ResolveRevisionAsync(report.RepositoryRoot, evidenceDirectory, cancellationToken);
            report.ProductionJavaFileCount = CountProductionJavaFiles(moduleRoot);
            report.SourceCheck = report.ProductionJavaFileCount > 0 ? "PASS" : "FAIL";
            if (report.ProductionJavaFileCount == 0)
                return await FinishExcludedAsync(report, "No production Java source file was found.", request, evidenceDirectory, cancellationToken);

            Report(progress, "isolation", "Creating isolated preflight workspace...", 1);
            CopyRepository(report.RepositoryRoot, temporaryRoot, cancellationToken);
            var isolatedBuild = DetectBuildTool(temporaryRoot, report.RepositoryRoot)
                ?? throw new InvalidOperationException("Build tool disappeared in the isolated copy.");

            Report(progress, "toolchain", $"Inspecting {isolatedBuild.Id} ({isolatedBuild.Executable}) and JDK...", 2);
            var versionResult = await RunBuildAsync(
                isolatedBuild,
                temporaryRoot,
                ["--version"],
                TimeSpan.FromSeconds(60),
                evidenceDirectory,
                "build-tool-version",
                buildEnvironment,
                report.ModulePath,
                cancellationToken);
            report.BuildToolVersion = FirstNonEmptyLine(versionResult.StandardOutput, versionResult.StandardError);
            report.JavaVersion = ParseJavaVersion(versionResult.StandardOutput + Environment.NewLine + versionResult.StandardError);
            if (!versionResult.IsSuccess || string.IsNullOrWhiteSpace(report.JavaVersion))
                return await FinishExcludedAsync(report, "Build tool/JDK could not be executed.", request, evidenceDirectory, cancellationToken);

            Report(progress, "compile", "Running clean compile/test-compile...", 3);
            var compileArguments = isolatedBuild.Id == "maven"
                ? new[] { "-B", "-ntp", "-DskipTests", "clean", "test-compile" }
                : new[] { "--no-daemon", "clean", "testClasses" };
            var compile = await RunBuildAsync(
                isolatedBuild,
                temporaryRoot,
                compileArguments,
                TimeSpan.FromSeconds(request.CompileTimeoutSeconds),
                evidenceDirectory,
                "clean-compile",
                buildEnvironment,
                report.ModulePath,
                cancellationToken);
            report.CleanCompile = ToStatus(compile);
            if (!compile.IsSuccess)
                return await FinishExcludedAsync(report, "Clean compile failed or timed out.", request, evidenceDirectory, cancellationToken);

            for (var repetition = 1; repetition <= 3; repetition++)
            {
                Report(progress, "tests", $"Running existing tests ({repetition}/3)...", 3 + repetition);
                var testArguments = isolatedBuild.Id == "maven"
                    ? new[] { "-B", "-ntp", "test" }
                    : new[] { "--no-daemon", "test" };
                var test = await RunBuildAsync(
                    isolatedBuild,
                    temporaryRoot,
                    testArguments,
                    TimeSpan.FromSeconds(request.TestTimeoutSeconds),
                    evidenceDirectory,
                    $"existing-tests-{repetition}",
                    buildEnvironment,
                    report.ModulePath,
                    cancellationToken);
                report.ExistingTests.Add(ToStatus(test));
                if (!test.IsSuccess)
                    return await FinishExcludedAsync(report, $"Existing test run {repetition}/3 failed or timed out.", request, evidenceDirectory, cancellationToken);
            }
            report.ExternalServices = "NOT_REQUIRED_BY_3_SUCCESSFUL_TEST_RUNS";

            Report(progress, "jacoco", "Running JaCoCo smoke report...", 7);
            var jacoco = await RunJacocoAsync(
                isolatedBuild, temporaryRoot, request, evidenceDirectory,
                buildEnvironment, report.ModulePath, cancellationToken);
            report.JacocoSmoke = jacoco;
            if (jacoco != "PASS")
                return await FinishExcludedAsync(report, "JaCoCo could not attach and produce XML evidence.", request, evidenceDirectory, cancellationToken);

            Report(progress, "pit", "Running PIT dry-run smoke...", 8);
            var pit = await RunPitAsync(
                isolatedBuild, temporaryRoot, request, evidenceDirectory,
                buildEnvironment, report.ModulePath, cancellationToken);
            report.PitSmoke = pit;
            if (pit != "PASS")
                return await FinishExcludedAsync(report, "PIT dry run could not produce mutation evidence.", request, evidenceDirectory, cancellationToken);

            report.Eligible = true;
            report.ExclusionReason = null;
            report.FinishedAt = DateTimeOffset.UtcNow;
            Report(progress, "complete", "Repository is eligible for subject sampling.", 9);
            await PersistAsync(report, request.ExperimentOutputRoot, cancellationToken);
            return report;
        }
        catch (OperationCanceledException)
        {
            report.Eligible = false;
            report.ExclusionReason = "Preflight cancelled.";
            report.FinishedAt = DateTimeOffset.UtcNow;
            await PersistAsync(report, request.ExperimentOutputRoot, CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            await File.WriteAllTextAsync(
                Path.Combine(evidenceDirectory, "unexpected-error.txt"),
                exception.ToString(),
                CancellationToken.None);
            return await FinishExcludedAsync(
                report,
                "Preflight infrastructure error: " + exception.Message,
                request,
                evidenceDirectory,
                CancellationToken.None);
        }
        finally
        {
            TryDeleteTemporaryWorkspace(temporaryRoot);
        }
    }

    private async Task<string> RunJacocoAsync(
        BuildTool build,
        string repository,
        PreflightRequest request,
        string evidenceDirectory,
        IReadOnlyDictionary<string, string?> environment,
        string modulePath,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> arguments = build.Id == "maven"
            ? ["-B", "-ntp", $"org.jacoco:jacoco-maven-plugin:{request.JacocoVersion}:prepare-agent", "test", $"org.jacoco:jacoco-maven-plugin:{request.JacocoVersion}:report"]
            : ["--no-daemon", "test", "jacocoTestReport"];
        var result = await RunBuildAsync(
            build, repository, arguments,
            TimeSpan.FromSeconds(request.JacocoTimeoutSeconds),
            evidenceDirectory, "jacoco-smoke", environment, modulePath, cancellationToken);
        var reports = Directory.Exists(repository)
            ? Directory.EnumerateFiles(repository, "jacoco*.xml", SearchOption.AllDirectories).ToArray()
            : [];
        await RetainToolReportsAsync(
            repository, evidenceDirectory, "jacoco", reports, cancellationToken);
        return result.IsSuccess && reports.Length > 0 ? "PASS" : ToStatus(result);
    }

    private async Task<string> RunPitAsync(
        BuildTool build,
        string repository,
        PreflightRequest request,
        string evidenceDirectory,
        IReadOnlyDictionary<string, string?> environment,
        string modulePath,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> arguments = build.Id == "maven"
            ? ["-B", "-ntp", "test-compile", $"org.pitest:pitest-maven:{request.PitVersion}:mutationCoverage", "-Dpit.dryRun=true", "-DfailWhenNoMutations=false", "-DoutputFormats=XML"]
            : ["--no-daemon", "pitest", "-PpitestDryRun=true"];
        var result = await RunBuildAsync(
            build, repository, arguments,
            TimeSpan.FromSeconds(request.PitTimeoutSeconds),
            evidenceDirectory, "pit-smoke", environment, modulePath, cancellationToken);
        var reports = Directory.Exists(repository)
            ? Directory.EnumerateFiles(repository, "mutations.xml", SearchOption.AllDirectories).ToArray()
            : [];
        await RetainToolReportsAsync(
            repository, evidenceDirectory, "pit", reports, cancellationToken);
        return result.IsSuccess && reports.Length > 0 ? "PASS" : ToStatus(result);
    }

    private static async Task RetainToolReportsAsync(
        string repository,
        string evidenceDirectory,
        string tool,
        IEnumerable<string> reports,
        CancellationToken cancellationToken)
    {
        var retainedRoot = Path.Combine(evidenceDirectory, "tool-reports", tool);
        var manifest = new StringBuilder("repository_relative_path,retained_path,sha256\n");
        foreach (var source in reports)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(repository, source);
            var destination = Path.Combine(retainedRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
            await using var stream = File.OpenRead(destination);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            manifest.Append(Csv(relative)).Append(',')
                .Append(Csv(destination)).Append(',')
                .Append(hash).AppendLine();
        }
        await File.WriteAllTextAsync(
            Path.Combine(evidenceDirectory, tool + "-reports.csv"),
            manifest.ToString(),
            cancellationToken);
    }

    private async Task<ProcessRunResult> RunBuildAsync(
        BuildTool build,
        string repository,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        string evidenceDirectory,
        string evidenceName,
        IReadOnlyDictionary<string, string?> environment,
        string modulePath,
        CancellationToken cancellationToken)
    {
        var invocation = BuildInvocation(build, ApplyModuleScope(build, arguments, modulePath));
        var result = await processRunner.RunAsync(
            invocation.Executable,
            invocation.Arguments,
            repository,
            timeout,
            environment,
            cancellationToken);
        await WriteProcessEvidenceAsync(evidenceDirectory, evidenceName, invocation, result, cancellationToken);
        return result;
    }

    private async Task<string> ResolveRevisionAsync(
        string repository,
        string evidenceDirectory,
        CancellationToken cancellationToken)
    {
        ProcessRunResult result;
        try
        {
            result = await processRunner.RunAsync(
                "git", ["rev-parse", "HEAD"], repository, TimeSpan.FromSeconds(30),
                cancellationToken: cancellationToken);
        }
        catch (Win32Exception exception)
        {
            result = new ProcessRunResult(
                null,
                false,
                TimeSpan.Zero,
                string.Empty,
                "Git executable unavailable: " + exception.Message);
        }
        await WriteProcessEvidenceAsync(
            evidenceDirectory,
            "revision",
            new Invocation("git", ["rev-parse", "HEAD"]),
            result,
            cancellationToken);
        if (result.IsSuccess && !string.IsNullOrWhiteSpace(result.StandardOutput))
            return result.StandardOutput.Trim();
        return ResolveRevisionWithoutGit(repository) ?? "UNVERSIONED";
    }

    private static string? ResolveRevisionWithoutGit(string repository)
    {
        for (var directory = new DirectoryInfo(repository); directory is not null; directory = directory.Parent)
        {
            foreach (var name in new[] { "REVISION", ".dataset-revision" })
            {
                var revisionFile = Path.Combine(directory.FullName, name);
                if (File.Exists(revisionFile))
                {
                    var value = File.ReadAllText(revisionFile).Trim();
                    if (value.Length > 0) return value;
                }
            }

            var gitDirectory = Path.Combine(directory.FullName, ".git");
            if (File.Exists(gitDirectory))
            {
                var descriptor = File.ReadAllText(gitDirectory).Trim();
                if (descriptor.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
                    gitDirectory = Path.GetFullPath(
                        Path.Combine(directory.FullName, descriptor["gitdir:".Length..].Trim()));
            }
            if (!Directory.Exists(gitDirectory)) continue;

            var headPath = Path.Combine(gitDirectory, "HEAD");
            if (!File.Exists(headPath)) continue;
            var head = File.ReadAllText(headPath).Trim();
            if (!head.StartsWith("ref:", StringComparison.OrdinalIgnoreCase))
                return head.Length > 0 ? head : null;

            var reference = head["ref:".Length..].Trim().Replace('/', Path.DirectorySeparatorChar);
            var looseReference = Path.Combine(gitDirectory, reference);
            if (File.Exists(looseReference)) return File.ReadAllText(looseReference).Trim();

            var packedReferences = Path.Combine(gitDirectory, "packed-refs");
            if (!File.Exists(packedReferences)) continue;
            var normalizedReference = reference.Replace(Path.DirectorySeparatorChar, '/');
            foreach (var line in File.ReadLines(packedReferences))
            {
                if (line.StartsWith('#') || line.StartsWith('^')) continue;
                var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[1] == normalizedReference) return parts[0];
            }
        }
        return null;
    }

    private static BuildTool? DetectBuildTool(string repository, params string[] toolSearchAnchors)
    {
        if (File.Exists(Path.Combine(repository, OperatingSystem.IsWindows() ? "mvnw.cmd" : "mvnw")))
            return new BuildTool("maven", Path.Combine(repository, OperatingSystem.IsWindows() ? "mvnw.cmd" : "mvnw"), true);
        if (File.Exists(Path.Combine(repository, "pom.xml")))
            return new BuildTool("maven", BundledBuildToolResolver.ResolveMaven(toolSearchAnchors), false);
        if (File.Exists(Path.Combine(repository, OperatingSystem.IsWindows() ? "gradlew.bat" : "gradlew")))
            return new BuildTool("gradle", Path.Combine(repository, OperatingSystem.IsWindows() ? "gradlew.bat" : "gradlew"), true);
        if (File.Exists(Path.Combine(repository, "build.gradle")) || File.Exists(Path.Combine(repository, "build.gradle.kts")))
            return new BuildTool("gradle", BundledBuildToolResolver.ResolveGradle(toolSearchAnchors), false);
        return null;
    }

    private static Invocation BuildInvocation(BuildTool build, IEnumerable<string> arguments)
    {
        var allArguments = arguments.ToArray();
        if (OperatingSystem.IsWindows()
            && (build.Executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                || build.Executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            return new Invocation("cmd.exe", ["/d", "/c", "call", build.Executable, .. allArguments]);
        }
        return new Invocation(build.Executable, allArguments);
    }

    private static IReadOnlyList<string> ApplyModuleScope(
        BuildTool build,
        IEnumerable<string> arguments,
        string modulePath)
    {
        var values = arguments.ToArray();
        if (modulePath == "." || values.Contains("--version", StringComparer.Ordinal))
            return values;

        if (build.Id == "maven")
            return ["-pl", modulePath.Replace('\\', '/'), "-am", .. values];

        var taskPrefix = ":" + modulePath
            .Replace('\\', ':')
            .Replace('/', ':')
            .Trim(':');
        return values.Select(value =>
                value.StartsWith('-') || value.Contains(':')
                    ? value
                    : taskPrefix + ":" + value)
            .ToArray();
    }

    private static Dictionary<string, string?> BuildJavaEnvironment(string configuredJavaHome)
    {
        var existingJavaOptions = Environment.GetEnvironmentVariable("JAVA_TOOL_OPTIONS");
        var deterministicJavaOptions = "-Duser.language=en -Duser.country=US -Duser.variant=";
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["LANG"] = "C",
            ["LC_ALL"] = "C",
            ["JAVA_TOOL_OPTIONS"] = string.IsNullOrWhiteSpace(existingJavaOptions)
                ? deterministicJavaOptions
                : existingJavaOptions + " " + deterministicJavaOptions
        };
        if (string.IsNullOrWhiteSpace(configuredJavaHome)) return environment;

        var javaHome = Path.GetFullPath(configuredJavaHome.Trim());
        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        environment["JAVA_HOME"] = javaHome;
        environment["PATH"] = Path.Combine(javaHome, "bin")
                              + Path.PathSeparator
                              + currentPath;
        return environment;
    }

    private static bool HasJavaExecutable(string javaHome)
    {
        var executable = OperatingSystem.IsWindows() ? "java.exe" : "java";
        return File.Exists(Path.Combine(javaHome, "bin", executable));
    }

    private static string NormalizeModulePath(string modulePath)
    {
        if (string.IsNullOrWhiteSpace(modulePath)) return ".";
        var normalized = modulePath.Trim()
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar);
        return normalized.Length == 0 ? "." : normalized;
    }

    private static bool IsPathInside(string root, string candidate)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        return relative == "."
               || (!Path.IsPathRooted(relative)
                   && relative != ".."
                   && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    private static int CountProductionJavaFiles(string repository) =>
        Directory.EnumerateFiles(repository, "*.java", SearchOption.AllDirectories)
            .Count(path => !HasExcludedSegment(path)
                           && !path.Contains($"{Path.DirectorySeparatorChar}test{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                           && !path.Contains($"{Path.DirectorySeparatorChar}src{Path.DirectorySeparatorChar}test{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

    private static void CopyRepository(string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        var pending = new Stack<(string Source, string Destination)>();
        pending.Push((source, destination));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            foreach (var directory in Directory.EnumerateDirectories(current.Source))
            {
                if (ExcludedDirectories.Contains(Path.GetFileName(directory))) continue;
                var childDestination = Path.Combine(current.Destination, Path.GetFileName(directory));
                Directory.CreateDirectory(childDestination);
                pending.Push((directory, childDestination));
            }
            foreach (var file in Directory.EnumerateFiles(current.Source))
                File.Copy(file, Path.Combine(current.Destination, Path.GetFileName(file)), overwrite: false);
        }
    }

    private static bool HasExcludedSegment(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(ExcludedDirectories.Contains);

    private static async Task WriteProcessEvidenceAsync(
        string evidenceDirectory,
        string name,
        Invocation invocation,
        ProcessRunResult result,
        CancellationToken cancellationToken)
    {
        var text = new StringBuilder()
            .AppendLine("Executable: " + invocation.Executable)
            .AppendLine("Arguments: " + string.Join(" ", invocation.Arguments))
            .AppendLine("ExitCode: " + result.ExitCode)
            .AppendLine("TimedOut: " + result.TimedOut)
            .AppendLine("Duration: " + result.Duration)
            .AppendLine("--- STDOUT ---")
            .AppendLine(result.StandardOutput)
            .AppendLine("--- STDERR ---")
            .AppendLine(result.StandardError)
            .ToString();
        await File.WriteAllTextAsync(Path.Combine(evidenceDirectory, name + ".log"), text, cancellationToken);
    }

    private static async Task<PreflightReport> FinishExcludedAsync(
        PreflightReport report,
        string reason,
        PreflightRequest request,
        string evidenceDirectory,
        CancellationToken cancellationToken)
    {
        report.Eligible = false;
        report.ExclusionReason = reason;
        report.FinishedAt = DateTimeOffset.UtcNow;
        await PersistAsync(report, request.ExperimentOutputRoot, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(evidenceDirectory, "exclusion-reason.txt"), reason, cancellationToken);
        return report;
    }

    private static async Task PersistAsync(
        PreflightReport report,
        string outputRoot,
        CancellationToken cancellationToken)
    {
        await PersistenceGate.WaitAsync(cancellationToken);
        try
        {
            var subjectsDirectory = Path.Combine(outputRoot, "subjects");
            var canonicalDirectory = Path.Combine(subjectsDirectory, "preflight-reports");
            Directory.CreateDirectory(canonicalDirectory);
            var serialized = JsonSerializer.Serialize(report, JsonSupport.Options);
            await File.WriteAllTextAsync(
                Path.Combine(subjectsDirectory, "preflight-report.json"),
                serialized,
                cancellationToken);
            var canonicalName = SanitizeId(report.ProjectId)
                                + "--"
                                + SanitizeId(report.ModulePath)
                                + ".json";
            await File.WriteAllTextAsync(
                Path.Combine(canonicalDirectory, canonicalName),
                serialized,
                cancellationToken);

            var canonicalReports = new List<PreflightReport>();
            foreach (var path in Directory.EnumerateFiles(canonicalDirectory, "*.json"))
            {
                var item = JsonSerializer.Deserialize<PreflightReport>(
                    await File.ReadAllTextAsync(path, cancellationToken),
                    JsonSupport.Options);
                if (item is not null) canonicalReports.Add(item);
            }

            var csv = new StringBuilder("project_id,module_path,eligible,exclusion_reason,evidence_directory\n");
            foreach (var item in canonicalReports
                         .Where(item => !item.Eligible)
                         .OrderBy(item => item.ProjectId, StringComparer.Ordinal)
                         .ThenBy(item => item.ModulePath, StringComparer.Ordinal))
            {
                csv.Append(Csv(item.ProjectId)).Append(',')
                    .Append(Csv(item.ModulePath)).Append(',')
                    .Append("false,")
                    .Append(Csv(item.ExclusionReason ?? string.Empty)).Append(',')
                    .Append(Csv(item.EvidenceDirectory)).AppendLine();
            }
            await File.WriteAllTextAsync(
                Path.Combine(subjectsDirectory, "exclusions.csv"),
                csv.ToString(),
                cancellationToken);
        }
        finally
        {
            PersistenceGate.Release();
        }
    }

    private static string ParseJavaVersion(string text)
    {
        foreach (var line in text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("Java version:", StringComparison.OrdinalIgnoreCase))
                return line[(line.IndexOf(':') + 1)..].Trim().Split(',')[0];
            if (line.Contains(" version \"", StringComparison.OrdinalIgnoreCase))
                return line.Split('"').Skip(1).FirstOrDefault() ?? string.Empty;
        }
        return string.Empty;
    }

    private static string FirstNonEmptyLine(params string[] texts) =>
        texts.SelectMany(value => value.Split('\n'))
            .Select(value => value.Trim())
            .FirstOrDefault(value => value.Length > 0) ?? string.Empty;

    private static string ToStatus(ProcessRunResult result) =>
        result.TimedOut ? "TIMEOUT" : result.IsSuccess ? "PASS" : "FAIL";

    private static string SanitizeId(string value) =>
        string.Concat(value.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_'));

    private static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';

    private static void Report(IProgress<PreflightProgress>? progress, string id, string message, int completed) =>
        progress?.Report(new PreflightProgress(id, message, completed, 9));

    private static void TryDeleteTemporaryWorkspace(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Evidence already exists; stale temp cleanup can be retried separately.
        }
    }

    private readonly record struct BuildTool(string Id, string Executable, bool IsWrapper);
    private sealed record Invocation(string Executable, IReadOnlyList<string> Arguments);
}
