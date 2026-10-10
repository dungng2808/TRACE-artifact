using System.Globalization;
using System.Text.Json;
using RQ3Evaluator.Wpf.Models;

namespace RQ3Evaluator.Wpf.Services;

public sealed record MetricsDashboardData(
    string CohortStatus,
    int EvaluationCases,
    int PredictionRows,
    string PredictionsSha256,
    string ManifestSha256,
    List<ResultMetricRow> VerifierMetrics,
    List<PairedTestRow> PairedTests,
    List<StabilityRecordRow> StabilityRecords,
    double? StabilityRate,
    int? StabilityComparisonTuples,
    int? StabilityAgreementCount,
    double? StabilityCostUsd
);

public sealed record PairedTestRow(
    string Comparison,
    int B2CorrectBaselineWrong,
    int BaselineCorrectB2Wrong,
    int DiscordantPairs,
    double ExactMcnemarP,
    double? HolmAdjustedP
);

public static class Phase6MetricsService
{
    public static MetricsDashboardData? LoadMetrics(string metricsJsonPath, string? stabilityJsonPath = null)
    {
        if (!File.Exists(metricsJsonPath)) return null;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(metricsJsonPath));
            var root = doc.RootElement;

            var status = root.GetProperty("cohort_status").GetString() ?? "";
            var casesCount = root.GetProperty("evaluation_cases").GetInt32();
            var rowsCount = root.GetProperty("prediction_rows").GetInt32();
            var predSha = root.GetProperty("predictions_sha256").GetString() ?? "";
            var manSha = root.GetProperty("manifest_sha256").GetString() ?? "";

            // Parse bootstrap map
            var bootstrapMap = new Dictionary<string, (double Lower, double Upper)>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("clustered_bootstrap", out var bootArr))
            {
                foreach (var b in bootArr.EnumerateArray())
                {
                    var ver = b.GetProperty("verifier").GetString()!;
                    var ci = b.GetProperty("decision_accuracy_clustered_bootstrap_95");
                    var lower = ci.GetProperty("lower").GetDouble();
                    var upper = ci.GetProperty("upper").GetDouble();
                    bootstrapMap[ver] = (lower, upper);
                }
            }

            // Parse verifier metrics
            var metricRows = new List<ResultMetricRow>();
            if (root.TryGetProperty("metrics", out var metArr))
            {
                foreach (var m in metArr.EnumerateArray())
                {
                    var ver = m.GetProperty("verifier").GetString()!;
                    var cases = m.GetProperty("cases").GetInt32();
                    var decAcc = m.GetProperty("decision_accuracy").GetDouble();
                    var wilson = m.GetProperty("decision_accuracy_wilson_95");
                    var wLower = wilson.GetProperty("lower").GetDouble();
                    var wUpper = wilson.GetProperty("upper").GetDouble();

                    var wCiStr = $"[{wLower:P1}, {wUpper:P1}]";
                    var bCiStr = bootstrapMap.TryGetValue(ver, out var bci)
                        ? $"[{bci.Lower:P1}, {bci.Upper:P1}]"
                        : "N/A";

                    var routAcc = m.GetProperty("routing_accuracy").GetDouble();
                    var macroF1 = m.GetProperty("macro_attribution_f1").GetDouble();
                    var absRate = m.GetProperty("abstention_rate").GetDouble();
                    var selAcc = m.GetProperty("selective_accuracy").GetDouble();
                    var errors = m.GetProperty("api_or_output_error_count").GetInt32();
                    var calls = m.GetProperty("api_calls").GetInt32();
                    var inTok = m.GetProperty("input_tokens").GetInt32();
                    var outTok = m.GetProperty("output_tokens").GetInt32();
                    var cost = m.GetProperty("cost_usd").GetDouble();

                    metricRows.Add(new ResultMetricRow(
                        Verifier: ver,
                        Cases: cases,
                        DecisionAccuracy: decAcc,
                        DecisionAccuracyDisplay: decAcc.ToString("P2", CultureInfo.InvariantCulture),
                        Wilson95Ci: wCiStr,
                        Bootstrap95Ci: bCiStr,
                        RoutingAccuracy: routAcc,
                        RoutingAccuracyDisplay: routAcc.ToString("P2", CultureInfo.InvariantCulture),
                        MacroAttributionF1: macroF1,
                        MacroAttributionF1Display: macroF1.ToString("F4", CultureInfo.InvariantCulture),
                        AbstentionRate: absRate,
                        AbstentionRateDisplay: absRate.ToString("P2", CultureInfo.InvariantCulture),
                        SelectiveAccuracy: selAcc,
                        SelectiveAccuracyDisplay: selAcc.ToString("P2", CultureInfo.InvariantCulture),
                        ApiOrOutputErrors: errors,
                        ApiCalls: calls,
                        InputTokens: inTok,
                        OutputTokens: outTok,
                        CostUsd: cost,
                        CostUsdDisplay: "$" + cost.ToString("F6", CultureInfo.InvariantCulture)
                    ));
                }
            }

            // Parse paired tests
            var pairedRows = new List<PairedTestRow>();
            if (root.TryGetProperty("paired_tests", out var pairArr))
            {
                foreach (var p in pairArr.EnumerateArray())
                {
                    var comp = p.GetProperty("comparison").GetString()!;
                    var b = p.GetProperty("b2_correct_baseline_wrong").GetInt32();
                    var c = p.GetProperty("baseline_correct_b2_wrong").GetInt32();
                    var disc = p.GetProperty("discordant_pairs").GetInt32();
                    var pval = p.GetProperty("exact_mcnemar_p").GetDouble();
                    double? holm = p.TryGetProperty("holm_adjusted_p", out var h) && h.ValueKind == JsonValueKind.Number
                        ? h.GetDouble()
                        : null;

                    pairedRows.Add(new PairedTestRow(comp, b, c, disc, pval, holm));
                }
            }

            // Parse stability
            var stabRecords = new List<StabilityRecordRow>();
            double? stabRate = null;
            int? stabTuples = null;
            int? stabAgree = null;
            double? stabCost = null;

            if (!string.IsNullOrWhiteSpace(stabilityJsonPath) && File.Exists(stabilityJsonPath))
            {
                try
                {
                    using var stabDoc = JsonDocument.Parse(File.ReadAllText(stabilityJsonPath));
                    var sRoot = stabDoc.RootElement;
                    if (sRoot.TryGetProperty("tuple_agreement_rate", out var tr)) stabRate = tr.GetDouble();
                    if (sRoot.TryGetProperty("comparison_tuples", out var ct)) stabTuples = ct.GetInt32();
                    if (sRoot.TryGetProperty("tuple_agreement_count", out var ac)) stabAgree = ac.GetInt32();
                    if (sRoot.TryGetProperty("cost_usd", out var cu)) stabCost = cu.GetDouble();

                    if (sRoot.TryGetProperty("records", out var recArr))
                    {
                        foreach (var r in recArr.EnumerateArray())
                        {
                            stabRecords.Add(new StabilityRecordRow(
                                Repetition: r.GetProperty("repetition").GetInt32(),
                                CaseId: r.GetProperty("case_id").GetString()!,
                                Verifier: r.GetProperty("verifier").GetString()!,
                                ReferenceDecision: r.GetProperty("reference_decision").GetString()!,
                                RepeatedDecision: r.GetProperty("repeated_decision").GetString()!,
                                ReferenceCause: r.GetProperty("reference_cause").GetString()!,
                                RepeatedCause: r.GetProperty("repeated_cause").GetString()!,
                                ReferenceRoute: r.GetProperty("reference_route").GetString()!,
                                RepeatedRoute: r.GetProperty("repeated_route").GetString()!,
                                TupleAgreement: r.GetProperty("tuple_agreement").GetBoolean(),
                                ExecutionStatus: r.GetProperty("execution_status").GetString()!,
                                CostUsd: r.GetProperty("cost_usd").GetDouble()
                            ));
                        }
                    }
                }
                catch
                {
                    // ignored
                }
            }

            return new MetricsDashboardData(
                CohortStatus: status,
                EvaluationCases: casesCount,
                PredictionRows: rowsCount,
                PredictionsSha256: predSha,
                ManifestSha256: manSha,
                VerifierMetrics: metricRows,
                PairedTests: pairedRows,
                StabilityRecords: stabRecords,
                StabilityRate: stabRate,
                StabilityComparisonTuples: stabTuples,
                StabilityAgreementCount: stabAgree,
                StabilityCostUsd: stabCost
            );
        }
        catch
        {
            return null;
        }
    }
}
