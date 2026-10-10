using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using RQ2Experiment.Core.Domain;

namespace RQ2Experiment.Infrastructure.Execution;

internal sealed record QualityMeasurementResult(
    double? Value,
    MeasurementStatus Status,
    string Detail,
    string ReportPath = "",
    int? ToolExitCode = null,
    double DurationSeconds = 0);

internal sealed partial class JavaBuildRunner(
    ExperimentTask task,
    string workspacePath,
    string artifactDirectory)
{
    private const string JaCoCoMavenPlugin = "org.jacoco:jacoco-maven-plugin:0.8.12";
    private const string PitestMavenPlugin = "org.pitest:pitest-maven:1.25.9";
    // Defects4J-era projects can pin Surefire versions that cannot discover a
    // JUnit 4 generated test, even though the source compiles.  This fallback
    // is only used after the configured plugin reports zero executed tests.
    private const string SurefireFallbackMavenPlugin = "org.apache.maven.plugins:maven-surefire-plugin:2.22.2";
    // Older Defects4J-era Maven projects can fail to load recent PIT plugins.
    // Keep this as a measurement-only fallback; repair success is never inferred
    // from a fallback result alone.
    private const string PitestFallbackMavenPlugin = "org.pitest:pitest-maven:1.15.8";
    private readonly string workingDirectory = ResolveWithin(workspacePath, task.ModulePath);

    public Task<ProcessResult> CompileAsync(int iteration, CancellationToken cancellationToken) =>
        RunAndSaveAsync(
            iteration,
            "compile",
            task.BuildSystem.Equals("gradle", StringComparison.OrdinalIgnoreCase)
                ? ["testClasses"]
                : MavenCompileOrTestArguments("-q", "-Dstyle.color=never", "-DskipTests", "test-compile"),
            cancellationToken);

    public async Task<ProcessResult> RunFocalTestAsync(int iteration, CancellationToken cancellationToken)
    {
        ValidateTestIdentifier(task.TestClassName);
        ValidateTestIdentifier(task.FailingTestMethod);
        var arguments = task.BuildSystem.Equals("gradle", StringComparison.OrdinalIgnoreCase)
            ? new[] { "test", "--tests", $"{task.TestClassName}.{task.FailingTestMethod}" }
            : MavenCompileOrTestArguments("-q", "-Dstyle.color=never", $"-Dtest={task.TestClassName}#{task.FailingTestMethod}", "test");
        var result = await RunAndSaveAsync(iteration, "focal-test", arguments, cancellationToken);

        // Some Defects4J-era Surefire versions report zero executed tests for a
        // valid JUnit 4 generated test. Retry only that exact focal method with
        // a compatible Surefire version; never run the whole class, so this
        // corrects a runner false-negative without weakening the focal oracle.
        if (!task.BuildSystem.Equals("gradle", StringComparison.OrdinalIgnoreCase) &&
            !result.Succeeded &&
            result.CombinedOutput.Contains("No tests were executed", StringComparison.OrdinalIgnoreCase))
        {
            var fallbackArguments = MavenCompileOrTestArguments(
                "-q",
                "-Dstyle.color=never",
                $"-Dtest={task.TestClassName}#{task.FailingTestMethod}",
                $"{SurefireFallbackMavenPlugin}:test");
            return await RunAndSaveAsync(
                iteration,
                "focal-test-surefire-fallback",
                fallbackArguments,
                cancellationToken);
        }

        return result;
    }

    public async Task<(int RegressionCount, string Summary)> RunRegressionAsync(
        int iteration,
        CancellationToken cancellationToken)
    {
        ValidateTestIdentifier(task.TestClassName);
        var classResult = await RunAndSaveAsync(
            iteration,
            "regression-test-class",
            task.BuildSystem.Equals("gradle", StringComparison.OrdinalIgnoreCase)
                ? ["test", "--tests", task.TestClassName]
                : MavenCompileOrTestArguments("-q", "-Dstyle.color=never", $"-Dtest={task.TestClassName}", "test"),
            cancellationToken);

        var externalVerifiedTests = task.VerifiedTests
            .Where(identifier => !IsTestFromFocalClass(identifier))
            .ToArray();
        var failures = classResult.Succeeded ? 0 : 1;
        foreach (var identifier in externalVerifiedTests)
        {
            ValidateCompositeTestIdentifier(identifier);
            var result = await RunAndSaveAsync(
                iteration,
                $"verified-{SanitizeFileName(identifier)}",
                task.BuildSystem.Equals("gradle", StringComparison.OrdinalIgnoreCase)
                    ? ["test", "--tests", identifier.Replace('#', '.')]
                    : MavenCompileOrTestArguments("-q", "-Dstyle.color=never", $"-Dtest={identifier}", "test"),
                cancellationToken);
            if (!result.Succeeded) failures++;
        }

        var classSummary = classResult.Succeeded
            ? $"Complete test class {task.TestClassName} passed."
            : $"Complete test class {task.TestClassName} failed; at least one existing test regressed.";
        var externalSummary = externalVerifiedTests.Length == 0
            ? string.Empty
            : $" {externalVerifiedTests.Length - Math.Max(0, failures - (classResult.Succeeded ? 0 : 1))}/" +
              $"{externalVerifiedTests.Length} external verified tests passed.";
        return (failures, classSummary + externalSummary);
    }

    private bool IsTestFromFocalClass(string identifier) =>
        identifier.Equals(task.TestClassName, StringComparison.Ordinal) ||
        identifier.StartsWith(task.TestClassName + "#", StringComparison.Ordinal);

    public async Task<QualityMeasurementResult> RunCoverageAsync(
        int iteration,
        CancellationToken cancellationToken)
    {
        var focalClasses = ResolveFocalClassNames();
        if (focalClasses.Count == 0)
        {
            return await SaveMeasurementResultAsync(
                iteration,
                "coverage",
                new QualityMeasurementResult(
                    null,
                    MeasurementStatus.ConfigurationError,
                    "No focal production class could be derived from FocalSourceFiles."),
                cancellationToken);
        }

        ValidateTestIdentifier(task.TestClassName);
        ValidateTestIdentifier(task.FailingTestMethod);
        TemporaryFilePatch? argLineBridge = null;
        var measurementStartedAt = DateTimeOffset.UtcNow;
        ProcessResult result;
        try
        {
            if (!task.BuildSystem.Equals("gradle", StringComparison.OrdinalIgnoreCase))
            {
                argLineBridge = await AddTemporaryJaCoCoArgLineBridgeAsync(cancellationToken);
            }

            result = await RunAndSaveAsync(
                iteration,
                "coverage",
                task.BuildSystem.Equals("gradle", StringComparison.OrdinalIgnoreCase)
                    ? ["test", "--tests", $"{task.TestClassName}.{task.FailingTestMethod}", "jacocoTestReport"]
                    :
                    [
                        "-q",
                        "-Dstyle.color=never",
                        $"{JaCoCoMavenPlugin}:prepare-agent",
                        $"-Dtest={task.TestClassName}#{task.FailingTestMethod}",
                        "test",
                        $"{JaCoCoMavenPlugin}:report"
                    ],
                cancellationToken);
        }
        catch (Exception exception) when (IsMeasurementToolException(exception, cancellationToken))
        {
            return await SaveMeasurementResultAsync(
                iteration,
                "coverage",
                ClassifyMeasurementException(exception),
                CancellationToken.None);
        }
        finally
        {
            if (argLineBridge is not null)
            {
                await File.WriteAllTextAsync(
                    argLineBridge.Path,
                    argLineBridge.OriginalContent,
                    CancellationToken.None);
            }
        }

        var reportPath = ResolveWithin(workingDirectory, task.CoverageReportPath);
        var measurement = ReadMeasurement(result, reportPath, measurementStartedAt, () =>
            QualityReportReader.ReadJacocoLineCoverage(reportPath, focalClasses));
        return await SaveMeasurementResultAsync(
            iteration,
            "coverage",
            measurement,
            cancellationToken);
    }

    public async Task<QualityMeasurementResult> RunMutationAsync(
        int iteration,
        CancellationToken cancellationToken)
    {
        var focalClasses = ResolveFocalClassNames();
        if (focalClasses.Count == 0)
        {
            return await SaveMeasurementResultAsync(
                iteration,
                "mutation",
                new QualityMeasurementResult(
                    null,
                    MeasurementStatus.ConfigurationError,
                    "No PIT targetClasses could be derived from FocalSourceFiles."),
                cancellationToken);
        }

        foreach (var focalClass in focalClasses) ValidateTestIdentifier(focalClass);
        ValidateTestIdentifier(task.TestClassName);
        var measurementStartedAt = DateTimeOffset.UtcNow;
        ProcessResult result;
        try
        {
            result = await RunAndSaveAsync(
                iteration,
                "mutation",
                task.BuildSystem.Equals("gradle", StringComparison.OrdinalIgnoreCase)
                    ? ["pitest"]
                    : MavenMutationArguments(focalClasses, PitestMavenPlugin),
                cancellationToken,
                TimeSpan.FromMinutes(30));

            if (!result.Succeeded &&
                !task.BuildSystem.Equals("gradle", StringComparison.OrdinalIgnoreCase) &&
                result.CombinedOutput.Contains("PluginContainerException", StringComparison.OrdinalIgnoreCase))
            {
                result = await RunAndSaveAsync(
                    iteration,
                    "mutation-pit-fallback",
                    MavenMutationArguments(focalClasses, PitestFallbackMavenPlugin),
                    cancellationToken,
                    TimeSpan.FromMinutes(30));
            }
        }
        catch (Exception exception) when (IsMeasurementToolException(exception, cancellationToken))
        {
            return await SaveMeasurementResultAsync(
                iteration,
                "mutation",
                ClassifyMeasurementException(exception),
                CancellationToken.None);
        }
        var reportPath = ResolvePitReportPath(ResolveWithin(workingDirectory, task.MutationReportPath));
        var measurement = ReadMeasurement(
            result,
            reportPath,
            measurementStartedAt,
            () => QualityReportReader.ReadPitMutationScore(reportPath));
        return await SaveMeasurementResultAsync(
            iteration,
            "mutation",
            measurement,
            cancellationToken);
    }

    internal static QualityMeasurementResult ReadMeasurement(
        ProcessResult process,
        string reportPath,
        DateTimeOffset measurementStartedAt,
        Func<double?> readValue)
    {
        if (!process.Succeeded)
        {
            return new QualityMeasurementResult(
                null,
                MeasurementStatus.ToolFailed,
                $"Measurement command exited with code {process.ExitCode}. {Tail(process.CombinedOutput)}",
                reportPath,
                process.ExitCode,
                process.Duration.TotalSeconds);
        }
        if (!File.Exists(reportPath))
        {
            return new QualityMeasurementResult(
                null,
                MeasurementStatus.ReportMissing,
                "Measurement command succeeded but the configured report was not created.",
                reportPath,
                process.ExitCode,
                process.Duration.TotalSeconds);
        }

        var reportTimestamp = File.GetLastWriteTimeUtc(reportPath);
        if (reportTimestamp < measurementStartedAt.UtcDateTime.AddSeconds(-5))
        {
            return new QualityMeasurementResult(
                null,
                MeasurementStatus.ReportStale,
                $"Report predates this measurement run (report={reportTimestamp:O}, run={measurementStartedAt:O}).",
                reportPath,
                process.ExitCode,
                process.Duration.TotalSeconds);
        }

        try
        {
            var value = readValue();
            return value.HasValue
                ? new QualityMeasurementResult(
                    value,
                    MeasurementStatus.Succeeded,
                    "Fresh report parsed successfully.",
                    reportPath,
                    process.ExitCode,
                    process.Duration.TotalSeconds)
                : new QualityMeasurementResult(
                    null,
                    MeasurementStatus.ReportInvalid,
                    "Report did not contain a usable metric for the configured focal classes.",
                    reportPath,
                    process.ExitCode,
                    process.Duration.TotalSeconds);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return new QualityMeasurementResult(
                null,
                MeasurementStatus.ReportInvalid,
                $"Could not parse report: {exception.Message}",
                reportPath,
                process.ExitCode,
                process.Duration.TotalSeconds);
        }
    }

    private IReadOnlyList<string> ResolveFocalClassNames()
    {
        const string sourceMarker = "/src/main/java/";
        return task.FocalSources
            .Select(path => path.Replace('\\', '/'))
            .Select(path =>
            {
                var markerIndex = path.IndexOf(sourceMarker, StringComparison.OrdinalIgnoreCase);
                var relativeClassPath = markerIndex >= 0
                    ? path[(markerIndex + sourceMarker.Length)..]
                    : path.StartsWith("src/main/java/", StringComparison.OrdinalIgnoreCase)
                        ? path["src/main/java/".Length..]
                        : string.Empty;
                return relativeClassPath.EndsWith(".java", StringComparison.OrdinalIgnoreCase)
                    ? relativeClassPath[..^".java".Length].Replace('/', '.')
                    : string.Empty;
            })
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<QualityMeasurementResult> SaveMeasurementResultAsync(
        int iteration,
        string name,
        QualityMeasurementResult result,
        CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(
            Path.Combine(artifactDirectory, $"iteration-{iteration:00}-{name}-measurement.json"),
            JsonSerializer.Serialize(new
            {
                metric = name,
                status = result.Status.ToString(),
                result.Value,
                result.Detail,
                result.ReportPath,
                result.ToolExitCode,
                result.DurationSeconds
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }),
            cancellationToken);
        return result;
    }

    private static bool IsMeasurementToolException(
        Exception exception,
        CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested && exception is
            TimeoutException or
            Win32Exception or
            FileNotFoundException or
            DirectoryNotFoundException or
            IOException or
            UnauthorizedAccessException or
            InvalidOperationException;

    internal static QualityMeasurementResult ClassifyMeasurementException(Exception exception)
    {
        var status = exception switch
        {
            TimeoutException => MeasurementStatus.ToolTimedOut,
            Win32Exception or FileNotFoundException or DirectoryNotFoundException => MeasurementStatus.ToolUnavailable,
            _ => MeasurementStatus.ToolFailed
        };
        return new QualityMeasurementResult(
            null,
            status,
            $"{exception.GetType().Name}: {exception.Message}");
    }

    private static string Tail(string value)
    {
        const int maximumLength = 1_000;
        var compact = value.Trim();
        return compact.Length <= maximumLength ? compact : compact[^maximumLength..];
    }

    private async Task<TemporaryFilePatch?> AddTemporaryJaCoCoArgLineBridgeAsync(
        CancellationToken cancellationToken)
    {
        var pomPath = Path.Combine(workingDirectory, "pom.xml");
        if (!File.Exists(pomPath)) return null;

        var original = await File.ReadAllTextAsync(pomPath, cancellationToken);
        var match = MavenArgLineRegex().Match(original);
        if (!match.Success) return null;

        var valueGroup = match.Groups["value"];
        if (valueGroup.Value.Contains("@{argLine}", StringComparison.Ordinal) ||
            valueGroup.Value.Contains("${argLine}", StringComparison.Ordinal))
        {
            return null;
        }

        var bridged = original.Insert(valueGroup.Index, "@{argLine} ");
        await File.WriteAllTextAsync(pomPath, bridged, cancellationToken);
        return new TemporaryFilePatch(pomPath, original);
    }

    private async Task<ProcessResult> RunAndSaveAsync(
        int iteration,
        string name,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var executable = ResolveExecutable();
        var result = await ProcessRunner.RunAsync(
            executable,
            arguments,
            workingDirectory,
            timeout ?? TimeSpan.FromMinutes(10),
            cancellationToken,
            string.IsNullOrWhiteSpace(task.JavaHome)
                ? null
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["JAVA_HOME"] = task.JavaHome,
                    ["PATH"] = Path.Combine(task.JavaHome, "bin") + Path.PathSeparator +
                               (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                });
        var log = $"Command: {executable} {string.Join(' ', arguments)}{Environment.NewLine}" +
                  $"Exit code: {result.ExitCode}{Environment.NewLine}" +
                  $"Duration: {result.Duration}{Environment.NewLine}{Environment.NewLine}" +
                  result.StandardOutput + Environment.NewLine + result.StandardError;
        await File.WriteAllTextAsync(
            Path.Combine(artifactDirectory, $"iteration-{iteration:00}-{name}.log"),
            log,
            cancellationToken);
        return result;
    }

    private string ResolveExecutable()
    {
        if (task.BuildSystem.Equals("gradle", StringComparison.OrdinalIgnoreCase))
        {
            var wrapper = Path.Combine(workspacePath, OperatingSystem.IsWindows() ? "gradlew.bat" : "gradlew");
            if (!File.Exists(wrapper))
            {
                throw new FileNotFoundException("Gradle tasks require a Gradle Wrapper at repository root.", wrapper);
            }

            return wrapper;
        }

        var mavenWrapper = Path.Combine(workspacePath, OperatingSystem.IsWindows() ? "mvnw.cmd" : "mvnw");
        return File.Exists(mavenWrapper)
            ? mavenWrapper
            : OperatingSystem.IsWindows() ? "mvn.cmd" : "mvn";
    }

    private string[] MavenCompileOrTestArguments(params string[] arguments) =>
        task.ProjectId.Contains("checkstyle", StringComparison.OrdinalIgnoreCase)
            ? ["-Djacoco.skip=true", .. arguments]
            : arguments;

    private string[] MavenMutationArguments(IReadOnlyList<string> focalClasses, string plugin) =>
    [
        "-q",
        "-Dstyle.color=never",
        "test-compile",
        $"-DtargetClasses={string.Join(',', focalClasses)}",
        $"-DtargetTests={task.TestClassName}",
        "-DoutputFormats=XML",
        "-DtimestampedReports=false",
        $"{plugin}:mutationCoverage"
    ];

    internal static string ResolveWithin(string parent, string relativePath)
    {
        var absoluteParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar);
        var candidate = string.IsNullOrWhiteSpace(relativePath)
            ? absoluteParent
            : Path.GetFullPath(Path.Combine(absoluteParent, relativePath));
        if (!candidate.Equals(absoluteParent, StringComparison.OrdinalIgnoreCase) &&
            !candidate.StartsWith(absoluteParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Path escapes the run workspace: {relativePath}");
        }

        return candidate;
    }

    private static string ResolvePitReportPath(string configuredPath)
    {
        if (File.Exists(configuredPath)) return configuredPath;
        var directory = Path.GetDirectoryName(configuredPath);
        if (directory is null || !Directory.Exists(directory)) return configuredPath;
        return Directory.EnumerateFiles(directory, "mutations.xml", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault() ?? configuredPath;
    }

    private static void ValidateTestIdentifier(string value)
    {
        if (!SimpleIdentifierRegex().IsMatch(value))
        {
            throw new InvalidOperationException($"Unsafe test identifier: {value}");
        }
    }

    private static void ValidateCompositeTestIdentifier(string value)
    {
        if (!CompositeIdentifierRegex().IsMatch(value))
        {
            throw new InvalidOperationException($"Unsafe verified test identifier: {value}");
        }
    }

    private static string SanitizeFileName(string value) =>
        string.Concat(value.Select(character => char.IsLetterOrDigit(character) ? character : '-'));

    [GeneratedRegex(@"^[A-Za-z_$][A-Za-z0-9_.$]*$")]
    private static partial Regex SimpleIdentifierRegex();

    [GeneratedRegex(@"^[A-Za-z_$][A-Za-z0-9_.$]*(?:#[A-Za-z_$][A-Za-z0-9_$]*)?$")]
    private static partial Regex CompositeIdentifierRegex();

    [GeneratedRegex(@"<argLine>(?<value>.*?)</argLine>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex MavenArgLineRegex();

    private sealed record TemporaryFilePatch(string Path, string OriginalContent);
}
