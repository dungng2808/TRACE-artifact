using System.Text;
using RQ2Experiment.Core.Domain;

namespace RQ2Experiment.Infrastructure.Persistence;

public sealed record Rq2TuningTriageExportResult(string CandidatesPath, string SummaryPath);

/// <summary>
/// Produces development-only evidence from a completed pilot.  It deliberately
/// does not estimate treatment superiority; its purpose is to identify why a
/// TGSLR repair lost to one of the baselines before a separate tuning study.
/// </summary>
public static class Rq2TuningTriageExporter
{
    public static async Task<Rq2TuningTriageExportResult> ExportAsync(
        IReadOnlyList<ExperimentRunResult> results,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (results.Count == 0) throw new ArgumentException("Cannot triage empty results.", nameof(results));

        var batchId = results.Select(result => result.BatchId).Distinct(StringComparer.Ordinal).Single();
        var directory = Path.Combine(Path.GetFullPath(outputDirectory), "analysis");
        Directory.CreateDirectory(directory);
        var safeBatch = Sanitize(batchId);
        var candidatesPath = Path.Combine(directory, $"batch-{safeBatch}-tgslr-tuning-candidates.csv");
        var summaryPath = Path.Combine(directory, $"batch-{safeBatch}-tgslr-tuning-triage.md");

        var rows = BuildRows(results);
        await WriteCandidatesAsync(candidatesPath, rows, cancellationToken);
        await File.WriteAllTextAsync(
            summaryPath,
            BuildSummary(rows, batchId),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            cancellationToken);
        return new Rq2TuningTriageExportResult(candidatesPath, summaryPath);
    }

    private static IReadOnlyList<TriageRow> BuildRows(IReadOnlyList<ExperimentRunResult> results) => results
        .GroupBy(result => new { result.TaskId, result.Repetition })
        .Select(group =>
        {
            var tgslr = group.SingleOrDefault(result => result.Strategy == RepairStrategy.TraceabilityGuidedScenarioLocal);
            var ftmr = group.SingleOrDefault(result => result.Strategy == RepairStrategy.FailingTestMethod);
            var fcr = group.SingleOrDefault(result => result.Strategy == RepairStrategy.FullClassRegeneration);
            return ToRow(tgslr, ftmr, fcr);
        })
        .Where(row => row is not null)
        .Cast<TriageRow>()
        .OrderByDescending(row => row.Priority)
        .ThenBy(row => row.ProjectId, StringComparer.Ordinal)
        .ThenBy(row => row.TaskId, StringComparer.Ordinal)
        .ToArray();

    private static TriageRow? ToRow(
        ExperimentRunResult? tgslr,
        ExperimentRunResult? ftmr,
        ExperimentRunResult? fcr)
    {
        if (tgslr is null) return null;
        var baselineSuccesses = new[] { ftmr, fcr }.Count(result => result?.Status == RunStatus.Success);
        var candidate = tgslr.Status != RunStatus.Success && baselineSuccesses > 0;
        var failedStage = tgslr.Status == RunStatus.BudgetExhausted
            ? "Budget exhausted"
            : tgslr.Stages.FailedStage;
        var category = candidate
            ? $"TGSLR failed; {baselineSuccesses} baseline(s) succeeded"
            : tgslr.Status == RunStatus.Success
                ? "TGSLR succeeded"
                : "TGSLR failed; no baseline succeeded";
        return new TriageRow(
            tgslr.TaskId,
            tgslr.ProjectId,
            tgslr.IndependenceUnitId,
            tgslr.ScenarioId,
            tgslr.Repetition,
            tgslr.Status.ToString(),
            ftmr?.Status.ToString() ?? "Missing",
            fcr?.Status.ToString() ?? "Missing",
            candidate,
            baselineSuccesses,
            failedStage,
            tgslr.FailureCategory.ToString(),
            tgslr.StopReason,
            tgslr.Iterations.Count,
            RecommendationFor(failedStage, tgslr.FailureCategory),
            DetailFor(failedStage, tgslr),
            candidate ? 2 : tgslr.Status == RunStatus.Success ? 0 : 1);
    }

    private static string RecommendationFor(string failedStage, RunFailureCategory category) =>
        failedStage switch
        {
            "LLM output" => "Audit JSON contract and structured-output compatibility; do not change treatment context yet.",
            "Source application" => "Audit target method/class mapping and traceability target path.",
            "Compilation" => "Audit scenario-to-source localization and add compiler feedback to a development-only prompt revision.",
            "Focal test" => "Audit scenario expected outcome, assertion semantics, and focal-method traceability.",
            "Buggy oracle" => "Audit negative behavior/oracle: the repaired test must fail on the buggy revision, not only pass on fixed.",
            "Regression" => "Audit preservation constraints for previously verified tests.",
            _ when category == RunFailureCategory.InvalidModelOutput => "Audit provider JSON mode/model compatibility before changing TGSLR semantics.",
            _ when category == RunFailureCategory.BudgetExhausted => "Inspect iteration feedback and budget use on a disjoint tuning set; keep confirmatory budget frozen.",
            _ => "Review the run artifacts and prompt audit on a disjoint tuning set."
        };

    private static string DetailFor(string failedStage, ExperimentRunResult result)
    {
        var detail = failedStage switch
        {
            "LLM output" => result.Stages.LlmOutput.Detail,
            "Source application" => result.Stages.SourceApplication.Detail,
            "Compilation" => result.Stages.Compilation.Detail,
            "Focal test" => result.Stages.FocalTest.Detail,
            "Buggy oracle" => result.Stages.BuggyOracle.Detail,
            "Regression" => result.Stages.Regression.Detail,
            _ => result.StopReason
        };
        return detail.Replace('\r', ' ').Replace('\n', ' ').Trim();
    }

    private static async Task WriteCandidatesAsync(
        string path,
        IReadOnlyList<TriageRow> rows,
        CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        await writer.WriteLineAsync(
            "task_id,project_id,independence_unit_id,scenario_id,repetition,tgslr_status,ftmr_status,fcr_status," +
            "tuning_candidate,baseline_successes,failed_stage,failure_category,stop_reason,iterations,recommendation,stage_detail");
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = new object?[]
            {
                row.TaskId, row.ProjectId, row.IndependenceUnitId, row.ScenarioId, row.Repetition,
                row.TgslrStatus, row.FtmrStatus, row.FcrStatus, row.IsTuningCandidate,
                row.BaselineSuccesses, row.FailedStage, row.FailureCategory, row.StopReason,
                row.Iterations, row.Recommendation, row.StageDetail
            };
            await writer.WriteLineAsync(string.Join(',', values.Select(Escape)));
        }
    }

    private static string BuildSummary(IReadOnlyList<TriageRow> rows, string batchId)
    {
        var candidates = rows.Where(row => row.IsTuningCandidate).ToArray();
        var builder = new StringBuilder();
        builder.AppendLine("# TGSLR tuning triage (development evidence only)");
        builder.AppendLine();
        builder.AppendLine($"- Batch: `{batchId}`");
        builder.AppendLine($"- Task-treatment blocks reviewed: {rows.Count}");
        builder.AppendLine($"- Candidates where TGSLR failed but at least one baseline succeeded: {candidates.Length}");
        builder.AppendLine();
        builder.AppendLine("This report is for diagnosis and prompt/system development only. Do not use these rows to claim superiority or to select a confirmatory outcome after observing results.");
        builder.AppendLine();
        builder.AppendLine("## Failure categories among tuning candidates");
        builder.AppendLine();
        builder.AppendLine("| Failed stage | Count | Suggested investigation |");
        builder.AppendLine("|---|---:|---|");
        foreach (var group in candidates.GroupBy(row => string.IsNullOrWhiteSpace(row.FailedStage) ? "No stage evidence" : row.FailedStage)
                     .OrderByDescending(group => group.Count()))
        {
            var recommendation = group.First().Recommendation.Replace("|", "/");
            builder.AppendLine($"| {group.Key} | {group.Count()} | {recommendation} |");
        }
        builder.AppendLine();
        builder.AppendLine("## Next use");
        builder.AppendLine();
        builder.AppendLine("Review the candidate CSV, choose a disjoint tuning set, and freeze one revised TGSLR prompt/version before any new confirmatory campaign.");
        return builder.ToString();
    }

    private static string Escape(object? value) => '"' + (value?.ToString() ?? string.Empty).Replace("\"", "\"\"") + '"';
    private static string Sanitize(string value) => new(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '-' : character).ToArray());

    private sealed record TriageRow(
        string TaskId,
        string ProjectId,
        string IndependenceUnitId,
        string ScenarioId,
        int Repetition,
        string TgslrStatus,
        string FtmrStatus,
        string FcrStatus,
        bool IsTuningCandidate,
        int BaselineSuccesses,
        string FailedStage,
        string FailureCategory,
        string StopReason,
        int Iterations,
        string Recommendation,
        string StageDetail,
        int Priority);
}
