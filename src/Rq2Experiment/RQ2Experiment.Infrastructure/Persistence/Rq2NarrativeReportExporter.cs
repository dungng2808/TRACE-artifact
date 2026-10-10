using System.Globalization;
using System.Text;
using RQ2Experiment.Core.Domain;
using RQ2Experiment.Core.Engine;

namespace RQ2Experiment.Infrastructure.Persistence;

public static class Rq2NarrativeReportExporter
{
    public static async Task<string> ExportAsync(
        IReadOnlyList<ExperimentRunResult> results,
        Rq2StatisticalAnalysis analysis,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(analysis);
        if (results.Count == 0) throw new ArgumentException("Cannot report empty results.", nameof(results));

        var batchId = results.Select(result => result.BatchId).Distinct(StringComparer.Ordinal).Single();
        var directory = Path.Combine(Path.GetFullPath(outputDirectory), "analysis");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"batch-{Sanitize(batchId)}-rq2-report.md");
        var report = Build(results, analysis);
        await File.WriteAllTextAsync(path, report, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken);
        return path;
    }

    private static string Build(IReadOnlyList<ExperimentRunResult> results, Rq2StatisticalAnalysis analysis)
    {
        var first = results[0];
        var plan = first.StudyPlan;
        var independentUnits = results.Select(result => result.IndependenceUnitId)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var projects = results.Select(result => result.ProjectId)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var strategyRows = Enum.GetValues<RepairStrategy>()
            .Select(strategy => Summarize(results.Where(result => result.Strategy == strategy).ToArray(), strategy))
            .ToArray();
        var claim = Rq2ClaimEvaluator.Evaluate(results, analysis);
        var measurementRows = results
            .Where(result => result.Status == RunStatus.Success)
            .ToArray();
        var builder = new StringBuilder();

        builder.AppendLine("# Kết quả RQ2 — Traceability-guided scenario-local repair");
        builder.AppendLine();
        builder.AppendLine("## Phạm vi và tái lập");
        builder.AppendLine();
        builder.AppendLine($"- **Batch:** `{first.BatchId}`");
        builder.AppendLine($"- **Dataset:** {first.DatasetId}; {independentUnits} đơn vị lỗi độc lập từ {projects} project.");
        builder.AppendLine($"- **Thiết kế:** {results.Count} runs; mỗi đơn vị được ghép cặp TGSLR, FTMR và FCR; seed lịch chạy {first.ScheduleSeed}.");
        builder.AppendLine($"- **LLM:** {first.ProviderName} / `{first.ModelName}` ({first.ApiProtocol}); tối đa {first.ConfiguredMaxIterations} iteration và {first.ConfiguredTokenBudget:N0} token/run.");
        builder.AppendLine($"- **Prompt contract:** `{PromptAuditEvidence.CurrentContractVersion}`; {results.Count(result => result.PromptAudit.ContractSatisfied)}/{results.Count} run có audit hợp lệ.");
        builder.AppendLine();

        builder.AppendLine("## Kết quả theo treatment");
        builder.AppendLine();
        builder.AppendLine("| Treatment | Runs | Success | Success rate | Avg token | Avg API calls | Avg iterations | Regression | Coverage measured | Mutation measured |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var row in strategyRows)
        {
            builder.AppendLine($"| {row.Name} | {row.Runs} | {row.Successes} | {Percent(row.SuccessRate)} | {Number(row.AverageTokens)} | {Number(row.AverageApiCalls)} | {Number(row.AverageIterations)} | {row.Regressions} | {row.CoverageMeasured}/{row.Successes} | {row.MutationMeasured}/{row.Successes} |");
        }
        builder.AppendLine();

        builder.AppendLine("## So sánh ghép cặp");
        builder.AppendLine();
        builder.AppendLine("| Comparison | Paired units | TGSLR | Baseline | Delta | Win/Tie/Loss | Cluster 95% CI | Δ token | Diễn giải |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---|");
        foreach (var comparison in analysis.Comparisons)
        {
            var interpretation = analysis.Readiness.InferentialReady
                ? comparison.Conclusion
                : "Mô tả; CI chỉ thăm dò vì pilot/protocol chưa analysis-ready.";
            builder.AppendLine($"| {comparison.Comparison} | {comparison.PairedTasks} | {Percent(comparison.TgslrTaskMeanSuccessRate)} | {Percent(comparison.BaselineTaskMeanSuccessRate)} | {comparison.SuccessRateDeltaDisplay} | {comparison.TaskWinTieLossDisplay} | {comparison.ClusterBootstrapIntervalDisplay} | {comparison.MeanTokenDeltaDisplay} | {interpretation} |");
        }
        builder.AppendLine();

        builder.AppendLine("## Độ đầy đủ measurement");
        builder.AppendLine();
        builder.AppendLine($"Trong {measurementRows.Length} successful repairs, coverage hoàn tất {measurementRows.Count(result => result.FinalCoverageStatus == MeasurementStatus.Succeeded)}/{measurementRows.Length}; mutation hoàn tất {measurementRows.Count(result => result.FinalMutationStatus == MeasurementStatus.Succeeded)}/{measurementRows.Length}.");
        builder.AppendLine();
        builder.AppendLine("| Metric | Status | Count |");
        builder.AppendLine("|---|---|---:|");
        foreach (var row in MeasurementStatusRows(results, coverage: true))
            builder.AppendLine($"| Coverage | {row.Status} | {row.Count} |");
        foreach (var row in MeasurementStatusRows(results, coverage: false))
            builder.AppendLine($"| Mutation | {row.Status} | {row.Count} |");
        builder.AppendLine();

        builder.AppendLine("## Trả lời RQ2");
        builder.AppendLine();
        var ftmr = analysis.Comparisons.FirstOrDefault(item => item.Comparison.Contains("FTMR", StringComparison.Ordinal));
        var fcr = analysis.Comparisons.FirstOrDefault(item => item.Comparison.Contains("FCR", StringComparison.Ordinal));
        if (ftmr is not null)
            builder.AppendLine(DescribePrimaryComparison(ftmr));
        if (fcr is not null)
            builder.AppendLine(DescribePrimaryComparison(fcr));
        builder.AppendLine();
        builder.AppendLine("Tuy nhiên, đây **không phải kết luận confirmatory**. Không diễn giải các chênh lệch là có ý nghĩa thống kê hoặc khẳng định TGSLR tốt hơn baseline ngoài phạm vi pilot.");
        builder.AppendLine();

        builder.AppendLine("## Claim gate và power projection");
        builder.AppendLine();
        builder.AppendLine($"**{claim.Status}.** {claim.Explanation}");
        builder.AppendLine();
        builder.AppendLine("| Điều kiện | Đạt | Bằng chứng |");
        builder.AppendLine("|---|---:|---|");
        foreach (var criterion in claim.Criteria)
            builder.AppendLine($"| {criterion.Name} | {(criterion.Passed ? "Có" : "Không")} | {criterion.Evidence.Replace("|", "/")} |");
        builder.AppendLine();
        builder.AppendLine("Power dưới đây là **ước lượng thăm dò từ pilot**, không phải power plan đã đăng ký trước:");
        builder.AppendLine();
        builder.AppendLine("| Comparison | Pilot W/L | Discordant rate | P(win | discordant) | Required units projected | Power at n=100 |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach (var projection in claim.PilotPowerProjections)
            builder.AppendLine($"| {projection.Comparison} | {projection.Wins}/{projection.Losses} | {Percent(projection.DiscordantRate)} | {Percent(projection.ConditionalWinProbability)} | {projection.RequiredDisplay} | {projection.PowerAt100Display} |");
        builder.AppendLine();

        builder.AppendLine("## Giới hạn và điều kiện để xác nhận");
        builder.AppendLine();
        builder.AppendLine($"- Cỡ mẫu độc lập là {independentUnits}/{plan.RequiredIndependentUnits}; power/sampling plan hiện provisional={plan.Provisional.ToString().ToLowerInvariant()}.");
        builder.AppendLine($"- Outcome contract hiện provisional={plan.OutcomePlanProvisional.ToString().ToLowerInvariant()}; không được freeze hồi tố sau khi đã quan sát batch này.");
        builder.AppendLine("- Coverage và mutation bị thiếu do tool/report failure ở một số project; chỉ dùng như mô tả trên subset đo được, không thay thế bằng 0.");
        builder.AppendLine("- Batch tiếp theo cần protocol frozen trước execution, measurement profile tương thích theo project, và sampling frame đạt cỡ mẫu độc lập đã khai báo.");
        builder.AppendLine();

        builder.AppendLine("## Tệp bằng chứng");
        builder.AppendLine();
        builder.AppendLine("- `runs.jsonl`: dữ liệu append-only theo từng run.");
        builder.AppendLine("- `runs.csv`, `iterations.csv`: dữ liệu cấp run/iteration.");
        builder.AppendLine("- `rq2-summary.csv`, `rq2-pairwise.csv`, `rq2-outcomes.csv`: bảng phân tích có thể tái lập.");
        builder.AppendLine("- `measurement-diagnostics.csv` (batch chạy bằng Phase 7.11+): triage lỗi JaCoCo/PIT.");
        return builder.ToString();
    }

    private static IEnumerable<(string Status, int Count)> MeasurementStatusRows(
        IReadOnlyList<ExperimentRunResult> results,
        bool coverage) => results
        .GroupBy(result => coverage ? result.FinalCoverageStatus : result.FinalMutationStatus)
        .OrderBy(group => group.Key)
        .Select(group => (group.Key.ToString(), group.Count()));

    private static StrategySummary Summarize(IReadOnlyList<ExperimentRunResult> results, RepairStrategy strategy)
    {
        var successes = results.Count(result => result.Status == RunStatus.Success);
        return new StrategySummary(
            strategy.ToDisplayName(),
            results.Count,
            successes,
            results.Count == 0 ? 0 : successes / (double)results.Count,
            results.Count == 0 ? 0 : results.Average(result => result.TotalTokens),
            results.Count == 0 ? 0 : results.Average(result => result.TotalApiCalls),
            results.Count == 0 ? 0 : results.Average(result => result.Iterations.Count),
            results.Sum(result => result.RegressedTests),
            results.Count(result => result.Status == RunStatus.Success && result.FinalCoverageStatus == MeasurementStatus.Succeeded),
            results.Count(result => result.Status == RunStatus.Success && result.FinalMutationStatus == MeasurementStatus.Succeeded));
    }

    private static string Percent(double value) => value.ToString("P1", CultureInfo.InvariantCulture);
    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static string DescribePrimaryComparison(Rq2PairwiseComparison comparison)
    {
        var baseline = comparison.Comparison.Replace("TGSLR vs ", string.Empty, StringComparison.Ordinal);
        var direction = comparison.SuccessRateDelta > 0
            ? "TGSLR có success rate mô tả cao hơn baseline"
            : comparison.SuccessRateDelta < 0
                ? "TGSLR có success rate mô tả thấp hơn baseline"
                : "hai treatment có success rate mô tả bằng nhau";
        var tokenDirection = comparison.MeanTokenDelta > 0
            ? "TGSLR dùng nhiều token hơn"
            : comparison.MeanTokenDelta < 0
                ? "TGSLR dùng ít token hơn"
                : "mức token trung bình bằng nhau";
        return $"So với {baseline}, {direction}: {Percent(comparison.TgslrTaskMeanSuccessRate)} so với " +
               $"{Percent(comparison.BaselineTaskMeanSuccessRate)} (Δ {comparison.SuccessRateDeltaDisplay}); " +
               $"{tokenDirection} (Δ {comparison.MeanTokenDeltaDisplay}). Đây chỉ là mô tả của batch; " +
               $"không được suy diễn ưu thế của TGSLR khi claim gate chưa đạt.";
    }
    private static string Sanitize(string value) => new(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '-' : character).ToArray());

    private sealed record StrategySummary(
        string Name, int Runs, int Successes, double SuccessRate, double AverageTokens,
        double AverageApiCalls, double AverageIterations, int Regressions,
        int CoverageMeasured, int MutationMeasured);
}
