using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RQ2Experiment.Infrastructure.Benchmark;

public sealed record PilotSamplingFrameEntry(
    int SelectionOrder,
    bool Selected,
    string BugId,
    string ProjectId,
    string ProjectRepositoryUrl,
    string FixedRevision,
    string BuggyRevision,
    string PullRequestUrl,
    string BugReportPath,
    string CandidateId,
    string GeneratedTestFile,
    string InjectedTest,
    string TestClassName,
    string FailingTestMethod,
    string InitialFailureCategory,
    int KnownBugOracleTests,
    bool RepairCandidateEligible,
    bool GeneratedTestPresent,
    bool RepositorySourcePresent,
    bool ReadyToReconstruct,
    string ReconstructionStatus);

public sealed record PilotSamplingFrameBuildResult(
    string CandidateFramePath,
    string SummaryPath,
    string JsonPath,
    int VerifiedBugs,
    int EligibleBugs,
    int SelectedBugs,
    int SelectedProjects,
    int ReadyToReconstruct);

public static class PilotSamplingFrameBuilder
{
    private static readonly Encoding ExcelUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
    private static readonly Regex BugPattern = new("^(?<project>.+)-(?<number>[0-9]+)$", RegexOptions.Compiled);
    private static readonly Regex CandidateIndexPattern = new("_n(?<number>[0-9]+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly IReadOnlyDictionary<string, ProjectInfo> Projects =
        new Dictionary<string, ProjectInfo>(StringComparer.OrdinalIgnoreCase)
        {
            ["assertj_assertj-core"] = new("assertj/assertj-core", "https://github.com/assertj/assertj-core.git", "assertj-core"),
            ["checkstyle_checkstyle"] = new("checkstyle/checkstyle", "https://github.com/checkstyle/checkstyle.git", "checkstyle"),
            ["FasterXML_jackson-databind"] = new("FasterXML/jackson-databind", "https://github.com/FasterXML/jackson-databind.git", "jackson-databind"),
            ["google_gson"] = new("google/gson", "https://github.com/google/gson.git", "gson"),
            ["Hakky54_sslcontext-kickstart"] = new("Hakky54/sslcontext-kickstart", "https://github.com/Hakky54/sslcontext-kickstart.git", "sslcontext-kickstart"),
            ["jhy_jsoup"] = new("jhy/jsoup", "https://github.com/jhy/jsoup.git", "jsoup")
        };

    public static async Task<PilotSamplingFrameBuildResult> BuildAsync(
        string libroRoot,
        string existingManifestPath,
        string outputDirectory,
        int targetSize,
        int selectionSeed,
        CancellationToken cancellationToken)
    {
        if (targetSize <= 0) throw new ArgumentOutOfRangeException(nameof(targetSize));
        if (selectionSeed == 0) throw new ArgumentOutOfRangeException(nameof(selectionSeed));

        var root = Path.GetFullPath(libroRoot);
        var verifiedPath = Path.Combine(root, "data", "GHRB", "verified_bugs.json");
        var resultsPath = Path.Combine(root, "results", "example2_n50_GHRB.json");
        var generatedTestsRoot = Path.Combine(root, "data", "GHRB", "gen_tests");
        var repositoriesRoot = Path.Combine(root, "data", "GHRB", "repos");
        if (!File.Exists(verifiedPath)) throw new FileNotFoundException("GHRB verified-bug metadata was not found.", verifiedPath);
        if (!File.Exists(resultsPath)) throw new FileNotFoundException("GHRB execution results were not found.", resultsPath);

        var generatedFiles = Directory.Exists(generatedTestsRoot)
            ? Directory.EnumerateFiles(generatedTestsRoot, "*", SearchOption.AllDirectories)
                .Select(path => new { Path = path, Name = Path.GetFileNameWithoutExtension(path) })
                .Where(item => !string.IsNullOrWhiteSpace(item.Name))
                .GroupBy(item => item.Name!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Path, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var anchors = await LoadAnchorsAsync(existingManifestPath, cancellationToken);

        await using var verifiedStream = File.OpenRead(verifiedPath);
        await using var resultsStream = File.OpenRead(resultsPath);
        using var verifiedDocument = await JsonDocument.ParseAsync(verifiedStream, cancellationToken: cancellationToken);
        using var resultsDocument = await JsonDocument.ParseAsync(resultsStream, cancellationToken: cancellationToken);

        var entries = new List<PilotSamplingFrameEntry>();
        foreach (var bugProperty in verifiedDocument.RootElement.EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var match = BugPattern.Match(bugProperty.Name);
            if (!match.Success || !Projects.TryGetValue(match.Groups["project"].Value, out var project)) continue;

            var verified = bugProperty.Value;
            var repairCandidates = ReadRepairCandidates(resultsDocument.RootElement, bugProperty.Name);
            anchors.TryGetValue(bugProperty.Name, out var anchorIndex);
            var candidate = ChooseCandidate(repairCandidates, bugProperty.Name, anchorIndex, selectionSeed);
            var generatedTestPath = candidate is null || !generatedFiles.TryGetValue(candidate.Id, out var path)
                ? string.Empty
                : path;
            var repositorySourcePresent = Directory.Exists(Path.Combine(repositoriesRoot, project.RepositoryDirectory));
            var eligible = candidate is not null;
            var ready = eligible && generatedTestPath.Length > 0 && repositorySourcePresent;
            var status = !eligible
                ? "No eligible failed generated-test candidate in local execution results."
                : generatedTestPath.Length == 0 && !repositorySourcePresent
                    ? "Generated-test archive and repository archive are missing."
                    : generatedTestPath.Length == 0
                        ? "Generated-test archive is missing."
                        : !repositorySourcePresent
                            ? "Repository archive is missing."
                            : "Ready to reconstruct fixed/buggy workspaces and author the runnable manifest task.";
            var injected = candidate?.InjectedTest ?? string.Empty;
            var separator = injected.LastIndexOf('#');
            var testClass = separator > 0 ? injected[..separator] : string.Empty;
            var method = separator >= 0 && separator + 1 < injected.Length ? injected[(separator + 1)..] : string.Empty;
            var generatedRelative = candidate is null
                ? string.Empty
                : Path.Combine("data", "GHRB", "gen_tests", candidate.Id + ".txt").Replace('\\', '/');

            entries.Add(new PilotSamplingFrameEntry(
                0,
                false,
                bugProperty.Name,
                project.ProjectId,
                project.RepositoryUrl,
                GetString(verified, "merge_commit"),
                GetString(verified, "buggy_commit"),
                GetString(verified, "PR_url"),
                Path.Combine("data", "GHRB", "bug_report", bugProperty.Name + ".json").Replace('\\', '/'),
                candidate?.Id ?? string.Empty,
                generatedRelative,
                injected,
                testClass,
                method,
                candidate?.FailureCategory ?? string.Empty,
                CountArray(verified, "execution_result", "success_tests"),
                eligible,
                generatedTestPath.Length > 0,
                repositorySourcePresent,
                ready,
                status));
        }

        var selectedBugIds = SelectBalanced(entries, anchors.Keys, targetSize, selectionSeed);
        var selectedOrder = selectedBugIds
            .Select((bugId, index) => new { BugId = bugId, Order = index + 1 })
            .ToDictionary(item => item.BugId, item => item.Order, StringComparer.OrdinalIgnoreCase);
        entries = entries.Select(entry => selectedOrder.TryGetValue(entry.BugId, out var order)
                ? entry with { Selected = true, SelectionOrder = order }
                : entry)
            .OrderByDescending(entry => entry.Selected)
            .ThenBy(entry => entry.SelectionOrder == 0 ? int.MaxValue : entry.SelectionOrder)
            .ThenBy(entry => entry.ProjectId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.BugId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Directory.CreateDirectory(outputDirectory);
        var candidateFramePath = Path.Combine(outputDirectory, "pilot-candidate-frame.csv");
        var summaryPath = Path.Combine(outputDirectory, "pilot-sampling-summary.csv");
        var jsonPath = Path.Combine(outputDirectory, "pilot-sampling-frame.json");
        await WriteCandidateCsvAsync(entries, candidateFramePath, cancellationToken);
        await WriteSummaryCsvAsync(entries, summaryPath, cancellationToken);
        await File.WriteAllTextAsync(
            jsonPath,
            JsonSerializer.Serialize(
                new
                {
                    contractVersion = "RQ2-PILOT-SAMPLING-FRAME-1",
                    selectionSeed,
                    requestedTargetSize = targetSize,
                    source = new
                    {
                        verifiedBugs = verifiedPath,
                        verifiedBugsSha256 = HashFile(verifiedPath),
                        executionResults = resultsPath,
                        executionResultsSha256 = HashFile(resultsPath),
                        existingManifest = Path.GetFullPath(existingManifestPath),
                        existingManifestSha256 = File.Exists(existingManifestPath) ? HashFile(existingManifestPath) : string.Empty
                    },
                    selectionRule = "Keep runnable-manifest anchors, choose one failed generated-test candidate per bug deterministically, then balance selected bugs across projects.",
                    entries
                },
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }),
            cancellationToken);

        var selected = entries.Where(entry => entry.Selected).ToArray();
        return new PilotSamplingFrameBuildResult(
            candidateFramePath,
            summaryPath,
            jsonPath,
            entries.Count,
            entries.Count(entry => entry.RepairCandidateEligible),
            selected.Length,
            selected.Select(entry => entry.ProjectId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            selected.Count(entry => entry.ReadyToReconstruct));
    }

    private static IReadOnlyList<RepairCandidate> ReadRepairCandidates(JsonElement root, string bugId)
    {
        if (!root.TryGetProperty(bugId, out var bugResults) || bugResults.ValueKind != JsonValueKind.Object) return [];
        var candidates = new List<RepairCandidate>();
        foreach (var property in bugResults.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object ||
                GetBoolean(property.Value, "success") ||
                !property.Value.TryGetProperty("fixed", out var fixedResult) ||
                fixedResult.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var injected = GetString(property.Value, "injected_test");
            if (string.IsNullOrWhiteSpace(injected)) continue;
            var category = GetBoolean(fixedResult, "compile_error")
                ? "fixed-compile-error"
                : GetBoolean(fixedResult, "runtime_error")
                    ? "fixed-runtime-error"
                    : GetBoolean(fixedResult, "autogen_failed")
                        ? "fixed-test-failure"
                        : string.Empty;
            if (category.Length == 0) continue;
            candidates.Add(new RepairCandidate(property.Name, injected, category));
        }
        return candidates;
    }

    private static RepairCandidate? ChooseCandidate(
        IReadOnlyList<RepairCandidate> candidates,
        string bugId,
        int? anchorIndex,
        int seed)
    {
        if (anchorIndex.HasValue)
        {
            var anchored = candidates.FirstOrDefault(candidate => CandidateIndex(candidate.Id) == anchorIndex.Value);
            if (anchored is not null) return anchored;
        }
        return candidates.OrderBy(candidate => StableHash($"{seed}|{bugId}|{candidate.Id}"), StringComparer.Ordinal).FirstOrDefault();
    }

    private static IReadOnlyList<string> SelectBalanced(
        IReadOnlyList<PilotSamplingFrameEntry> entries,
        IEnumerable<string> anchorBugIds,
        int targetSize,
        int seed)
    {
        var eligible = entries.Where(entry => entry.RepairCandidateEligible).ToArray();
        var eligibleIds = eligible.Select(entry => entry.BugId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedOrder = anchorBugIds.Where(eligibleIds.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var selected = selectedOrder.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var projectCounts = eligible.Select(entry => entry.ProjectId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                project => project,
                project => eligible.Count(entry => selected.Contains(entry.BugId) && entry.ProjectId.Equals(project, StringComparison.OrdinalIgnoreCase)),
                StringComparer.OrdinalIgnoreCase);
        var round = 0;
        while (selected.Count < Math.Min(targetSize, eligible.Length))
        {
            var next = eligible.Where(entry => !selected.Contains(entry.BugId))
                .OrderBy(entry => projectCounts[entry.ProjectId])
                .ThenBy(entry => StableHash($"{seed}|{round}|{entry.ProjectId}"), StringComparer.Ordinal)
                .ThenBy(entry => StableHash($"{seed}|{entry.BugId}"), StringComparer.Ordinal)
                .First();
            selected.Add(next.BugId);
            selectedOrder.Add(next.BugId);
            projectCounts[next.ProjectId]++;
            round++;
        }
        return selectedOrder;
    }

    private static async Task<IReadOnlyDictionary<string, int?>> LoadAnchorsAsync(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(manifestPath)) return result;
        await using var stream = File.OpenRead(manifestPath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var tasks = document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("tasks", out var taskArray)
            ? taskArray
            : default;
        if (tasks.ValueKind != JsonValueKind.Array) return result;
        foreach (var task in tasks.EnumerateArray())
        {
            var independenceUnit = GetString(task, "independenceUnitId");
            var projectId = GetString(task, "projectId");
            var numberSeparator = independenceUnit.LastIndexOf('#');
            if (numberSeparator < 0) continue;
            var project = Projects.FirstOrDefault(item =>
                item.Value.ProjectId.Equals(projectId, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(project.Key)) continue;
            var bugId = $"{project.Key}-{independenceUnit[(numberSeparator + 1)..]}";
            result[bugId] = CandidateIndex(GetString(task, "artifactId"));
        }
        return result;
    }

    private static async Task WriteCandidateCsvAsync(
        IReadOnlyList<PilotSamplingFrameEntry> entries,
        string path,
        CancellationToken cancellationToken)
    {
        var csv = new StringBuilder();
        csv.AppendLine("selection_order,selected,bug_id,project_id,project_repository_url,fixed_revision,buggy_revision,pull_request_url,bug_report_path,candidate_id,generated_test_file,injected_test,test_class_name,failing_test_method,initial_failure_category,known_bug_oracle_tests,repair_candidate_eligible,generated_test_present,repository_source_present,ready_to_reconstruct,reconstruction_status");
        foreach (var entry in entries)
        {
            csv.AppendLine(string.Join(",",
                entry.SelectionOrder,
                Bool(entry.Selected),
                Csv(entry.BugId),
                Csv(entry.ProjectId),
                Csv(entry.ProjectRepositoryUrl),
                Csv(entry.FixedRevision),
                Csv(entry.BuggyRevision),
                Csv(entry.PullRequestUrl),
                Csv(entry.BugReportPath),
                Csv(entry.CandidateId),
                Csv(entry.GeneratedTestFile),
                Csv(entry.InjectedTest),
                Csv(entry.TestClassName),
                Csv(entry.FailingTestMethod),
                Csv(entry.InitialFailureCategory),
                entry.KnownBugOracleTests,
                Bool(entry.RepairCandidateEligible),
                Bool(entry.GeneratedTestPresent),
                Bool(entry.RepositorySourcePresent),
                Bool(entry.ReadyToReconstruct),
                Csv(entry.ReconstructionStatus)));
        }
        await File.WriteAllTextAsync(path, csv.ToString(), ExcelUtf8, cancellationToken);
    }

    private static async Task WriteSummaryCsvAsync(
        IReadOnlyList<PilotSamplingFrameEntry> entries,
        string path,
        CancellationToken cancellationToken)
    {
        var csv = new StringBuilder("project_id,verified_bugs,eligible_bugs,selected_bugs,selected_generated_tests_present,repository_source_present,ready_to_reconstruct\n");
        foreach (var group in entries.GroupBy(entry => entry.ProjectId, StringComparer.OrdinalIgnoreCase)
                                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var rows = group.ToArray();
            csv.AppendLine(string.Join(",",
                Csv(group.Key),
                rows.Length,
                rows.Count(entry => entry.RepairCandidateEligible),
                rows.Count(entry => entry.Selected),
                rows.Count(entry => entry.Selected && entry.GeneratedTestPresent),
                Bool(rows.Any(entry => entry.RepositorySourcePresent)),
                rows.Count(entry => entry.Selected && entry.ReadyToReconstruct)));
        }
        await File.WriteAllTextAsync(path, csv.ToString(), ExcelUtf8, cancellationToken);
    }

    private static int? CandidateIndex(string value)
    {
        var match = CandidateIndexPattern.Match(value ?? string.Empty);
        return match.Success && int.TryParse(match.Groups["number"].Value, out var number) ? number : null;
    }
    private static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    private static bool GetBoolean(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
    private static int CountArray(JsonElement element, string objectProperty, string arrayProperty) =>
        element.TryGetProperty(objectProperty, out var nested) &&
        nested.ValueKind == JsonValueKind.Object &&
        nested.TryGetProperty(arrayProperty, out var values) &&
        values.ValueKind == JsonValueKind.Array
            ? values.GetArrayLength()
            : 0;
    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static string StableHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Bool(bool value) => value ? "TRUE" : "FALSE";
    private static string Csv(string? value)
    {
        var text = value ?? string.Empty;
        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : text;
    }

    private sealed record ProjectInfo(string ProjectId, string RepositoryUrl, string RepositoryDirectory);
    private sealed record RepairCandidate(string Id, string InjectedTest, string FailureCategory);
}
