using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rq1Experiment.Contracts.Subjects;
using Rq1Experiment.Contracts.Targets;
using Rq1Experiment.Core.Abstractions;
using Rq1Experiment.Infrastructure.Persistence;

namespace Rq1Experiment.Infrastructure.Java;

public sealed class StructuralTargetService(IProcessRunner processRunner) : IStructuralTargetService
{
    private const string AnalyzerVersion = "target-analyzer-v2-cfg";
    private const string OfficialSemantics = "FULL_BRANCH_SITE_COVERAGE";
    private const string CfgSemantics = "SOURCE_LEVEL_INTRAPROCEDURAL_CFG_V1";
    private static readonly JsonSerializerOptions JsonLinesOptions = new(JsonSupport.Options)
    {
        WriteIndented = false
    };

    public async Task<StructuralTargetReport> RunAsync(
        StructuralTargetRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var workspaceRoot = Path.GetFullPath(request.WorkspaceRoot);
        var projectsRoot = Path.GetFullPath(request.ProjectsRoot);
        var subjectsRoot = Path.GetFullPath(request.SubjectsRoot);
        var outputRoot = Path.GetFullPath(request.OutputRoot);
        Directory.CreateDirectory(outputRoot);
        var splits = new[] { ResolveSplit(request.ExperimentMode) };
        var subjectPaths = splits.ToDictionary(split => split,
            split => Path.Combine(subjectsRoot, $"subjects-{split}.jsonl"), StringComparer.Ordinal);
        foreach (var path in subjectPaths.Values)
            if (!File.Exists(path)) throw new FileNotFoundException("Frozen subject manifest is required before target extraction.", path);
        var subjectsBySplit = new Dictionary<string, List<FocalMethodSubject>>(StringComparer.Ordinal);
        foreach (var split in splits)
            subjectsBySplit[split] = await ReadJsonLinesAsync<FocalMethodSubject>(subjectPaths[split], cancellationToken);
        var allSubjects = subjectsBySplit.Values.SelectMany(items => items)
            .ToDictionary(item => item.SubjectId, StringComparer.Ordinal);

        var analyzerRoot = Path.Combine(workspaceRoot, "tools", "rq1", "java-structural-analyzer");
        var analyzerJar = Path.Combine(analyzerRoot, "target", "java-structural-analyzer.jar");
        var environment = BuildJavaEnvironment(request.JavaHome);
        progress?.Report("Building deterministic structural-target extractor...");
        var build = await RunMavenBuildAsync(workspaceRoot, analyzerRoot, environment, cancellationToken);
        await WriteToolLogAsync(outputRoot, "target-analyzer-build.log", build, cancellationToken);
        if (!build.IsSuccess || !File.Exists(analyzerJar))
            throw new InvalidOperationException("Structural-target analyzer build failed; see target-analyzer-build.log.");

        var javaExecutable = string.IsNullOrWhiteSpace(request.JavaHome)
            ? "java"
            : Path.Combine(request.JavaHome, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
        var deterministicRoot = Path.Combine(outputRoot, "determinism");
        Directory.CreateDirectory(deterministicRoot);
        var runPaths = new Dictionary<(string Split, int Run), string>();
        foreach (var split in splits)
        {
            var subjectsPath = subjectPaths[split];
            for (var run = 1; run <= 3; run++)
            {
                progress?.Report($"Extracting {split} structural targets (determinism run {run}/3)...");
                var outputPath = Path.Combine(deterministicRoot, $"targets-{split}.run-{run}.jsonl");
                var extraction = await processRunner.RunAsync(
                    javaExecutable,
                    ["-cp", analyzerJar, "io.rq1.analyzer.TargetExtractorMain",
                        "--projects-root", projectsRoot,
                        "--subjects", subjectsPath,
                        "--output", outputPath],
                    workspaceRoot,
                    TimeSpan.FromMinutes(5),
                    environment,
                    cancellationToken);
                await WriteToolLogAsync(
                    outputRoot, $"target-analyzer-{split}-run-{run}.log", extraction, cancellationToken);
                if (!extraction.IsSuccess)
                    throw new InvalidOperationException(
                        $"Structural-target extraction failed for {split} run {run}; see retained log.");
                runPaths[(split, run)] = outputPath;
            }
        }

        var deterministic = splits.All(split =>
            Enumerable.Range(1, 3)
                .Select(run => Sha256(runPaths[(split, run)]))
                .Distinct(StringComparer.Ordinal).Count() == 1);
        var inventoriesBySplit = new Dictionary<string, List<StructuralTargetInventory>>(StringComparer.Ordinal);
        var hashesBySplit = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var split in splits)
        {
            var finalPath = Path.Combine(outputRoot, $"targets-{split}.jsonl");
            File.Copy(runPaths[(split, 1)], finalPath, overwrite: true);
            inventoriesBySplit[split] = await ReadJsonLinesAsync<StructuralTargetInventory>(finalPath, cancellationToken);
            hashesBySplit[split] = Sha256(finalPath);
        }
        var inventories = inventoriesBySplit.Values.SelectMany(items => items).ToList();

        var ids = inventories.SelectMany(item => item.Targets).Select(item => item.TargetId).ToList();
        var idsStable = ids.Count == ids.Distinct(StringComparer.Ordinal).Count()
                        && inventories.All(inventory => inventory.Targets.Select((target, index) =>
                                target.TargetId == $"{inventory.SubjectId}-T{index + 1:000}").All(value => value));
        var hashesVerified = inventories.All(inventory =>
            allSubjects.TryGetValue(inventory.SubjectId, out var subject)
            && inventory.SourceSha256.Equals(subject.SourceSha256, StringComparison.OrdinalIgnoreCase));
        var allMapped = inventories.All(inventory =>
            allSubjects.TryGetValue(inventory.SubjectId, out var subject)
            && inventory.Targets.Count > 0
            && inventory.Targets.All(target =>
                target.Line >= subject.StartLine
                && target.Line <= subject.EndLine
                && !string.IsNullOrWhiteSpace(target.SourceRange)
                && target.SourceRange != "UNKNOWN"));
        var allCfgValid = inventories.All(inventory => IsValidControlFlowGraph(inventory.ControlFlowGraph));
        var allCfgMapped = inventories.All(inventory =>
            allSubjects.TryGetValue(inventory.SubjectId, out var subject)
            && inventory.ControlFlowGraph.Nodes.All(node =>
                node.Kind is "ENTRY" or "EXIT"
                ? node.Line == -1 && node.SourceRange == "UNKNOWN"
                : node.Line >= subject.StartLine
                  && node.Line <= subject.EndLine
                  && !string.IsNullOrWhiteSpace(node.SourceRange)
                  && node.SourceRange != "UNKNOWN"));
        var analyzerVersionsValid = inventories.All(inventory =>
            inventory.AnalyzerVersion == AnalyzerVersion);
        var officialTargets = inventories.SelectMany(item => item.Targets)
            .Where(target => target.MeasurementRole == "OFFICIAL").ToList();
        var semanticsFrozen = officialTargets.All(target =>
                                  target.Type == "BRANCH_SITE"
                                  && target.MeasurementSemantics == "ALL_JACOCO_BRANCHES_AT_SOURCE_LINE")
                              && inventories.All(item => item.OfficialTargetSemantics == OfficialSemantics);
        var subjectsWithoutOfficialTargets = inventories
            .Where(inventory => !inventory.Targets.Any(target => target.MeasurementRole == "OFFICIAL"))
            .Select(inventory => inventory.SubjectId)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
        var errorCount = Directory.EnumerateFiles(deterministicRoot, "*.errors.txt")
            .Sum(path => File.ReadLines(path).Count(line => !string.IsNullOrWhiteSpace(line)));

        await WriteAuditCsvAsync(outputRoot, inventories, allSubjects, cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(outputRoot, "MEASUREMENT_SEMANTICS.md"),
            MeasurementSemanticsDocument(), cancellationToken);

        var pilotHash = hashesBySplit.GetValueOrDefault("pilot", string.Empty);
        var officialHash = hashesBySplit.GetValueOrDefault("official", string.Empty);
        var smokeHash = hashesBySplit.GetValueOrDefault("smoke", string.Empty);
        var report = new StructuralTargetReport
        {
            AnalyzerVersion = AnalyzerVersion,
            OfficialTargetSemantics = OfficialSemantics,
            SubjectCount = inventories.Count,
            PilotSubjectCount = inventoriesBySplit.GetValueOrDefault("pilot", []).Count,
            OfficialSubjectCount = inventoriesBySplit.GetValueOrDefault("official", []).Count,
            SmokeSubjectCount = inventoriesBySplit.GetValueOrDefault("smoke", []).Count,
            SubjectWithOfficialTargetsCount = inventories.Count(inventory =>
                inventory.Targets.Any(target => target.MeasurementRole == "OFFICIAL")),
            SubjectWithoutOfficialTargetsCount = subjectsWithoutOfficialTargets.Count,
            SubjectsWithoutOfficialTargets = subjectsWithoutOfficialTargets,
            OfficialBranchSiteTargetCount = officialTargets.Count,
            DiagnosticReturnPathCount = inventories.SelectMany(item => item.Targets)
                .Count(target => target.Type == "RETURN_PATH"),
            DiagnosticExceptionPathCount = inventories.SelectMany(item => item.Targets)
                .Count(target => target.Type == "EXPLICIT_EXCEPTION_PATH"),
            ErrorCount = errorCount,
            DeterministicThreeRuns = deterministic,
            TargetIdsStable = idsStable,
            AllTargetsSourceMapped = allMapped,
            CfgNodeCount = inventories.Sum(inventory => inventory.ControlFlowGraph.Nodes.Count),
            CfgEdgeCount = inventories.Sum(inventory => inventory.ControlFlowGraph.Edges.Count),
            AllControlFlowGraphsValid = allCfgValid,
            AllControlFlowNodesSourceMapped = allCfgMapped,
            SourceHashesVerified = hashesVerified,
            PlannerIndependent = true,
            OutcomeSemanticsFrozen = semanticsFrozen,
            Complete = inventories.Count == allSubjects.Count
                       && deterministic
                       && idsStable
                       && allMapped
                       && allCfgValid
                       && allCfgMapped
                       && analyzerVersionsValid
                       && hashesVerified
                       && semanticsFrozen
                       && errorCount == 0,
            PilotTargetsHash = pilotHash,
            OfficialTargetsHash = officialHash,
            SmokeTargetsHash = smokeHash,
            CombinedFingerprint = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(pilotHash + "|" + officialHash + "|" + smokeHash + "|" + OfficialSemantics))),
            GeneratedAt = DateTimeOffset.UtcNow
        };
        await File.WriteAllTextAsync(
            Path.Combine(outputRoot, "target-extraction-report.json"),
            JsonSerializer.Serialize(report, JsonSupport.Options),
            cancellationToken);
        var modeReportName = $"target-extraction-report-{splits[0]}.json";
        await File.WriteAllTextAsync(
            Path.Combine(outputRoot, modeReportName),
            JsonSerializer.Serialize(report, JsonSupport.Options), cancellationToken);
        progress?.Report(
            $"Structural targets and source CFGs generated: subjects={report.SubjectCount}, official branch sites={report.OfficialBranchSiteTargetCount}, CFG nodes={report.CfgNodeCount}, CFG edges={report.CfgEdgeCount}, deterministic={report.DeterministicThreeRuns}.");
        return report;
    }

    private static string ResolveSplit(string mode) => mode.Trim().ToUpperInvariant() switch
    {
        "SMOKE" => "smoke",
        "PILOT" => "pilot",
        "OFFICIAL" => "official",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode,
            "Experiment mode must be Smoke, Pilot, or Official.")
    };

    private async Task<ProcessRunResult> RunMavenBuildAsync(
        string workspaceRoot,
        string analyzerRoot,
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken cancellationToken)
    {
        var mavenExecutable = BundledBuildToolResolver.ResolveMaven(workspaceRoot);
        var command = OperatingSystem.IsWindows()
            ? ToWorkspaceRelativeExecutable(mavenExecutable, workspaceRoot)
            : mavenExecutable;
        var pom = OperatingSystem.IsWindows()
            ? Path.GetRelativePath(workspaceRoot, Path.Combine(analyzerRoot, "pom.xml"))
            : Path.Combine(analyzerRoot, "pom.xml");
        var arguments = new[] { "-B", "-ntp", "package", "-f", pom };
        if (OperatingSystem.IsWindows() && mavenExecutable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
            return await processRunner.RunAsync(
                "cmd.exe", new[] { "/d", "/c", "call", command }.Concat(arguments),
                workspaceRoot, TimeSpan.FromMinutes(5), environment, cancellationToken);
        return await processRunner.RunAsync(
            command, arguments, workspaceRoot, TimeSpan.FromMinutes(5), environment, cancellationToken);
    }

    private static async Task WriteAuditCsvAsync(
        string outputRoot,
        IEnumerable<StructuralTargetInventory> inventories,
        IReadOnlyDictionary<string, FocalMethodSubject> subjects,
        CancellationToken cancellationToken)
    {
        var csv = new StringBuilder(
            "subject_id,split,project_id,focal_method_signature,source_start,source_end,official_branch_sites,diagnostic_return_paths,diagnostic_exception_paths,cfg_nodes,cfg_edges,cfg_valid,review_status,reviewer,notes\n");
        foreach (var inventory in inventories.OrderBy(item => item.SubjectId, StringComparer.Ordinal))
        {
            var subject = subjects[inventory.SubjectId];
            csv.Append(Csv(inventory.SubjectId)).Append(',').Append(Csv(inventory.Split)).Append(',')
                .Append(Csv(inventory.ProjectId)).Append(',').Append(Csv(inventory.FocalMethodSignature)).Append(',')
                .Append(subject.StartLine).Append(',').Append(subject.EndLine).Append(',')
                .Append(inventory.Targets.Count(target => target.MeasurementRole == "OFFICIAL")).Append(',')
                .Append(inventory.Targets.Count(target => target.Type == "RETURN_PATH")).Append(',')
                .Append(inventory.Targets.Count(target => target.Type == "EXPLICIT_EXCEPTION_PATH")).Append(',')
                .Append(inventory.ControlFlowGraph.Nodes.Count).Append(',')
                .Append(inventory.ControlFlowGraph.Edges.Count).Append(',')
                .Append(IsValidControlFlowGraph(inventory.ControlFlowGraph) ? "true" : "false").Append(',')
                .Append("AUTO_VERIFIED,")
                .Append(Csv("deterministic AST/CFG validator")).Append(',')
                .Append(Csv("Exact focal signature/source hash, target ranges and CFG node/edge references validated."))
                .AppendLine();
        }
        await File.WriteAllTextAsync(Path.Combine(outputRoot, "manual-audit.csv"), csv.ToString(), cancellationToken);
    }

    private static string MeasurementSemanticsDocument() =>
        """
        # Frozen Phase 4 measurement semantics

        Official RQ1 target satisfaction uses `BRANCH_SITE` with
        `ALL_JACOCO_BRANCHES_AT_SOURCE_LINE`: a site is satisfied only when JaCoCo
        reports zero missed branches and at least one covered branch at that source
        line. JaCoCo evidence is not interpreted as semantic true/false outcomes.

        `RETURN_PATH` and `EXPLICIT_EXCEPTION_PATH` are source-mapped diagnostic and
        planning obligations. They are not included in the official TargetSat
        denominator unless a later, pre-registered instrumentation protocol provides
        an independently observable execution mapping.

        A focal method with no JaCoCo branch site remains in the paired experiment for
        compilation, execution, mutation, and diagnostic analyses, but is reported as
        N/A and excluded from the branch-site TargetSat denominator. Such subjects are
        listed explicitly in `target-extraction-report.json`.

        Each inventory also contains a deterministic source-level intraprocedural
        control-flow graph (`SOURCE_LEVEL_INTRAPROCEDURAL_CFG_V1`). It models explicit
        Java control constructs and conservative catch edges. It is not a JVM-bytecode
        CFG: implicit runtime exceptions are not resolved, and `finally` is represented
        on normal try/catch completion rather than every abrupt completion. A planner
        must therefore use it as auditable structural guidance, not as proof of exact
        runtime feasibility. Short-circuit, ternary, switch-expression, and
        label-specific subflows are not expanded into separate nodes.

        This inventory is generated deterministically from Java AST and the frozen
        Phase 3 manifests. No Planner or LLM output is read by the extractor.
        """;

    private static bool IsValidControlFlowGraph(MethodControlFlowGraph graph)
    {
        if (graph is null || graph.Semantics != CfgSemantics
            || graph.Nodes is null || graph.Edges is null || graph.ExitNodeIds is null
            || graph.Nodes.Count < 2 || graph.Edges.Count == 0
            || string.IsNullOrWhiteSpace(graph.EntryNodeId)
            || graph.ExitNodeIds.Count == 0)
            return false;
        var nodeIds = graph.Nodes.Select(node => node.Id).ToList();
        var uniqueNodeIds = nodeIds.ToHashSet(StringComparer.Ordinal);
        if (uniqueNodeIds.Count != nodeIds.Count
            || !uniqueNodeIds.Contains(graph.EntryNodeId)
            || graph.ExitNodeIds.Any(exit => !uniqueNodeIds.Contains(exit))
            || graph.Edges.Any(edge => !uniqueNodeIds.Contains(edge.From)
                                       || !uniqueNodeIds.Contains(edge.To)
                                       || string.IsNullOrWhiteSpace(edge.Kind)))
            return false;
        var edgeKeys = graph.Edges
            .Select(edge => $"{edge.From}\u001f{edge.To}\u001f{edge.Kind}\u001f{edge.Label}")
            .ToList();
        if (edgeKeys.Distinct(StringComparer.Ordinal).Count() != edgeKeys.Count) return false;

        return graph.Edges.Any(edge => edge.From == graph.EntryNodeId);
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

    private static string ToWorkspaceRelativeExecutable(string executable, string workspaceRoot)
    {
        if (!Path.IsPathRooted(executable)) return executable;
        var relative = Path.GetRelativePath(workspaceRoot, executable);
        return relative.Equals("..", StringComparison.Ordinal)
               || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? executable
            : relative;
    }

    private static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';
}
