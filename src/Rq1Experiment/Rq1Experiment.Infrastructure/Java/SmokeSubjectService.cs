using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rq1Experiment.Contracts.Subjects;
using Rq1Experiment.Core.Abstractions;
using Rq1Experiment.Infrastructure.Persistence;

namespace Rq1Experiment.Infrastructure.Java;

public sealed class SmokeSubjectService(IProcessRunner processRunner) : ISmokeSubjectService
{
    private static readonly JsonSerializerOptions CompactJson = new(JsonSupport.Options) { WriteIndented = false };

    public async Task<SmokeMethodDiscoveryReport> DiscoverAsync(
        SmokeMethodDiscoveryRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var repository = Path.GetFullPath(request.RepositoryRoot);
        var output = Path.GetFullPath(request.OutputRoot);
        if (!Directory.Exists(repository))
            throw new DirectoryNotFoundException("Java repository does not exist: " + repository);
        Directory.CreateDirectory(output);

        var analyzerRoot = Path.Combine(request.WorkspaceRoot, "tools", "rq1", "java-structural-analyzer");
        var analyzerJar = Path.Combine(analyzerRoot, "target", "java-structural-analyzer.jar");
        var environment = BuildJavaEnvironment(request.JavaHome);
        progress?.Report("Building JavaParser analyzer for Smoke repository discovery...");
        var maven = BundledBuildToolResolver.ResolveMaven(request.WorkspaceRoot, repository);
        var pom = Path.Combine(analyzerRoot, "pom.xml");
        var arguments = new[] { "-B", "-ntp", "package", "-f", pom };
        ProcessRunResult build;
        if (OperatingSystem.IsWindows() && maven.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
            build = await processRunner.RunAsync("cmd.exe", new[] { "/d", "/c", "call", maven }.Concat(arguments),
                request.WorkspaceRoot, TimeSpan.FromMinutes(5), environment, cancellationToken);
        else
            build = await processRunner.RunAsync(maven, arguments, request.WorkspaceRoot,
                TimeSpan.FromMinutes(5), environment, cancellationToken);
        await WriteLogAsync(Path.Combine(output, "smoke-analyzer-build.log"), build, cancellationToken);
        if (!build.IsSuccess || !File.Exists(analyzerJar))
            throw new InvalidOperationException("Smoke structural analyzer build failed; inspect smoke-analyzer-build.log.");

        progress?.Report("Scanning production Java methods in the selected repository...");
        var raw = Path.Combine(output, "smoke-candidates.jsonl");
        var java = string.IsNullOrWhiteSpace(request.JavaHome) ? "java" :
            Path.Combine(request.JavaHome, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
        var run = await processRunner.RunAsync(java,
            ["-jar", analyzerJar, "--repository-root", repository, "--output", raw, "--revision", request.Revision],
            request.WorkspaceRoot, TimeSpan.FromMinutes(8), environment, cancellationToken);
        await WriteLogAsync(Path.Combine(output, "smoke-analyzer-run.log"), run, cancellationToken);
        if (!run.IsSuccess)
            throw new InvalidOperationException("Smoke repository analysis failed; inspect smoke-analyzer-run.log.");

        var candidates = await ReadLinesAsync<SubjectCandidate>(raw, cancellationToken);
        var report = new SmokeMethodDiscoveryReport
        {
            RepositoryRoot = repository,
            ProjectId = Path.GetFileName(Path.TrimEndingDirectorySeparator(repository)),
            ParsedMethodCount = candidates.Count,
            EligibleMethodCount = candidates.Count(item => item.Eligible),
            Candidates = candidates.OrderBy(item => item.FocalClass, StringComparer.Ordinal)
                .ThenBy(item => item.FocalMethodSignature, StringComparer.Ordinal)
                .ThenBy(item => item.StartLine).ToList(),
            GeneratedAt = DateTimeOffset.UtcNow
        };
        await JsonSupport.WriteAsync(Path.Combine(output, "smoke-discovery-report.json"), report, cancellationToken);
        progress?.Report($"Smoke discovery completed: {report.ParsedMethodCount} methods, {report.EligibleMethodCount} eligible.");
        return report;
    }

    public async Task<FocalMethodSubject> FreezeAsync(
        SmokeSubjectFreezeRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var report = await DiscoverAsync(request.Discovery, progress, cancellationToken);
        var candidate = report.Candidates.SingleOrDefault(item =>
            item.CandidateKey.Equals(request.CandidateKey, StringComparison.Ordinal));
        if (candidate is null)
            throw new InvalidOperationException("The selected Smoke focal method no longer exists. Analyze the repository and select it again.");
        var subject = new FocalMethodSubject
        {
            CandidateKey = candidate.CandidateKey,
            ProjectId = candidate.ProjectId,
            Revision = candidate.Revision,
            ModulePath = string.IsNullOrWhiteSpace(request.Discovery.ModulePath) ? "." : request.Discovery.ModulePath,
            SourceFile = candidate.SourceFile,
            FocalClass = candidate.FocalClass,
            FocalMethodSignature = candidate.FocalMethodSignature,
            StartLine = candidate.StartLine,
            EndLine = candidate.EndLine,
            Loc = candidate.Loc,
            CyclomaticComplexity = candidate.CyclomaticComplexity,
            ComplexityStratum = candidate.ComplexityStratum,
            Visibility = candidate.Visibility,
            ExplicitExceptionPath = candidate.ExplicitExceptionPath,
            Eligible = candidate.Eligible,
            ExclusionReasons = candidate.ExclusionReasons.ToList(),
            SourceSha256 = candidate.SourceSha256,
            SubjectId = "SMOKE-" + Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(candidate.CandidateKey + "|" + candidate.SourceSha256)))[..10],
            RepositoryPath = report.RepositoryRoot,
            Split = "SMOKE",
            SelectionSeed = 0,
            ManualReviewStatus = candidate.Eligible ? "APPROVED" : "DIAGNOSTIC_SMOKE_ONLY"
        };
        var manifest = Path.Combine(request.Discovery.OutputRoot, "subjects-smoke.jsonl");
        await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(subject, CompactJson) + "\n", cancellationToken);
        await JsonSupport.WriteAsync(Path.Combine(request.Discovery.OutputRoot, "smoke-subject-report.json"), new
        {
            subject.SubjectId,
            subject.CandidateKey,
            subject.FocalClass,
            subject.FocalMethodSignature,
            subject.SourceFile,
            subject.SourceSha256,
            subject.Eligible,
            subject.ExclusionReasons,
            subject.ManualReviewStatus,
            repositoryRoot = report.RepositoryRoot,
            frozenAt = DateTimeOffset.UtcNow
        }, cancellationToken);
        progress?.Report($"Frozen Smoke subject: {subject.FocalClass}.{subject.FocalMethodSignature}.");
        return subject;
    }

    private static async Task<List<T>> ReadLinesAsync<T>(string path, CancellationToken token)
    {
        var result = new List<T>();
        foreach (var line in await File.ReadAllLinesAsync(path, token))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            result.Add(JsonSerializer.Deserialize<T>(line, JsonSupport.Options)
                       ?? throw new InvalidDataException("Invalid JSONL: " + path));
        }
        return result;
    }

    private static IReadOnlyDictionary<string, string?> BuildJavaEnvironment(string javaHome)
    {
        var values = new Dictionary<string, string?>();
        if (string.IsNullOrWhiteSpace(javaHome)) return values;
        values["JAVA_HOME"] = javaHome;
        values["PATH"] = Path.Combine(javaHome, "bin") + Path.PathSeparator
                         + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
        return values;
    }

    private static Task WriteLogAsync(string path, ProcessRunResult result, CancellationToken token) =>
        File.WriteAllTextAsync(path,
            $"exitCode={result.ExitCode}\ntimedOut={result.TimedOut}\nduration={result.Duration}\n\nSTDOUT\n{result.StandardOutput}\n\nSTDERR\n{result.StandardError}", token);
}
