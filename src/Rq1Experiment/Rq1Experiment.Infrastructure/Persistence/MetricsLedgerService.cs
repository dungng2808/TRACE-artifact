using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Rq1Experiment.Contracts.Experiments;
using Rq1Experiment.Contracts.Generation;
using Rq1Experiment.Contracts.Subjects;
using Rq1Experiment.Contracts.Targets;
using Rq1Experiment.Core.Abstractions;

namespace Rq1Experiment.Infrastructure.Persistence;

public sealed class MetricsLedgerService : IMetricsLedgerService
{
    private static readonly JsonSerializerOptions CompactJson = new(JsonSupport.Options) { WriteIndented = false };
    private static readonly HashSet<string> ArtifactFailureStatuses = new(StringComparer.Ordinal)
    {
        ToolRunStatuses.GenerationInvalid, ToolRunStatuses.CompileFail,
        ToolRunStatuses.TestDiscoveryFail, ToolRunStatuses.TestExecutionFail,
        ToolRunStatuses.AssertionFail, ToolRunStatuses.RuntimeError
    };

    public async Task<MetricsLedgerReport> RebuildAsync(
        MetricsLedgerRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var experimentRunsRoot = Path.Combine(request.ExperimentOutputRoot, "runs", request.ExperimentId);
        if (!Directory.Exists(experimentRunsRoot))
            throw new DirectoryNotFoundException($"Experiment run directory does not exist: {experimentRunsRoot}");

        var configSnapshot = await EnsureConfigSnapshotAsync(request, experimentRunsRoot, cancellationToken);
        var configHash = await Sha256FileAsync(configSnapshot, cancellationToken);
        var subjects = await ReadJsonLinesAsync<FocalMethodSubject>(
            Path.Combine(request.ExperimentOutputRoot, "subjects", "subjects-smoke.jsonl"),
            Path.Combine(request.ExperimentOutputRoot, "subjects", "subjects-pilot.jsonl"),
            Path.Combine(request.ExperimentOutputRoot, "subjects", "subjects-official.jsonl"), cancellationToken);
        var subjectMap = subjects.ToDictionary(item => item.SubjectId, StringComparer.Ordinal);
        var inventories = await ReadJsonLinesAsync<StructuralTargetInventory>(
            Path.Combine(request.ExperimentOutputRoot, "targets", "targets-smoke.jsonl"),
            Path.Combine(request.ExperimentOutputRoot, "targets", "targets-pilot.jsonl"),
            Path.Combine(request.ExperimentOutputRoot, "targets", "targets-official.jsonl"), cancellationToken);
        var inventoryMap = inventories.ToDictionary(item => item.SubjectId, StringComparer.Ordinal);
        var artifacts = DiscoverRuns(experimentRunsRoot).ToList();
        if (artifacts.Count == 0)
            throw new InvalidDataException("No DIRECT or PLANNED run.json artifacts were found for this experiment.");

        var mutationSets = artifacts.ToDictionary(
            artifact => artifact.Key,
            artifact => ReadFocalMutations(artifact.ToolRecord, artifact.RunDirectory),
            StringComparer.Ordinal);
        var commonMutants = BuildCommonMutantSets(artifacts, mutationSets);
        var rows = new List<RunMetricsRecord>();
        var sequence = 0;
        foreach (var artifact in artifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sequence++;
            progress?.Report($"[{sequence}/{artifacts.Count}] Rebuilding metrics for {artifact.SubjectId} {artifact.Approach} R{artifact.Repetition:00}...");
            subjectMap.TryGetValue(artifact.SubjectId, out var subject);
            inventoryMap.TryGetValue(artifact.SubjectId, out var inventory);
            var row = BuildRow(request, artifact, subject, inventory, configHash,
                mutationSets[artifact.Key], commonMutants.GetValueOrDefault(artifact.SubjectId, []));
            ValidateRow(row, artifact);
            rows.Add(row);
            await JsonSupport.WriteAsync(Path.Combine(artifact.RunDirectory, "metrics.json"), row, cancellationToken);
        }

        var duplicateKeys = rows.GroupBy(row => row.Key.ToString(), StringComparer.Ordinal)
            .Where(group => group.Count() > 1).ToList();
        foreach (var duplicate in duplicateKeys)
        foreach (var row in duplicate)
            row.ValidationErrors.Add("DUPLICATE_RUN_KEY: " + duplicate.Key);

        var ledgerRoot = Path.Combine(request.ExperimentOutputRoot, "ledger", request.ExperimentId);
        Directory.CreateDirectory(ledgerRoot);
        var csvPath = Path.Combine(ledgerRoot, "runs.csv");
        var jsonlPath = Path.Combine(ledgerRoot, "metrics.jsonl");
        var qualityPath = Path.Combine(ledgerRoot, "data-quality-report.json");
        await File.WriteAllTextAsync(csvPath, BuildCsv(rows), cancellationToken);
        await File.WriteAllTextAsync(jsonlPath,
            string.Join('\n', rows.Select(row => JsonSerializer.Serialize(row, CompactJson))) + "\n", cancellationToken);

        var invalidRows = rows.Where(row => row.ValidationErrors.Count > 0).ToList();
        var report = new MetricsLedgerReport
        {
            ExperimentId = request.ExperimentId, Mode = request.Mode, RunCount = rows.Count,
            DirectRunCount = rows.Count(row => row.Key.Approach == "DIRECT"),
            PlannedRunCount = rows.Count(row => row.Key.Approach == "PLANNED"),
            InvalidRowCount = invalidRows.Count, DuplicateKeyCount = duplicateKeys.Count,
            FailedRunRetainedCount = rows.Count(row => ArtifactFailureStatuses.Contains(row.Status)),
            ToolErrorCount = rows.Count(row => row.Status == "TOOL_ERROR" || row.Status == ToolRunStatuses.Timeout),
            UniqueKeys = duplicateKeys.Count == 0,
            FailedRunsRetained = artifacts.Count(artifact => IsArtifactFailure(artifact.ToolRecord?.Status, artifact.GenerationStatus))
                                 == rows.Count(row => ArtifactFailureStatuses.Contains(row.Status)),
            NoMissingRequiredHashes = rows.All(row =>
                row.PromptHash.Length == 64 && row.ContextHash.Length == 64 && row.ConfigHash.Length == 64
                && row.SourceHash.Length == 64 && row.TargetHash.Length == 64),
            FailureScoringValid = rows.All(FailureScoringIsValid),
            TreatmentContractsValid = rows.All(row =>
                row.Key.Approach == "DIRECT" ? row.ScenarioCount is null && row.MappingValid is null
                    : row.ScenarioCount is not null && row.MappingValid is not null),
            RebuildIndependentOfUiState = configSnapshot.StartsWith(experimentRunsRoot, StringComparison.OrdinalIgnoreCase),
            RunsCsvPath = csvPath, MetricsJsonlPath = jsonlPath, DataQualityReportPath = qualityPath,
            Approaches = rows.GroupBy(row => row.Key.Approach, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal).Select(Aggregate).ToList(),
            Errors = invalidRows.SelectMany(row => row.ValidationErrors.Select(error => $"{row.Key}: {error}"))
                .Concat(duplicateKeys.Select(group => "Duplicate key: " + group.Key)).ToList(),
            GeneratedAt = DateTimeOffset.UtcNow
        };
        report.Complete = report.RunCount > 0 && report.InvalidRowCount == 0 && report.UniqueKeys
                          && report.FailedRunsRetained && report.NoMissingRequiredHashes
                          && report.FailureScoringValid && report.TreatmentContractsValid
                          && report.RebuildIndependentOfUiState;
        report.LedgerSha256 = await Sha256FileAsync(csvPath, cancellationToken);
        await JsonSupport.WriteAsync(qualityPath, report, cancellationToken);
        await JsonSupport.WriteAsync(Path.Combine(request.ExperimentOutputRoot, "metrics-ledger-report.json"), report, cancellationToken);
        if (!report.Complete)
            progress?.Report($"Ledger rejected {report.InvalidRowCount} invalid row(s); inspect {qualityPath}.");
        else
            progress?.Report($"Ledger rebuilt from {report.RunCount} immutable run directories; SHA-256={report.LedgerSha256}.");
        return report;
    }

    private static RunMetricsRecord BuildRow(MetricsLedgerRequest request, RunArtifact artifact,
        FocalMethodSubject? subject, StructuralTargetInventory? inventory, string configHash,
        IReadOnlyDictionary<string, string> runMutants, IReadOnlySet<string> commonMutants)
    {
        var generation = artifact.Generation;
        var tool = artifact.ToolRecord;
        var generationValid = artifact.GenerationStatus == "VALID";
        var toolError = generationValid && (tool is null || tool.Status is ToolRunStatuses.CoverageToolError
            or ToolRunStatuses.MutationToolError or ToolRunStatuses.Timeout || tool.FailureGate == "INFRASTRUCTURE");
        var compileSuccess = generationValid && tool?.Processes.Any(process =>
            process.Gate == "COMPILE" && !process.TimedOut && process.ExitCode == 0) == true;
        var executionSuccess = compileSuccess && tool?.Processes.Any(process =>
            process.Gate == "JUNIT" && !process.TimedOut && process.ExitCode == 0) == true
            && tool.Status is not (ToolRunStatuses.TestDiscoveryFail or ToolRunStatuses.AssertionFail
                or ToolRunStatuses.RuntimeError or ToolRunStatuses.TestExecutionFail);
        var status = ResolveStatus(artifact, toolError, executionSuccess);
        var branch = Ratio(tool?.Coverage?.BranchCovered, tool?.Coverage?.BranchMissed);
        var target = executionSuccess && !toolError
            ? CalculateTargetSatisfaction(tool!, subject, inventory, artifact.RunDirectory) : default;
        var mutation = CalculateMutation(runMutants, commonMutants);
        var conditionalMutation = executionSuccess && !toolError ? mutation.Score : null;
        var artifactFailure = IsArtifactFailure(tool?.Status, artifact.GenerationStatus);
        var planEvidence = CalculatePlanEvidence(artifact, inventory);
        var junit = ReadJunitSummary(artifact.RunDirectory);
        var passedTests = junit is null
            ? 0
            : Math.Max(0, junit.Value.Tests - junit.Value.Failures - junit.Value.Errors - junit.Value.Skipped);
        double? passRate = toolError
            ? null
            : junit is { Tests: > 0 }
                ? (double)passedTests / junit.Value.Tests
                : artifactFailure ? 0 : null;
        var scenarioCount = artifact.Approach == "PLANNED" ? Int(generation, "scenarioCount") : null;
        var mappingValid = artifact.Approach == "PLANNED" ? Bool(generation, "bijectionValid") : null;
        double? scenarioValidity = artifact.Approach != "PLANNED"
            ? null
            : toolError ? null
            : scenarioCount is > 0 && mappingValid == true && junit is { Tests: > 0 }
                ? Math.Min(1d, (double)passedTests / scenarioCount.Value)
                : 0;

        var inputTokens = artifact.Approach == "DIRECT"
            ? Int(generation, "inputTokens")
            : SumNullable(Int(generation, "plannerInputTokens"), Int(generation, "generatorInputTokens"));
        var outputTokens = artifact.Approach == "DIRECT"
            ? Int(generation, "outputTokens")
            : SumNullable(Int(generation, "plannerOutputTokens"), Int(generation, "generatorOutputTokens"));
        var semanticCalls = artifact.Approach == "DIRECT"
            ? Int(generation, "semanticLlmCalls") ?? 0
            : (Int(generation, "plannerSemanticCalls") ?? 0) + (Int(generation, "generatorSemanticCalls") ?? 0);
        var plannerRequestHash = String(generation, "plannerRequestHash");
        var generatorRequestHash = String(generation, "generatorRequestHash");
        var responseModel = artifact.Approach == "DIRECT"
            ? String(generation, "responseModelId")
            : FirstNonEmpty(String(generation, "generatorResponseModelId"),
                String(generation, "plannerResponseModelId"));
        var promptHash = artifact.Approach == "DIRECT"
            ? String(generation, "requestHash")
            : string.IsNullOrWhiteSpace(generatorRequestHash)
                ? plannerRequestHash
                : Sha256(plannerRequestHash + "|" + generatorRequestHash);
        var targetHash = inventory is null ? string.Empty : Sha256(JsonSerializer.Serialize(inventory, CompactJson));
        var generationSeconds = DurationSeconds(generation);
        var toolSeconds = tool?.Processes.Sum(process => process.DurationSeconds) ?? 0;

        var row = new RunMetricsRecord
        {
            Key = new RunMetricKey { ExperimentId = request.ExperimentId, SubjectId = artifact.SubjectId,
                Approach = artifact.Approach, Repetition = artifact.Repetition },
            ProjectId = subject?.ProjectId ?? tool?.ProjectId ?? string.Empty,
            Status = status,
            CompileSuccess = toolError && !compileSuccess ? null : compileSuccess,
            ExecutionSuccess = toolError && !executionSuccess ? null : executionSuccess,
            ConditionalTargetSatisfaction = executionSuccess && !toolError ? target.Ratio : null,
            ConditionalBranchCoverage = executionSuccess && !toolError ? branch : null,
            ConditionalMutationScore = conditionalMutation,
            EndToEndTargetSatisfaction = artifactFailure ? 0 : toolError ? null : target.Ratio,
            EndToEndBranchCoverage = artifactFailure ? 0 : toolError ? null : branch,
            EndToEndMutationScore = artifactFailure ? 0 : toolError ? null : conditionalMutation,
            PlanFidelity = planEvidence.Fidelity,
            PlanObligationCoverage = planEvidence.ObligationCoverage,
            ScenarioValidity = scenarioValidity,
            PassRate = passRate,
            SatisfiedTargetCount = target.Satisfied, TargetCount = target.Total,
            GeneratedTestCount = artifact.Approach == "DIRECT"
                ? Int(generation, "declaredTestMethodCount") ?? 0 : Int(generation, "testMethodCount") ?? 0,
            ScenarioCount = scenarioCount,
            MappingValid = mappingValid,
            Mutation = mutation,
            Cost = new RunCostMetric { InputTokens = inputTokens, OutputTokens = outputTokens,
                TotalTokens = Int(generation, "totalTokens"), SemanticLlmCalls = semanticCalls,
                TransportAttempts = Int(generation, "transportAttempts") ?? 0,
                WallClockSeconds = generationSeconds + toolSeconds },
            ProviderId = String(generation, "providerId"), RequestedModelId = String(generation, "requestedModelId"),
            ResponseModelId = responseModel, PromptHash = promptHash,
            ContextHash = String(generation, "contextHash"), ConfigHash = configHash,
            SourceHash = subject?.SourceSha256 ?? string.Empty, TargetHash = targetHash,
            RunDirectory = artifact.RunDirectory
        };
        return row;
    }

    private static PlanEvidenceMetric CalculatePlanEvidence(
        RunArtifact artifact,
        StructuralTargetInventory? inventory)
    {
        if (artifact.Approach != "PLANNED") return new PlanEvidenceMetric(null, null);
        if (inventory is null || inventory.Targets.Count == 0) return new PlanEvidenceMetric(null, null);
        var planPath = Path.Combine(artifact.RunDirectory, "planner", "scenario-plan.json");
        if (!File.Exists(planPath)) return new PlanEvidenceMetric(0, 0);
        try
        {
            var plan = JsonSerializer.Deserialize<ScenarioPlan>(File.ReadAllText(planPath), JsonSupport.Options);
            if (plan is null || plan.Scenarios.Count == 0) return new PlanEvidenceMetric(0, 0);
            var targets = inventory.Targets.ToDictionary(target => target.TargetId, StringComparer.Ordinal);
            var links = plan.Scenarios
                .SelectMany(scenario => scenario.TargetIds.Select(targetId => (Scenario: scenario, TargetId: targetId)))
                .ToList();
            var faithfulLinks = links.Count(link =>
                targets.TryGetValue(link.TargetId, out var target)
                && link.Scenario.OracleProvenance.SourceLines.Contains(target.Line));
            var coveredTargets = links.Select(link => link.TargetId)
                .Where(targets.ContainsKey).Distinct(StringComparer.Ordinal).Count();
            return new PlanEvidenceMetric(
                links.Count == 0 ? 0 : (double)faithfulLinks / links.Count,
                (double)coveredTargets / inventory.Targets.Count);
        }
        catch (JsonException)
        {
            return new PlanEvidenceMetric(0, 0);
        }
    }

    private static JunitMetric? ReadJunitSummary(string runDirectory)
    {
        var path = Path.Combine(runDirectory, "tools", "junit-summary.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            return new JunitMetric(
                Int(root, "tests") ?? 0,
                Int(root, "failures") ?? 0,
                Int(root, "errors") ?? 0,
                Int(root, "skipped") ?? 0);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void ValidateRow(RunMetricsRecord row, RunArtifact artifact)
    {
        void RequiredHash(string name, string value)
        { if (value.Length != 64) row.ValidationErrors.Add($"MISSING_OR_INVALID_{name.ToUpperInvariant()}_HASH"); }
        RequiredHash("prompt", row.PromptHash); RequiredHash("context", row.ContextHash);
        RequiredHash("config", row.ConfigHash); RequiredHash("source", row.SourceHash); RequiredHash("target", row.TargetHash);
        if (!HasRawResponses(artifact)) row.ValidationErrors.Add("MISSING_RAW_RESPONSE");
        if (row.Key.Approach == "DIRECT")
        {
            if (string.IsNullOrWhiteSpace(String(artifact.Generation, "requestHash")))
                row.ValidationErrors.Add("DIRECT_REQUEST_HASH_MISSING");
            if (Directory.Exists(Path.Combine(row.RunDirectory, "planner"))) row.ValidationErrors.Add("DIRECT_HAS_PLANNER_ARTIFACT");
            if (row.ScenarioCount is not null || row.MappingValid is not null) row.ValidationErrors.Add("DIRECT_TREATMENT_CONTAMINATION");
        }
        else
        {
            if (row.ScenarioCount is null || row.MappingValid is null)
                row.ValidationErrors.Add("PLANNED_MAPPING_METADATA_MISSING");
            var failureStage = String(artifact.Generation, "failureStage");
            if (string.IsNullOrWhiteSpace(String(artifact.Generation, "plannerRequestHash")))
                row.ValidationErrors.Add("PLANNER_REQUEST_HASH_MISSING");
            if (!failureStage.Equals("PLANNER", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(String(artifact.Generation, "generatorRequestHash")))
                row.ValidationErrors.Add("GENERATOR_REQUEST_HASH_MISSING");
        }
        if (!FailureScoringIsValid(row)) row.ValidationErrors.Add("INVALID_FAILURE_SCORING");
        if (row.CompileSuccess == false && (Positive(row.EndToEndBranchCoverage)
                                            || Positive(row.EndToEndMutationScore)
                                            || Positive(row.EndToEndTargetSatisfaction)))
            row.ValidationErrors.Add("POSITIVE_METRIC_AFTER_COMPILE_FAILURE");
    }

    private static TargetMetric CalculateTargetSatisfaction(ToolRunRecord tool,
        FocalMethodSubject? subject, StructuralTargetInventory? inventory, string runDirectory)
    {
        var targets = inventory?.Targets.Where(target => target.MeasurementRole == "OFFICIAL").ToList() ?? [];
        var report = ResolveRetainedToolFile(tool.Coverage?.ReportFile, runDirectory, "jacoco", "jacoco.xml");
        if (targets.Count == 0 || subject is null || tool.Coverage is null || report is null)
            return new TargetMetric(0, targets.Count, targets.Count == 0 ? null : 0);
        var document = XDocument.Load(report);
        var sourceName = Path.GetFileName(subject.SourceFile);
        var package = subject.FocalClass.Contains('.') ? subject.FocalClass[..subject.FocalClass.LastIndexOf('.')].Replace('.', '/') : string.Empty;
        var sourceFile = document.Descendants("package")
            .Where(element => element.Attribute("name")?.Value == package)
            .Elements("sourcefile").FirstOrDefault(element => element.Attribute("name")?.Value == sourceName);
        var lines = sourceFile?.Elements("line").ToDictionary(
            element => IntAttribute(element, "nr"), element => element) ?? [];
        var satisfied = targets.Count(target => lines.TryGetValue(target.Line, out var line)
            && IntAttribute(line, "cb") > 0 && IntAttribute(line, "mb") == 0);
        return new TargetMetric(satisfied, targets.Count, (double)satisfied / targets.Count);
    }

    private static MutationMetric CalculateMutation(IReadOnlyDictionary<string, string> run,
        IReadOnlySet<string> common)
    {
        var statuses = common.Where(run.ContainsKey).Select(key => run[key]).ToList();
        var killed = statuses.Count(status => status == "KILLED");
        var survived = statuses.Count(status => status == "SURVIVED");
        var noCoverage = statuses.Count(status => status == "NO_COVERAGE");
        var denominator = killed + survived + noCoverage;
        return new MutationMetric
        {
            Killed = killed, Survived = survived, NoCoverage = noCoverage,
            Excluded = Math.Max(0, run.Count - denominator), CommonViableMutantCount = common.Count,
            Score = denominator == 0 ? null : (double)killed / denominator
        };
    }

    private static Dictionary<string, HashSet<string>> BuildCommonMutantSets(IReadOnlyCollection<RunArtifact> artifacts,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> sets)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var group in artifacts.GroupBy(item => item.SubjectId, StringComparer.Ordinal))
        {
            var available = group.Select(item => sets[item.Key]).Where(set => set.Count > 0).ToList();
            if (available.Count == 0) { result[group.Key] = []; continue; }
            var common = available[0].Keys.ToHashSet(StringComparer.Ordinal);
            foreach (var set in available.Skip(1)) common.IntersectWith(set.Keys);
            result[group.Key] = common;
        }
        return result;
    }

    private static IReadOnlyDictionary<string, string> ReadFocalMutations(ToolRunRecord? tool, string runDirectory)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var path = ResolveRetainedToolFile(tool?.Mutation?.ReportFile, runDirectory, "pit", "mutations.xml");
        if (tool is null || path is null) return result;
        var methodName = tool.FocalMethodSignature.Split('(', 2)[0];
        foreach (var mutation in XDocument.Load(path).Descendants("mutation"))
        {
            if (mutation.Element("mutatedClass")?.Value != tool.FocalClass
                || mutation.Element("mutatedMethod")?.Value != methodName) continue;
            var line = int.TryParse(mutation.Element("lineNumber")?.Value, out var parsed) ? parsed : 0;
            if (line < tool.FocalStartLine || line > tool.FocalEndLine) continue;
            var status = (mutation.Attribute("status")?.Value ?? mutation.Element("status")?.Value ?? "").ToUpperInvariant();
            if (status is not ("KILLED" or "SURVIVED" or "NO_COVERAGE")) continue;
            var identity = string.Join('|', mutation.Element("mutatedClass")?.Value,
                mutation.Element("mutatedMethod")?.Value, mutation.Element("methodDescription")?.Value,
                line, mutation.Element("mutator")?.Value,
                string.Join(',', mutation.Element("indexes")?.Elements("index").Select(element => element.Value) ?? []));
            result[identity] = status;
        }
        return result;
    }

    private static string? ResolveRetainedToolFile(string? recordedPath, string runDirectory,
        string tool, string fileName)
    {
        if (!string.IsNullOrWhiteSpace(recordedPath) && File.Exists(recordedPath)) return recordedPath;
        var portable = Path.Combine(runDirectory, "tools", tool, fileName);
        return File.Exists(portable) ? portable : null;
    }

    private static IEnumerable<RunArtifact> DiscoverRuns(string experimentRoot)
    {
        foreach (var path in Directory.EnumerateFiles(experimentRoot, "run.json", SearchOption.AllDirectories).Order())
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement.Clone();
            var approach = String(root, "approach");
            if (approach is not ("DIRECT" or "PLANNED")) continue;
            var runDirectory = Path.GetDirectoryName(path)!;
            var toolPath = Path.Combine(runDirectory, "tools", "tool-run.json");
            var tool = File.Exists(toolPath)
                ? JsonSerializer.Deserialize<ToolRunRecord>(File.ReadAllText(toolPath), JsonSupport.Options)
                : null;
            yield return new RunArtifact(String(root, "subjectId"), approach, Int(root, "repetition") ?? 0,
                String(root, "status"), runDirectory, root, tool);
        }
    }

    private static async Task<string> EnsureConfigSnapshotAsync(MetricsLedgerRequest request,
        string experimentRunsRoot, CancellationToken token)
    {
        var snapshot = Path.Combine(experimentRunsRoot, "experiment-config.snapshot.json");
        if (File.Exists(snapshot)) return snapshot;
        if (!File.Exists(request.ConfigSnapshotPath))
            throw new FileNotFoundException("Experiment config snapshot is required for a reproducible config hash.", request.ConfigSnapshotPath);
        File.Copy(request.ConfigSnapshotPath, snapshot, false);
        await Task.CompletedTask;
        token.ThrowIfCancellationRequested();
        return snapshot;
    }

    private static bool HasRawResponses(RunArtifact artifact)
    {
        if (artifact.Approach == "DIRECT")
            return File.Exists(Path.Combine(artifact.RunDirectory, "generation", "assistant-content.raw.txt"))
                   && File.Exists(Path.Combine(artifact.RunDirectory, "generation", "provider-response.raw.json"));

        var plannerEvidence = File.Exists(Path.Combine(artifact.RunDirectory, "planner", "assistant-content.raw.txt"))
                              && File.Exists(Path.Combine(artifact.RunDirectory, "planner", "provider-response.raw.json"));
        if (!plannerEvidence) return false;
        if (String(artifact.Generation, "failureStage").Equals("PLANNER", StringComparison.OrdinalIgnoreCase))
            return true;
        return File.Exists(Path.Combine(artifact.RunDirectory, "generator", "assistant-content.raw.txt"))
               && File.Exists(Path.Combine(artifact.RunDirectory, "generator", "provider-response.raw.json"));
    }

    private static string ResolveStatus(RunArtifact artifact, bool toolError, bool executionSuccess)
    {
        if (artifact.GenerationStatus != "VALID") return ToolRunStatuses.GenerationInvalid;
        if (toolError) return artifact.ToolRecord?.Status == ToolRunStatuses.Timeout ? ToolRunStatuses.Timeout : "TOOL_ERROR";
        if (executionSuccess && artifact.ToolRecord?.Mutation is not null) return "COMPLETED";
        return artifact.ToolRecord?.Status ?? "TOOL_ERROR";
    }

    private static bool IsArtifactFailure(string? toolStatus, string generationStatus) =>
        generationStatus != "VALID" || (toolStatus is not null && ArtifactFailureStatuses.Contains(toolStatus));

    private static bool FailureScoringIsValid(RunMetricsRecord row)
    {
        if (!ArtifactFailureStatuses.Contains(row.Status)) return true;
        return row.EndToEndTargetSatisfaction == 0 && row.EndToEndBranchCoverage == 0
               && row.EndToEndMutationScore == 0 && row.ConditionalTargetSatisfaction is null
               && row.ConditionalBranchCoverage is null && row.ConditionalMutationScore is null;
    }

    private static ApproachAggregate Aggregate(IGrouping<string, RunMetricsRecord> group) => new()
    {
        Approach = group.Key, RunCount = group.Count(),
        CompilationSuccessRate = (double)group.Count(row => row.CompileSuccess == true) / group.Count(),
        ExecutionSuccessRate = (double)group.Count(row => row.ExecutionSuccess == true) / group.Count(),
        MeanEndToEndTargetSatisfaction = Mean(group.Select(row => row.EndToEndTargetSatisfaction)),
        MeanEndToEndBranchCoverage = Mean(group.Select(row => row.EndToEndBranchCoverage)),
        MeanEndToEndMutationScore = Mean(group.Select(row => row.EndToEndMutationScore)),
        MeanPlanFidelity = Mean(group.Select(row => row.PlanFidelity)),
        MeanPlanObligationCoverage = Mean(group.Select(row => row.PlanObligationCoverage)),
        MeanScenarioValidity = Mean(group.Select(row => row.ScenarioValidity)),
        MeanPassRate = Mean(group.Select(row => row.PassRate)),
        TotalInputTokens = SumTokens(group.Select(row => row.Cost.InputTokens)),
        TotalOutputTokens = SumTokens(group.Select(row => row.Cost.OutputTokens)),
        TotalReportedTokens = SumTokens(group.Select(row => row.Cost.TotalTokens)),
        TokenSampleCount = group.Count(row => row.Cost.TotalTokens.HasValue),
        ToolErrorCount = group.Count(row => row.Status is "TOOL_ERROR" or ToolRunStatuses.Timeout)
    };

    private static string BuildCsv(IEnumerable<RunMetricsRecord> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine("experiment_id,subject_id,project_id,approach,repetition,run_status,compile_success,execution_success,e2e_target_satisfaction,e2e_branch_coverage,e2e_mutation_score,plan_fidelity,plan_obligation_coverage,scenario_validity,pass_rate,conditional_branch_coverage,conditional_mutation_score,generated_test_count,scenario_count,mapping_valid,input_tokens,output_tokens,total_tokens,semantic_llm_calls,transport_attempts,wall_clock_seconds,provider_id,requested_model_id,response_model_id,prompt_hash,context_hash,config_hash,source_hash,target_hash,run_directory");
        foreach (var row in rows.OrderBy(row => row.Key.SubjectId, StringComparer.Ordinal)
                     .ThenBy(row => row.Key.Repetition).ThenBy(row => row.Key.Approach, StringComparer.Ordinal))
        {
            builder.AppendLine(string.Join(',', new[]
            {
                Csv(row.Key.ExperimentId), Csv(row.Key.SubjectId), Csv(row.ProjectId), Csv(row.Key.Approach), row.Key.Repetition.ToString(CultureInfo.InvariantCulture),
                Csv(row.Status), Csv(BoolText(row.CompileSuccess)), Csv(BoolText(row.ExecutionSuccess)), Number(row.EndToEndTargetSatisfaction),
                Number(row.EndToEndBranchCoverage), Number(row.EndToEndMutationScore), Number(row.PlanFidelity),
                Number(row.PlanObligationCoverage), Number(row.ScenarioValidity), Number(row.PassRate),
                Number(row.ConditionalBranchCoverage), Number(row.ConditionalMutationScore),
                row.GeneratedTestCount.ToString(CultureInfo.InvariantCulture), NullableInt(row.ScenarioCount), Csv(BoolText(row.MappingValid)),
                NullableInt(row.Cost.InputTokens), NullableInt(row.Cost.OutputTokens), NullableInt(row.Cost.TotalTokens),
                row.Cost.SemanticLlmCalls.ToString(CultureInfo.InvariantCulture), row.Cost.TransportAttempts.ToString(CultureInfo.InvariantCulture),
                row.Cost.WallClockSeconds.ToString("0.######", CultureInfo.InvariantCulture), Csv(row.ProviderId), Csv(row.RequestedModelId), Csv(row.ResponseModelId),
                Csv(row.PromptHash), Csv(row.ContextHash), Csv(row.ConfigHash), Csv(row.SourceHash), Csv(row.TargetHash), Csv(row.RunDirectory)
            }));
        }
        return builder.ToString();
    }

    private static async Task<List<T>> ReadJsonLinesAsync<T>(string first, string second, string third, CancellationToken token)
    {
        var rows = new List<T>();
        foreach (var path in new[] { first, second, third }.Where(File.Exists))
        foreach (var line in await File.ReadAllLinesAsync(path, token))
            if (!string.IsNullOrWhiteSpace(line)) rows.Add(JsonSerializer.Deserialize<T>(line, JsonSupport.Options)!);
        return rows;
    }

    private static int? Int(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;
    private static bool? Bool(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    private static string String(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : string.Empty;
    private static string FirstNonEmpty(string first, string second) =>
        !string.IsNullOrWhiteSpace(first) ? first : second;
    private static int? SumNullable(int? first, int? second) => first is null && second is null ? null : (first ?? 0) + (second ?? 0);
    private static double DurationSeconds(JsonElement root) =>
        DateTimeOffset.TryParse(String(root, "startedAt"), out var start)
        && DateTimeOffset.TryParse(String(root, "completedAt"), out var end) ? Math.Max(0, (end - start).TotalSeconds) : 0;
    private static double? Ratio(int? covered, int? missed) => covered is null || missed is null || covered + missed == 0 ? null : (double)covered / (covered + missed);
    private static int IntAttribute(XElement element, string name) => int.TryParse(element.Attribute(name)?.Value, out var value) ? value : 0;
    private static double? Mean(IEnumerable<double?> values) { var present = values.Where(value => value is not null).Select(value => value!.Value).ToList(); return present.Count == 0 ? null : present.Average(); }
    private static long? SumTokens(IEnumerable<int?> values) { var present = values.Where(value => value.HasValue).Select(value => (long)value!.Value).ToList(); return present.Count == 0 ? null : present.Sum(); }
    private static bool Positive(double? value) => value > 0;
    private static string BoolText(bool? value) => value is null ? string.Empty : value.Value ? "true" : "false";
    private static string NullableInt(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    private static string Number(double? value) => value?.ToString("0.########", CultureInfo.InvariantCulture) ?? string.Empty;
    private static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';
    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static async Task<string> Sha256FileAsync(string path, CancellationToken token) { await using var stream = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant(); }

    private sealed record RunArtifact(string SubjectId, string Approach, int Repetition, string GenerationStatus,
        string RunDirectory, JsonElement Generation, ToolRunRecord? ToolRecord)
    { public string Key => $"{SubjectId}|{Approach}|{Repetition}"; }
    private readonly record struct TargetMetric(int Satisfied, int Total, double? Ratio);
    private readonly record struct PlanEvidenceMetric(double? Fidelity, double? ObligationCoverage);
    private readonly record struct JunitMetric(int Tests, int Failures, int Errors, int Skipped);
}
