namespace RQ3Evaluator.Wpf;

public static class MetricsService
{
    public static void Write(string folder, IReadOnlyList<(Prediction P, Gold G)> scored, string predictionsSha256)
    {
        if (scored.Count == 0) return;
        var metrics = scored.GroupBy(x => x.P.Verifier).Select(group =>
        {
            var rows = group.ToArray();
            var repair = rows.Where(x => x.G.Decision == "REPAIR_REQUIRED").ToArray();
            var predictedVerified = rows.Where(x => x.P.Decision == "VERIFIED").ToArray();
            var falsePass = rows.Where(x => x.G.Decision == "REPAIR_REQUIRED" && x.P.Decision == "VERIFIED").ToArray();
            var covered = rows.Where(x => x.P.Decision != "ABSTAIN").ToArray();
            var falsePassPrecision = Rate(falsePass.Length, predictedVerified.Length);
            var falsePassRecall = Rate(falsePass.Length, repair.Length);
            var falsePassF1 = F1(falsePassPrecision, falsePassRecall);
            return new
            {
                verifier = group.Key,
                cases = rows.Length,
                decision_accuracy = Rate(rows.Count(x => x.P.Decision == x.G.Decision), rows.Length),
                false_pass_precision = falsePassPrecision,
                false_pass_recall = falsePassRecall,
                false_pass_f1 = falsePassF1,
                false_acceptance_rate = falsePassRecall,
                macro_attribution_f1 = MacroCauseF1(rows),
                routing_accuracy = Rate(rows.Count(x => x.P.Route == x.G.Route), rows.Length),
                abstention_rate = Rate(rows.Count(x => x.P.Decision == "ABSTAIN"), rows.Length),
                decision_coverage = Rate(covered.Length, rows.Length),
                selective_accuracy = Rate(covered.Count(x => x.P.Decision == x.G.Decision), covered.Length),
                api_or_output_error_count = rows.Count(x => x.P.ExecutionStatus != "OK"),
                input_tokens = rows.Sum(x => x.P.InputTokens),
                output_tokens = rows.Sum(x => x.P.OutputTokens),
                api_calls = rows.Sum(x => x.P.ApiCalls),
                cost_usd = Math.Round(rows.Sum(x => x.P.CostUsd), 8)
            };
        }).ToArray();

        var result = new
        {
            schema_version = "rq3-wpf-pilot-metrics-v1.1",
            generated_at = DateTimeOffset.Now,
            predictions_sha256 = predictionsSha256,
            prediction_rows = scored.Count,
            private_gold_join_stage = "POST_PREDICTION_FINALIZATION",
            metrics
        };
        var target = Path.Combine(folder, "pilot_metrics.json");
        var temp = target + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, target, true);
    }

    private static double MacroCauseF1(IReadOnlyCollection<(Prediction P, Gold G)> rows)
    {
        var labels = rows.Select(x => x.G.Cause).Distinct().ToArray();
        if (labels.Length == 0) return 0;
        var scores = labels.Select(label =>
        {
            var tp = rows.Count(x => x.G.Cause == label && x.P.Cause == label);
            var fp = rows.Count(x => x.G.Cause != label && x.P.Cause == label);
            var fn = rows.Count(x => x.G.Cause == label && x.P.Cause != label);
            return F1(Rate(tp, tp + fp), Rate(tp, tp + fn));
        });
        return Math.Round(scores.Average(), 6);
    }

    private static double Rate(int numerator, int denominator) => denominator == 0 ? 0 : Math.Round(numerator / (double)denominator, 6);
    private static double F1(double precision, double recall) => precision + recall == 0 ? 0 : Math.Round(2 * precision * recall / (precision + recall), 6);
}
