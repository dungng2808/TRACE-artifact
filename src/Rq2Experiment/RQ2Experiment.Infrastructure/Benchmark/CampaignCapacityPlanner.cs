using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RQ2Experiment.Infrastructure.Benchmark;

public sealed record CampaignCapacityPlanResult(
    string PlanPath,
    string CandidateCsvPath,
    string ReportPath,
    int TargetIndependentUnits,
    int AvailableIndependentUnits,
    int SelectedIndependentUnits,
    int AdditionalIndependentUnitsRequired,
    int AvailableProjects,
    bool IsRunnable);

/// <summary>
/// Freezes a deterministic campaign selection from a sampling frame.  It never
/// duplicates a bug to satisfy a requested sample size: repetitions belong to
/// the stochastic LLM treatment, not to the independent-unit denominator.
/// </summary>
public static class CampaignCapacityPlanner
{
    private static readonly Encoding ExcelUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    public static async Task<CampaignCapacityPlanResult> BuildAsync(
        string samplingFramePath,
        int targetIndependentUnits,
        int selectionSeed,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        if (targetIndependentUnits <= 0) throw new ArgumentOutOfRangeException(nameof(targetIndependentUnits));
        if (selectionSeed == 0) throw new ArgumentOutOfRangeException(nameof(selectionSeed));

        var sourcePath = Path.GetFullPath(samplingFramePath);
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("Sampling-frame JSON was not found.", sourcePath);
        await using var stream = File.OpenRead(sourcePath);
        var frame = await JsonSerializer.DeserializeAsync<SamplingFrameDocument>(
            stream,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true },
            cancellationToken) ?? throw new InvalidDataException("Sampling-frame JSON is empty or invalid.");

        var available = frame.Entries
            .Where(entry => entry.RepairCandidateEligible && entry.ReadyToReconstruct)
            .GroupBy(entry => entry.BugId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(entry => entry.CandidateId, StringComparer.OrdinalIgnoreCase).First())
            .ToArray();
        var selected = SelectBalanced(available, targetIndependentUnits, selectionSeed);
        var availableProjects = available.Select(entry => entry.ProjectId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var additional = Math.Max(0, targetIndependentUnits - available.Length);
        var runnable = additional == 0;

        Directory.CreateDirectory(outputDirectory);
        var candidateCsvPath = Path.Combine(outputDirectory, "campaign-selected-candidates.csv");
        var planPath = Path.Combine(outputDirectory, "campaign-capacity-plan.json");
        var reportPath = Path.Combine(outputDirectory, "campaign-capacity-report.md");
        await WriteCsvAsync(selected, candidateCsvPath, cancellationToken);
        await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(new
        {
            contractVersion = "RQ2-CAMPAIGN-CAPACITY-1",
            createdUtc = DateTimeOffset.UtcNow,
            sourceSamplingFrame = sourcePath,
            sourceSamplingFrameSha256 = HashFile(sourcePath),
            selectionSeed,
            targetIndependentUnits,
            availableIndependentUnits = available.Length,
            selectedIndependentUnits = selected.Count,
            additionalIndependentUnitsRequired = additional,
            availableProjects,
            isRunnable = runnable,
            selectionRule = "One eligible, reconstructable bug is one independent unit. Select without replacement, balancing project counts then using a stable seed tie-breaker.",
            selected
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), cancellationToken);
        await File.WriteAllTextAsync(reportPath, BuildReport(
            sourcePath, targetIndependentUnits, available.Length, selected.Count, additional, availableProjects, runnable, selected), cancellationToken);

        return new CampaignCapacityPlanResult(
            planPath, candidateCsvPath, reportPath, targetIndependentUnits, available.Length,
            selected.Count, additional, availableProjects, runnable);
    }

    private static IReadOnlyList<PilotSamplingFrameEntry> SelectBalanced(
        IReadOnlyList<PilotSamplingFrameEntry> candidates,
        int target,
        int seed)
    {
        var count = Math.Min(target, candidates.Count);
        var selected = new List<PilotSamplingFrameEntry>(count);
        var selectedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var projectCounts = candidates.Select(entry => entry.ProjectId).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(project => project, _ => 0, StringComparer.OrdinalIgnoreCase);
        for (var round = 0; selected.Count < count; round++)
        {
            var next = candidates.Where(entry => !selectedIds.Contains(entry.BugId))
                .OrderBy(entry => projectCounts[entry.ProjectId])
                .ThenBy(entry => StableHash($"{seed}|{round}|{entry.ProjectId}|{entry.BugId}"), StringComparer.Ordinal)
                .First();
            selectedIds.Add(next.BugId);
            projectCounts[next.ProjectId]++;
            selected.Add(next with { Selected = true, SelectionOrder = selected.Count + 1 });
        }
        return selected;
    }

    private static async Task WriteCsvAsync(
        IReadOnlyList<PilotSamplingFrameEntry> selected,
        string path,
        CancellationToken cancellationToken)
    {
        var csv = new StringBuilder("selection_order,bug_id,project_id,candidate_id,initial_failure_category,ready_to_reconstruct\n");
        foreach (var entry in selected)
        {
            csv.Append(entry.SelectionOrder).Append(',')
                .Append(Csv(entry.BugId)).Append(',')
                .Append(Csv(entry.ProjectId)).Append(',')
                .Append(Csv(entry.CandidateId)).Append(',')
                .Append(Csv(entry.InitialFailureCategory)).Append(",TRUE\n");
        }
        await File.WriteAllTextAsync(path, csv.ToString(), ExcelUtf8, cancellationToken);
    }

    private static string BuildReport(string source, int target, int available, int selected, int missing,
        int projects, bool runnable, IReadOnlyList<PilotSamplingFrameEntry> entries)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# RQ2 campaign capacity report");
        builder.AppendLine();
        builder.AppendLine($"- Source sampling frame: `{source}`");
        builder.AppendLine($"- Target independent bug units: **{target}**");
        builder.AppendLine($"- Eligible reconstructable independent units available: **{available}** across **{projects}** projects");
        builder.AppendLine($"- Frozen selected units: **{selected}**");
        builder.AppendLine($"- Additional independent units required: **{missing}**");
        builder.AppendLine($"- Runnable as a {target}-unit campaign: **{(runnable ? "yes" : "no")}**");
        builder.AppendLine();
        if (!runnable)
        {
            builder.AppendLine("> Do not fill the shortfall by repeating a bug or using multiple generated tests from the same bug. Those observations are not independent and would overstate the evidence.");
            builder.AppendLine();
        }
        builder.AppendLine("## Selected units by project");
        builder.AppendLine();
        foreach (var group in entries.GroupBy(entry => entry.ProjectId).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
            builder.AppendLine($"- {group.Key}: {group.Count()}");
        return builder.ToString();
    }

    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static string StableHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Csv(string value) => value.IndexOfAny([',', '"', '\r', '\n']) >= 0
        ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
        : value;

    private sealed record SamplingFrameDocument(IReadOnlyList<PilotSamplingFrameEntry> Entries);
}
