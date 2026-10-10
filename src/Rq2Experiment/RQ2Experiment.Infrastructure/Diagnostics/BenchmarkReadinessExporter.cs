using System.Text;
using System.Text.Json;

namespace RQ2Experiment.Infrastructure.Diagnostics;

public sealed record BenchmarkTaskReadiness(
    string TaskId,
    string ProjectId,
    string DatasetId,
    string ArtifactId,
    string IndependenceUnitId,
    string SamplingStratum,
    bool ManifestValid,
    bool FixedInitialFailureReproduced,
    bool BuggyOracleRequired,
    bool? BuggyBaselineCompiles,
    bool Ready,
    string ExclusionReason);

public sealed record BenchmarkReadinessExport(
    string TaskReadinessPath,
    string SamplingFrameSummaryPath,
    string JsonPath);

public static class BenchmarkReadinessExporter
{
    private static readonly Encoding ExcelUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    public static async Task<BenchmarkReadinessExport> ExportAsync(
        IReadOnlyList<BenchmarkTaskReadiness> rows,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        Directory.CreateDirectory(outputDirectory);

        var taskPath = Path.Combine(outputDirectory, "task-readiness.csv");
        var summaryPath = Path.Combine(outputDirectory, "sampling-frame-summary.csv");
        var jsonPath = Path.Combine(outputDirectory, "benchmark-readiness.json");
        var summaryRows = BuildSummary(rows);

        var taskCsv = new StringBuilder();
        taskCsv.AppendLine(
            "task_id,project_id,dataset_id,artifact_id,independence_unit_id,sampling_stratum," +
            "manifest_valid,fixed_initial_failure_reproduced,buggy_oracle_required,buggy_baseline_compiles,ready,exclusion_reason");
        foreach (var row in rows.OrderBy(item => item.ProjectId, StringComparer.OrdinalIgnoreCase)
                                 .ThenBy(item => item.IndependenceUnitId, StringComparer.OrdinalIgnoreCase)
                                 .ThenBy(item => item.TaskId, StringComparer.OrdinalIgnoreCase))
        {
            taskCsv.AppendLine(string.Join(",",
                Csv(row.TaskId),
                Csv(row.ProjectId),
                Csv(row.DatasetId),
                Csv(row.ArtifactId),
                Csv(row.IndependenceUnitId),
                Csv(row.SamplingStratum),
                Bool(row.ManifestValid),
                Bool(row.FixedInitialFailureReproduced),
                Bool(row.BuggyOracleRequired),
                row.BuggyBaselineCompiles.HasValue ? Bool(row.BuggyBaselineCompiles.Value) : "N/A",
                Bool(row.Ready),
                Csv(row.ExclusionReason)));
        }

        var summaryCsv = new StringBuilder();
        summaryCsv.AppendLine("dimension,value,tasks,independent_units,ready_tasks,ready_independent_units");
        foreach (var row in summaryRows)
        {
            summaryCsv.AppendLine(string.Join(",",
                Csv(row.Dimension),
                Csv(row.Value),
                row.Tasks,
                row.IndependentUnits,
                row.ReadyTasks,
                row.ReadyIndependentUnits));
        }

        await File.WriteAllTextAsync(taskPath, taskCsv.ToString(), ExcelUtf8, cancellationToken);
        await File.WriteAllTextAsync(summaryPath, summaryCsv.ToString(), ExcelUtf8, cancellationToken);
        await File.WriteAllTextAsync(
            jsonPath,
            JsonSerializer.Serialize(
                new
                {
                    generatedAtUtc = DateTimeOffset.UtcNow,
                    totals = summaryRows[0],
                    summary = summaryRows,
                    tasks = rows
                },
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }),
            cancellationToken);

        return new BenchmarkReadinessExport(taskPath, summaryPath, jsonPath);
    }

    private static IReadOnlyList<ReadinessSummaryRow> BuildSummary(
        IReadOnlyList<BenchmarkTaskReadiness> rows)
    {
        var result = new List<ReadinessSummaryRow>
        {
            Summarize("overall", "all", rows)
        };
        result.AddRange(rows
            .GroupBy(row => Display(row.ProjectId), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => Summarize("project", group.Key, group)));
        result.AddRange(rows
            .GroupBy(row => Display(row.SamplingStratum), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => Summarize("stratum", group.Key, group)));
        result.AddRange(rows
            .GroupBy(
                row => $"{Display(row.ProjectId)} | {Display(row.SamplingStratum)}",
                StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => Summarize("project_stratum", group.Key, group)));
        return result;
    }

    private static ReadinessSummaryRow Summarize(
        string dimension,
        string value,
        IEnumerable<BenchmarkTaskReadiness> source)
    {
        var rows = source.ToArray();
        return new ReadinessSummaryRow(
            dimension,
            value,
            rows.Length,
            CountUnits(rows),
            rows.Count(row => row.Ready),
            CountUnits(rows.Where(row => row.Ready)));
    }

    private static int CountUnits(IEnumerable<BenchmarkTaskReadiness> rows) => rows
        .Select(row => string.IsNullOrWhiteSpace(row.IndependenceUnitId) ? $"task:{row.TaskId}" : row.IndependenceUnitId)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count();

    private static string Display(string value) => string.IsNullOrWhiteSpace(value) ? "(missing)" : value;
    private static string Bool(bool value) => value ? "TRUE" : "FALSE";
    private static string Csv(string? value)
    {
        var text = value ?? string.Empty;
        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : text;
    }

    private sealed record ReadinessSummaryRow(
        string Dimension,
        string Value,
        int Tasks,
        int IndependentUnits,
        int ReadyTasks,
        int ReadyIndependentUnits);
}
