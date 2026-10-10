using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rq1Experiment.Contracts.Subjects;
using Rq1Experiment.Core.Abstractions;
using Rq1Experiment.Infrastructure.Persistence;

namespace Rq1Experiment.Infrastructure.Java;

public sealed class SubjectSelectionService(IProcessRunner processRunner) : ISubjectSelectionService
{
    private static readonly string[] Strata = ["LOW", "MEDIUM", "HIGH"];
    private static readonly JsonSerializerOptions JsonLinesOptions = new(JsonSupport.Options)
    {
        WriteIndented = false
    };

    public async Task<SubjectSelectionReport> RunAsync(
        SubjectSelectionRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var projectsRoot = Path.GetFullPath(request.ProjectsRoot);
        var outputRoot = Path.GetFullPath(request.OutputRoot);
        var analyzerRoot = Path.Combine(request.WorkspaceRoot, "tools", "rq1", "java-structural-analyzer");
        var analyzerJar = Path.Combine(analyzerRoot, "target", "java-structural-analyzer.jar");
        if (!Directory.Exists(projectsRoot))
            throw new DirectoryNotFoundException("ProjectTest Java projects root not found: " + projectsRoot);
        Directory.CreateDirectory(outputRoot);

        var environment = BuildJavaEnvironment(request.JavaHome);
        var mavenExecutable = BundledBuildToolResolver.ResolveMaven(request.WorkspaceRoot, projectsRoot);
        progress?.Report($"Building deterministic JavaParser structural analyzer with {mavenExecutable}...");
        var analyzerPom = Path.Combine(analyzerRoot, "pom.xml");
        var mavenPomArgument = OperatingSystem.IsWindows()
            ? Path.GetRelativePath(request.WorkspaceRoot, analyzerPom)
            : analyzerPom;
        var mavenArguments = new[] { "-B", "-ntp", "package", "-f", mavenPomArgument };
        var mavenCommand = OperatingSystem.IsWindows()
            ? ToWorkspaceRelativeExecutable(mavenExecutable, request.WorkspaceRoot)
            : mavenExecutable;
        var mavenInvocation = OperatingSystem.IsWindows() && mavenExecutable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            ? (Executable: "cmd.exe", Arguments: new[] { "/d", "/c", "call", mavenCommand }.Concat(mavenArguments).ToArray())
            : (Executable: mavenCommand, Arguments: mavenArguments);
        var analyzerBuild = await processRunner.RunAsync(
            mavenInvocation.Executable, mavenInvocation.Arguments,
            request.WorkspaceRoot, TimeSpan.FromMinutes(5), environment, cancellationToken);
        await WriteToolLogAsync(outputRoot, "subject-analyzer-build.log", analyzerBuild, cancellationToken);
        if (!analyzerBuild.IsSuccess || !File.Exists(analyzerJar))
            throw new InvalidOperationException("Java structural analyzer build failed; see subject-analyzer-build.log.");

        var rawPool = Path.Combine(outputRoot, "candidate-pool.raw.jsonl");
        progress?.Report("Extracting exact method signatures and structural metrics with AST...");
        var javaExecutable = string.IsNullOrWhiteSpace(request.JavaHome)
            ? "java"
            : Path.Combine(request.JavaHome, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
        var extraction = await processRunner.RunAsync(
            javaExecutable,
            ["-jar", analyzerJar, "--projects-root", projectsRoot, "--output", rawPool, "--revision", request.Revision],
            request.WorkspaceRoot, TimeSpan.FromMinutes(5), environment, cancellationToken);
        await WriteToolLogAsync(outputRoot, "subject-analyzer-run.log", extraction, cancellationToken);
        if (!extraction.IsSuccess)
            throw new InvalidOperationException("Java structural extraction failed; see subject-analyzer-run.log.");

        var candidates = await ReadJsonLinesAsync<SubjectCandidate>(rawPool, cancellationToken);
        var candidatePoolPath = Path.Combine(outputRoot, "candidate-pool.jsonl");
        await WriteJsonLinesAsync(candidatePoolPath, candidates, cancellationToken);
        var eligible = candidates.Where(item => item.Eligible).ToList();
        if (eligible.Count == 0) throw new InvalidOperationException("No eligible focal method candidate was extracted.");

        progress?.Report("Selecting deterministic stratified official and pilot method subjects...");
        var pilotCandidates = SelectStratified(
            eligible,
            request.PilotCount,
            request.MaxPerProject,
            request.SelectionSeed + 1,
            excludedKeys: new HashSet<string>(StringComparer.Ordinal));
        var pilotKeys = pilotCandidates.Select(item => item.CandidateKey).ToHashSet(StringComparer.Ordinal);
        var officialCandidates = SelectStratified(
            eligible,
            request.OfficialCount,
            request.MaxPerProject,
            request.SelectionSeed,
            pilotKeys);

        var official = CreateSubjects(
            officialCandidates, "OFFICIAL", "PT-O", request, projectsRoot);
        var pilot = CreateSubjects(
            pilotCandidates, "PILOT", "PT-P", request, projectsRoot);
        var selectedSubjects = pilot.Concat(official).ToList();
        var manifestFingerprint = StableScore(0, string.Join('\n', selectedSubjects
            .OrderBy(item => item.SubjectId, StringComparer.Ordinal)
            .Select(item => item.SubjectId + "|" + item.CandidateKey + "|" + item.SourceSha256)));
        var manualReview = await EnsureManualReviewAsync(
            outputRoot, selectedSubjects, manifestFingerprint, cancellationToken);
        foreach (var subject in selectedSubjects)
            subject.ManualReviewStatus = manualReview.Statuses[subject.SubjectId];

        var pilotPath = Path.Combine(outputRoot, "subjects-pilot.jsonl");
        var officialPath = Path.Combine(outputRoot, "subjects-official.jsonl");
        await WriteJsonLinesAsync(pilotPath, pilot, cancellationToken);
        await WriteJsonLinesAsync(officialPath, official, cancellationToken);

        await WriteSummaryCsvAsync(outputRoot, candidates, pilot, official, cancellationToken);
        var candidatePoolHash = await Sha256Async(candidatePoolPath, cancellationToken);
        var pilotManifestHash = await Sha256Async(pilotPath, cancellationToken);
        var officialManifestHash = await Sha256Async(officialPath, cancellationToken);
        var parseErrorsPath = Path.Combine(outputRoot, "candidate-parse-errors.txt");
        var parseErrorCount = File.Exists(parseErrorsPath)
            ? (await File.ReadAllLinesAsync(parseErrorsPath, cancellationToken)).Count(line => !string.IsNullOrWhiteSpace(line))
            : 0;
        var disjoint = !pilot.Select(item => item.CandidateKey)
            .Intersect(official.Select(item => item.CandidateKey), StringComparer.Ordinal).Any();
        var uniqueSignatures = pilot.Concat(official)
            .GroupBy(item => (item.ProjectId, item.FocalClass, item.FocalMethodSignature, item.StartLine))
            .All(group => group.Count() == 1);
        var availableStrata = eligible.Select(item => item.ComplexityStratum).ToHashSet(StringComparer.Ordinal);
        var pilotCoversAvailableStrata = availableStrata.All(
            stratum => pilot.Any(item => item.ComplexityStratum == stratum));
        var report = new SubjectSelectionReport
        {
            Revision = request.Revision,
            ParsedMethodCount = candidates.Count,
            EligibleCandidateCount = eligible.Count,
            ExcludedCandidateCount = candidates.Count - eligible.Count,
            ParseErrorCount = parseErrorCount,
            PilotCount = pilot.Count,
            OfficialCount = official.Count,
            RequestedOfficialCount = request.OfficialCount,
            MaxPerProject = request.MaxPerProject,
            SelectionSeed = request.SelectionSeed,
            PilotOfficialDisjoint = disjoint,
            ExactSignaturesUnique = uniqueSignatures,
            PilotCoversAvailableStrata = pilotCoversAvailableStrata,
            AutomatedSelectionComplete = pilot.Count == request.PilotCount
                                         && disjoint
                                         && uniqueSignatures
                                         && pilotCoversAvailableStrata
                                         && parseErrorCount == 0,
            ManualReviewComplete = manualReview.IsComplete,
            ManualReviewApprovedCount = manualReview.Approved,
            ManualReviewPendingCount = manualReview.Pending,
            ManualReviewRejectedCount = manualReview.Rejected,
            DocumentedShortfall = official.Count < request.OfficialCount
                ? $"Requested {request.OfficialCount}, selected {official.Count}. The frozen max of {request.MaxPerProject} methods/project and current eligible ProjectTest pool prevent a larger unbiased sample."
                : null,
            CandidatePoolHash = candidatePoolHash,
            PilotManifestHash = pilotManifestHash,
            OfficialManifestHash = officialManifestHash,
            ManifestFingerprint = manifestFingerprint,
            GeneratedAt = DateTimeOffset.UtcNow
        };
        await File.WriteAllTextAsync(
            Path.Combine(outputRoot, "subject-selection-report.json"),
            JsonSerializer.Serialize(report, JsonSupport.Options),
            cancellationToken);
        progress?.Report($"Subject selection generated: pilot={pilot.Count}, official={official.Count}, "
                         + $"manual review approved={manualReview.Approved}, pending={manualReview.Pending}, rejected={manualReview.Rejected}.");
        return report;
    }

    private static List<SubjectCandidate> SelectStratified(
        IReadOnlyCollection<SubjectCandidate> candidates,
        int requestedCount,
        int maxPerProject,
        int seed,
        IReadOnlySet<string> excludedKeys)
    {
        var queues = Strata.ToDictionary(
            stratum => stratum,
            stratum => new Queue<SubjectCandidate>(candidates
                .Where(item => item.ComplexityStratum == stratum && !excludedKeys.Contains(item.CandidateKey))
                .OrderBy(item => StableScore(seed, item.CandidateKey), StringComparer.Ordinal)),
            StringComparer.Ordinal);
        var selected = new List<SubjectCandidate>();
        var projectCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        while (selected.Count < requestedCount)
        {
            var madeProgress = false;
            foreach (var stratum in Strata)
            {
                while (queues[stratum].Count > 0)
                {
                    var candidate = queues[stratum].Dequeue();
                    projectCounts.TryGetValue(candidate.ProjectId, out var count);
                    if (count >= maxPerProject) continue;
                    selected.Add(candidate);
                    projectCounts[candidate.ProjectId] = count + 1;
                    madeProgress = true;
                    break;
                }
                if (selected.Count == requestedCount) break;
            }
            if (!madeProgress) break;
        }
        return selected;
    }

    private static List<FocalMethodSubject> CreateSubjects(
        IEnumerable<SubjectCandidate> selected,
        string split,
        string idPrefix,
        SubjectSelectionRequest request,
        string projectsRoot)
    {
        return selected
            .OrderBy(item => item.ProjectId, StringComparer.Ordinal)
            .ThenBy(item => item.FocalClass, StringComparer.Ordinal)
            .ThenBy(item => item.FocalMethodSignature, StringComparer.Ordinal)
            .ThenBy(item => item.StartLine)
            .Select((item, index) => new FocalMethodSubject
            {
                SubjectId = $"{idPrefix}-{index + 1:000}",
                RepositoryPath = PortablePath(Path.GetRelativePath(
                    request.WorkspaceRoot,
                    Path.Combine(projectsRoot, item.ProjectId))),
                Split = split,
                SelectionSeed = request.SelectionSeed,
                ManualReviewStatus = "PENDING",
                CandidateKey = item.CandidateKey,
                ProjectId = item.ProjectId,
                Revision = item.Revision,
                ModulePath = item.ModulePath,
                SourceFile = item.SourceFile,
                FocalClass = item.FocalClass,
                FocalMethodSignature = item.FocalMethodSignature,
                StartLine = item.StartLine,
                EndLine = item.EndLine,
                Loc = item.Loc,
                CyclomaticComplexity = item.CyclomaticComplexity,
                ComplexityStratum = item.ComplexityStratum,
                Visibility = item.Visibility,
                ExplicitExceptionPath = item.ExplicitExceptionPath,
                Eligible = item.Eligible,
                ExclusionReasons = item.ExclusionReasons,
                SourceSha256 = item.SourceSha256
            }).ToList();
    }

    private static async Task WriteSummaryCsvAsync(
        string outputRoot,
        IReadOnlyCollection<SubjectCandidate> candidates,
        IReadOnlyCollection<FocalMethodSubject> pilot,
        IReadOnlyCollection<FocalMethodSubject> official,
        CancellationToken cancellationToken)
    {
        var csv = new StringBuilder("split,stratum,subject_count,project_count\n");
        foreach (var (name, values) in new[]
                 {
                     ("PILOT", pilot.AsEnumerable()),
                     ("OFFICIAL", official.AsEnumerable())
                 })
        {
            foreach (var stratum in Strata)
            {
                var items = values.Where(item => item.ComplexityStratum == stratum).ToList();
                csv.Append(name).Append(',').Append(stratum).Append(',')
                    .Append(items.Count).Append(',')
                    .Append(items.Select(item => item.ProjectId).Distinct().Count()).AppendLine();
            }
        }
        csv.Append("CANDIDATE_POOL,ALL,").Append(candidates.Count).Append(',')
            .Append(candidates.Select(item => item.ProjectId).Distinct().Count()).AppendLine();
        await File.WriteAllTextAsync(
            Path.Combine(outputRoot, "manifest-summary.csv"), csv.ToString(), cancellationToken);
    }

    private static async Task<ManualReviewResult> EnsureManualReviewAsync(
        string outputRoot,
        IReadOnlyCollection<FocalMethodSubject> subjects,
        string manifestFingerprint,
        CancellationToken cancellationToken)
    {
        var reviewPath = Path.Combine(outputRoot, "manual-review.csv");
        var metadataPath = Path.Combine(outputRoot, "manual-review.meta.json");
        if (File.Exists(reviewPath) && File.Exists(metadataPath))
        {
            var metadata = JsonSerializer.Deserialize<ManualReviewMetadata>(
                await File.ReadAllTextAsync(metadataPath, cancellationToken), JsonSupport.Options);
            if (string.Equals(metadata?.ManifestFingerprint, manifestFingerprint, StringComparison.Ordinal))
            {
                var existing = await ReadManualReviewAsync(reviewPath, subjects, cancellationToken);
                if (existing is not null) return existing;
            }
        }

        // A new analyzer gate can change subject IDs while leaving most focal
        // methods unchanged. Preserve prior human decisions by stable method
        // identity; only genuinely new/replacement subjects require review.
        var priorReviews = File.Exists(reviewPath)
            ? await ReadPriorManualReviewsAsync(reviewPath, cancellationToken)
            : new Dictionary<string, PriorManualReview>(StringComparer.Ordinal);

        var csv = new StringBuilder(
            "subject_id,split,project_id,focal_class,focal_method_signature,source_file,start_line,end_line,review_status,reviewer,notes\n");
        var statuses = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in subjects.OrderBy(item => item.SubjectId, StringComparer.Ordinal))
        {
            var prior = priorReviews.GetValueOrDefault(ManualReviewIdentity(item));
            var status = prior?.Status is "APPROVED" or "REJECTED" or "EXCLUDED"
                ? prior.Status
                : "PENDING";
            statuses[item.SubjectId] = status;
            csv.Append(Csv(item.SubjectId)).Append(',').Append(Csv(item.Split)).Append(',')
                .Append(Csv(item.ProjectId)).Append(',').Append(Csv(item.FocalClass)).Append(',')
                .Append(Csv(item.FocalMethodSignature)).Append(',').Append(Csv(item.SourceFile)).Append(',')
                .Append(item.StartLine).Append(',').Append(item.EndLine).Append(',')
                .Append(Csv(status)).Append(',')
                .Append(Csv(prior?.Reviewer ?? string.Empty)).Append(',')
                .Append(Csv(prior?.Notes ?? string.Empty)).AppendLine();
        }
        await File.WriteAllTextAsync(reviewPath, csv.ToString(), cancellationToken);
        await File.WriteAllTextAsync(
            metadataPath,
            JsonSerializer.Serialize(new ManualReviewMetadata { ManifestFingerprint = manifestFingerprint }, JsonSupport.Options),
            cancellationToken);
        var approved = statuses.Values.Count(value => value == "APPROVED");
        var rejected = statuses.Values.Count(value => value is "REJECTED" or "EXCLUDED");
        var pending = statuses.Count - approved - rejected;
        return new ManualReviewResult(
            pending == 0 && rejected == 0, approved, pending, rejected, statuses);
    }

    private static async Task<Dictionary<string, PriorManualReview>> ReadPriorManualReviewsAsync(
        string path, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, PriorManualReview>(StringComparer.Ordinal);
        var lines = await File.ReadAllLinesAsync(path, cancellationToken);
        foreach (var line in lines.Skip(1).Where(line => !string.IsNullOrWhiteSpace(line)))
        {
            var fields = ParseCsvLine(line);
            if (fields.Count < 11) continue;
            var identity = ManualReviewIdentity(
                fields[2], fields[3], fields[4], fields[5], fields[6], fields[7]);
            result[identity] = new PriorManualReview(
                fields[8].Trim().ToUpperInvariant(), fields[9], fields[10]);
        }
        return result;
    }

    private static string ManualReviewIdentity(FocalMethodSubject subject) => ManualReviewIdentity(
        subject.ProjectId, subject.FocalClass, subject.FocalMethodSignature,
        subject.SourceFile, subject.StartLine.ToString(), subject.EndLine.ToString());

    private static string ManualReviewIdentity(
        string projectId, string focalClass, string focalMethod, string sourceFile,
        string startLine, string endLine) => string.Join('|',
        projectId.Trim(), focalClass.Trim(), focalMethod.Trim(),
        sourceFile.Replace('\\', '/').Trim(), startLine.Trim(), endLine.Trim());

    private static async Task<ManualReviewResult?> ReadManualReviewAsync(
        string path,
        IReadOnlyCollection<FocalMethodSubject> subjects,
        CancellationToken cancellationToken)
    {
        var expectedIds = subjects.Select(item => item.SubjectId).ToHashSet(StringComparer.Ordinal);
        var statuses = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = await File.ReadAllLinesAsync(path, cancellationToken);
        foreach (var line in lines.Skip(1).Where(line => !string.IsNullOrWhiteSpace(line)))
        {
            var fields = ParseCsvLine(line);
            if (fields.Count < 9 || !expectedIds.Contains(fields[0])) return null;
            statuses[fields[0]] = fields[8].Trim().ToUpperInvariant();
        }
        if (statuses.Count != expectedIds.Count) return null;
        var approved = statuses.Values.Count(value => value == "APPROVED");
        var rejected = statuses.Values.Count(value => value is "REJECTED" or "EXCLUDED");
        var pending = statuses.Count - approved - rejected;
        return new ManualReviewResult(
            pending == 0 && rejected == 0, approved, pending, rejected, statuses);
    }

    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    current.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (character == ',' && !quoted)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(character);
            }
        }
        fields.Add(current.ToString());
        return fields;
    }

    private static async Task<List<T>> ReadJsonLinesAsync<T>(string path, CancellationToken cancellationToken)
    {
        var result = new List<T>();
        foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            result.Add(JsonSerializer.Deserialize<T>(line, JsonLinesOptions)
                       ?? throw new InvalidDataException("Invalid JSONL record in " + path));
        }
        return result;
    }

    private static async Task WriteJsonLinesAsync<T>(
        string path, IEnumerable<T> values, CancellationToken cancellationToken)
    {
        var content = string.Join('\n', values.Select(value => JsonSerializer.Serialize(value, JsonLinesOptions))) + "\n";
        await File.WriteAllTextAsync(path, content, cancellationToken);
    }

    private static async Task WriteToolLogAsync(
        string outputRoot, string name, ProcessRunResult result, CancellationToken cancellationToken)
    {
        var content = $"ExitCode: {result.ExitCode}\nTimedOut: {result.TimedOut}\nDuration: {result.Duration}\n"
                      + "--- STDOUT ---\n" + result.StandardOutput
                      + "\n--- STDERR ---\n" + result.StandardError;
        await File.WriteAllTextAsync(Path.Combine(outputRoot, name), content, cancellationToken);
    }

    private static Dictionary<string, string?> BuildJavaEnvironment(string javaHome)
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["LANG"] = "C",
            ["LC_ALL"] = "C",
            ["JAVA_TOOL_OPTIONS"] = "-Duser.language=en -Duser.country=US -Duser.variant="
        };
        if (string.IsNullOrWhiteSpace(javaHome)) return environment;
        environment["JAVA_HOME"] = javaHome;
        environment["PATH"] = Path.Combine(javaHome, "bin") + Path.PathSeparator
                              + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
        return environment;
    }

    private static string StableScore(int seed, string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed + "|" + key)));

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static string PortablePath(string path) => path.Replace('\\', '/');
    private static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';

    private static string ToWorkspaceRelativeExecutable(string executable, string workspaceRoot)
    {
        if (!Path.IsPathRooted(executable)) return executable;
        var relative = Path.GetRelativePath(workspaceRoot, executable);
        return relative.Equals("..", StringComparison.Ordinal)
               || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? executable
            : relative;
    }

    private sealed class ManualReviewMetadata
    {
        public string ManifestFingerprint { get; set; } = string.Empty;
    }

    private sealed record PriorManualReview(string Status, string Reviewer, string Notes);

    private sealed record ManualReviewResult(
        bool IsComplete,
        int Approved,
        int Pending,
        int Rejected,
        IReadOnlyDictionary<string, string> Statuses);
}
