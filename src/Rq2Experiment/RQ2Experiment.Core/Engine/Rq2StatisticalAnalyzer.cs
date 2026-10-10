using RQ2Experiment.Core.Domain;

namespace RQ2Experiment.Core.Engine;

public sealed record Rq2AnalysisReadiness(
    bool BatchComplete,
    bool ConfigurationComparable,
    bool PairingComplete,
    bool ProvenanceComplete,
    bool TreatmentIsolationComplete,
    bool SamplingPlanComplete,
    bool PowerPlanSatisfied,
    bool OutcomePlanComplete,
    bool ClusterAnalysisReady,
    bool ComparisonSamplesComplete,
    int IndependentTasks,
    int Projects,
    int RequiredIndependentUnits,
    int RequiredProjectClusters,
    bool InferentialReady,
    IReadOnlyList<string> Issues)
{
    public string DisplayStatus => InferentialReady
        ? "Sẵn sàng phân tích ghép cặp"
        : "Chỉ mô tả — chưa đủ điều kiện kết luận";
}

public sealed record Rq2PairwiseComparison(
    string Comparison,
    int PairedTasks,
    double TgslrTaskMeanSuccessRate,
    double BaselineTaskMeanSuccessRate,
    double SuccessRateDelta,
    int TaskWins,
    int TaskTies,
    int TaskLosses,
    int ProjectClusters,
    double? ClusterBootstrapLower,
    double? ClusterBootstrapUpper,
    double? ExactSignTestPValue,
    double? HolmAdjustedPValue,
    double? MeanTokenDelta,
    string Conclusion)
{
    public string SuccessRateDeltaDisplay => $"{SuccessRateDelta:+0.0%;-0.0%;0.0%}";
    public string ExactSignTestPValueDisplay => ExactSignTestPValue.HasValue
        ? ExactSignTestPValue.Value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
        : "N/A";
    public string HolmAdjustedPValueDisplay => HolmAdjustedPValue.HasValue
        ? HolmAdjustedPValue.Value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
        : "N/A";
    public string MeanTokenDeltaDisplay => MeanTokenDelta.HasValue
        ? $"{MeanTokenDelta.Value:+#,##0;-#,##0;0}"
        : "N/A";
    public string TaskWinTieLossDisplay => $"{TaskWins}/{TaskTies}/{TaskLosses}";
    public string ClusterBootstrapIntervalDisplay =>
        ClusterBootstrapLower.HasValue && ClusterBootstrapUpper.HasValue
            ? $"[{ClusterBootstrapLower.Value:+0.0%;-0.0%;0.0%}, {ClusterBootstrapUpper.Value:+0.0%;-0.0%;0.0%}]"
            : "N/A";
}

public sealed record Rq2StatisticalAnalysis(
    Rq2AnalysisReadiness Readiness,
    IReadOnlyList<Rq2PairwiseComparison> Comparisons,
    IReadOnlyList<Rq2OutcomeEffect> Outcomes,
    string OverallConclusion);

public sealed record Rq2OutcomeEffect(
    string Comparison,
    string Outcome,
    string Role,
    string Unit,
    int PairedUnits,
    int ProjectClusters,
    double TgslrMean,
    double BaselineMean,
    double MeanDelta,
    double? ClusterBootstrapLower,
    double? ClusterBootstrapUpper,
    double? NonInferiorityMargin,
    string Decision)
{
    public string TgslrMeanDisplay => Format(TgslrMean);
    public string BaselineMeanDisplay => Format(BaselineMean);
    public string MeanDeltaDisplay => $"{MeanDelta:+0.###;-0.###;0}";
    public string ClusterIntervalDisplay => ClusterBootstrapLower.HasValue && ClusterBootstrapUpper.HasValue
        ? $"[{ClusterBootstrapLower.Value:+0.###;-0.###;0}, {ClusterBootstrapUpper.Value:+0.###;-0.###;0}]"
        : "N/A";
    public string MarginDisplay => NonInferiorityMargin.HasValue
        ? NonInferiorityMargin.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
        : "N/A";

    private string Format(double value) => Unit == "proportion"
        ? value.ToString("P1", System.Globalization.CultureInfo.InvariantCulture)
        : value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}

public static class Rq2StatisticalAnalyzer
{
    public const int ClusterBootstrapReplicates = 10_000;
    public const int MinimumProjectClustersForInference = 5;
    public const double ClusterConfidenceLevel = 0.95;

    public static Rq2StatisticalAnalysis Analyze(IReadOnlyList<ExperimentRunResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (results.Count == 0)
        {
            return new Rq2StatisticalAnalysis(
                new Rq2AnalysisReadiness(false, false, false, false, false, false, false, false, false, false, 0, 0, 0,
                    MinimumProjectClustersForInference, false,
                    ["No experiment runs are available."]),
                [],
                [],
                "Chưa có dữ liệu để phân tích RQ2.");
        }

        var issues = new List<string>();
        var expectedRuns = results.Select(result => result.ExpectedBatchRuns).Distinct().ToArray();
        var batchComplete = expectedRuns.Length == 1 && expectedRuns[0] > 0 && expectedRuns[0] == results.Count;
        if (!batchComplete)
        {
            issues.Add("Batch is incomplete or expectedBatchRuns is inconsistent.");
        }

        var configurationComparable = HasOneValue(results, result => result.BatchId) &&
                                      HasOneValue(results, result => result.ProviderName) &&
                                      HasOneValue(results, result => result.ModelName) &&
                                      HasOneValue(results, result => result.ApiProtocol) &&
                                      results.Select(result => result.ConfiguredTokenBudget).Distinct().Count() == 1 &&
                                      results.Select(result => result.ConfiguredMaxIterations).Distinct().Count() == 1 &&
                                      results.Select(result => result.ScheduleSeed).Distinct().Count() == 1 &&
                                      results.Select(result => result.SourceSchemaVersion).Distinct().Count() == 1 &&
                                      HasOneValue(results, result => result.StudyPlan.StudyId) &&
                                      HasOneValue(results, result => result.StudyPlan.SamplingPlanSha256) &&
                                      HasOneValue(results, result => result.StudyPlan.PowerPlanSha256) &&
                                      HasOneValue(results, result => result.StudyPlan.OutcomePlanSha256);
        if (!configurationComparable)
        {
            issues.Add("Runs do not share one batch/provider/model/protocol/budget/stop rule/seed/schema/study plan.");
        }

        var strategies = new[]
        {
            RepairStrategy.TraceabilityGuidedScenarioLocal,
            RepairStrategy.FailingTestMethod,
            RepairStrategy.FullClassRegeneration
        };
        var blocks = results.GroupBy(result => new { result.TaskId, result.Repetition }).ToArray();
        var pairingComplete = blocks.All(block =>
            block.Count() == strategies.Length &&
            strategies.All(strategy => block.Count(result => result.Strategy == strategy) == 1));
        if (!pairingComplete)
        {
            issues.Add("At least one task–repetition block does not contain every treatment exactly once.");
        }

        var provenanceComplete = results.All(result =>
            !string.IsNullOrWhiteSpace(result.ProjectId) &&
            !string.IsNullOrWhiteSpace(result.DatasetId) &&
            !string.IsNullOrWhiteSpace(result.ArtifactId));
        if (!provenanceComplete)
        {
            issues.Add("Project/dataset/artifact provenance is missing; rerun with schema version 7.");
        }

        var treatmentIsolationComplete = results.All(result =>
            result.PromptAudit.ContractSatisfied &&
            !result.PromptAudit.InferredFromLegacy &&
            result.PromptAudit.ContractVersion == PromptAuditEvidence.CurrentContractVersion);
        if (!treatmentIsolationComplete)
        {
            issues.Add("Prompt treatment isolation is missing, legacy, or violated; rerun with schema version 9.");
        }

        var crossProjectUnit = results
            .GroupBy(result => result.IndependenceUnitId, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Select(result => result.ProjectId)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1);
        var samplingPlanComplete = !crossProjectUnit && results.All(result =>
            result.StudyPlan.SamplingPlanComplete &&
            !string.IsNullOrWhiteSpace(result.IndependenceUnitId) &&
            !string.IsNullOrWhiteSpace(result.SamplingStratum));
        if (!samplingPlanComplete)
        {
            issues.Add("Sampling evidence is incomplete; schema-10+ runs require an explicit independence unit/stratum, and one unit cannot span multiple projects.");
        }

        var taskCount = results.Select(result => result.IndependenceUnitId)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var projectCount = results.Select(result => result.ProjectId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var plan = results[0].StudyPlan;
        var powerPlanSatisfied = results.All(result =>
                                     result.StudyPlan.PowerPlanDeclared &&
                                     !result.StudyPlan.Provisional &&
                                     result.StudyPlan.RequiredIndependentUnits > 0 &&
                                     result.StudyPlan.PlannedIndependentUnits >= result.StudyPlan.RequiredIndependentUnits) &&
                                 taskCount >= plan.RequiredIndependentUnits &&
                                 projectCount >= plan.MinimumProjects;
        if (!powerPlanSatisfied)
        {
            issues.Add(
                $"Power plan is not satisfied: observed {taskCount}/{plan.RequiredIndependentUnits} independent unit(s), " +
                $"{projectCount}/{plan.MinimumProjects} project(s), planned={plan.PlannedIndependentUnits}, provisional={plan.Provisional}.");
        }

        var outcomePlanComplete = results.All(result =>
            result.StudyPlan.OutcomePlanDeclared &&
            !result.StudyPlan.OutcomePlanProvisional &&
            result.StudyPlan.OutcomeContractVersion == "RQ2-OUTCOME-PLAN-1" &&
            result.StudyPlan.SuccessNonInferiorityMargin > 0 &&
            result.StudyPlan.CoverageNonInferiorityMargin > 0 &&
            result.StudyPlan.MutationNonInferiorityMargin > 0);
        if (!outcomePlanComplete)
        {
            issues.Add("Outcome contract is missing, invalid, or provisional; schema-11 confirmatory runs must justify and freeze metric hierarchy/margins before execution.");
        }

        var clusterAnalysisReady = projectCount >= MinimumProjectClustersForInference;
        if (!clusterAnalysisReady)
        {
            issues.Add(
                $"Project-cluster inference requires at least {MinimumProjectClustersForInference} project clusters; " +
                $"the batch contains {projectCount}. Bootstrap intervals are N/A with fewer than two clusters and descriptive below the gate.");
        }

        var baseInferentialReady = batchComplete && configurationComparable && pairingComplete &&
                                   provenanceComplete && treatmentIsolationComplete &&
                                   samplingPlanComplete && powerPlanSatisfied && outcomePlanComplete && clusterAnalysisReady;
        var rawComparisons = new[]
        {
            Compare(results, RepairStrategy.FailingTestMethod, baseInferentialReady, results[0].ScheduleSeed, plan.RequiredIndependentUnits),
            Compare(results, RepairStrategy.FullClassRegeneration, baseInferentialReady, results[0].ScheduleSeed, plan.RequiredIndependentUnits)
        };
        var comparisonSamplesComplete = rawComparisons.All(comparison =>
            comparison.PairedTasks >= plan.RequiredIndependentUnits &&
            comparison.ProjectClusters >= MinimumProjectClustersForInference);
        if (!comparisonSamplesComplete)
        {
            issues.Add("At least one TGSLR-baseline comparison loses required independent units or project clusters after analysis-eligibility filtering.");
        }
        var inferentialReady = baseInferentialReady && comparisonSamplesComplete;
        var comparisons = ApplyHolmCorrection(rawComparisons, inferentialReady);
        var readiness = new Rq2AnalysisReadiness(
            batchComplete,
            configurationComparable,
            pairingComplete,
            provenanceComplete,
            treatmentIsolationComplete,
            samplingPlanComplete,
            powerPlanSatisfied,
            outcomePlanComplete,
            clusterAnalysisReady,
            comparisonSamplesComplete,
            taskCount,
            projectCount,
            plan.RequiredIndependentUnits,
            MinimumProjectClustersForInference,
            inferentialReady,
            issues);

        var outcomes = BuildOutcomeEffects(results, plan, inferentialReady);
        var conclusion = inferentialReady
            ? BuildInferentialConclusion(comparisons)
            : "Kết quả chỉ có giá trị mô tả. Không kết luận TGSLR tốt hơn baseline cho đến khi khắc phục các điều kiện analysis-ready.";
        return new Rq2StatisticalAnalysis(readiness, comparisons, outcomes, conclusion);
    }

    private static Rq2PairwiseComparison Compare(
        IReadOnlyList<ExperimentRunResult> results,
        RepairStrategy baseline,
        bool inferentialReady,
        int bootstrapSeed,
        int requiredIndependentUnits)
    {
        var taskPairs = results
            .GroupBy(result => result.IndependenceUnitId, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var tgslr = group.Where(result =>
                    result.Strategy == RepairStrategy.TraceabilityGuidedScenarioLocal &&
                    ExperimentOutcomePolicy.IsAnalysisEligible(result)).ToArray();
                var comparison = group.Where(result =>
                    result.Strategy == baseline && ExperimentOutcomePolicy.IsAnalysisEligible(result)).ToArray();
                if (tgslr.Length == 0 || comparison.Length == 0) return null;

                var tgslrRate = tgslr.Count(result => result.Status == RunStatus.Success) / (double)tgslr.Length;
                var baselineRate = comparison.Count(result => result.Status == RunStatus.Success) / (double)comparison.Length;
                var tgslrTokens = CompleteTokenMean(tgslr);
                var baselineTokens = CompleteTokenMean(comparison);
                return new TaskPair(
                    group.Select(result => result.ProjectId).First(),
                    tgslrRate,
                    baselineRate,
                    tgslrRate - baselineRate,
                    tgslrTokens.HasValue && baselineTokens.HasValue
                        ? tgslrTokens.Value - baselineTokens.Value
                        : null);
            })
            .Where(pair => pair is not null)
            .Cast<TaskPair>()
            .ToArray();

        var tgslrMean = taskPairs.Length == 0 ? 0 : taskPairs.Average(pair => pair.TgslrRate);
        var baselineMean = taskPairs.Length == 0 ? 0 : taskPairs.Average(pair => pair.BaselineRate);
        var delta = taskPairs.Length == 0 ? 0 : taskPairs.Average(pair => pair.SuccessDelta);
        var wins = taskPairs.Count(pair => pair.SuccessDelta > 1e-12);
        var losses = taskPairs.Count(pair => pair.SuccessDelta < -1e-12);
        var ties = taskPairs.Length - wins - losses;
        var projectClusters = taskPairs.Select(pair => pair.ProjectId)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var clusterInterval = projectClusters < 2
            ? null
            : ProjectClusterBootstrapInterval(taskPairs, bootstrapSeed, baseline);
        var tokenDeltas = taskPairs.Where(pair => pair.TokenDelta.HasValue)
            .Select(pair => pair.TokenDelta!.Value).ToArray();
        var comparisonReady = inferentialReady &&
                              taskPairs.Length >= requiredIndependentUnits &&
                              projectClusters >= MinimumProjectClustersForInference;
        double? pValue = comparisonReady ? ExactTwoSidedSignTest(wins, losses) : null;
        var baselineName = baseline.ToDisplayName();
        return new Rq2PairwiseComparison(
            $"TGSLR vs {baselineName}",
            taskPairs.Length,
            tgslrMean,
            baselineMean,
            delta,
            wins,
            ties,
            losses,
            projectClusters,
            clusterInterval?.Lower,
            clusterInterval?.Upper,
            pValue,
            null,
            tokenDeltas.Length == 0 ? null : tokenDeltas.Average(),
            comparisonReady
                ? "Đang áp dụng hiệu chỉnh Holm và project-cluster bootstrap CI."
                : projectClusters >= 2
                    ? "Mô tả; cluster CI chưa đủ số project để suy luận."
                    : "Mô tả; cần ít nhất hai project để ước lượng cluster CI.");
    }

    private static IReadOnlyList<Rq2PairwiseComparison> ApplyHolmCorrection(
        IReadOnlyList<Rq2PairwiseComparison> comparisons,
        bool inferentialReady)
    {
        if (!inferentialReady) return comparisons;

        var ordered = comparisons
            .Select((comparison, index) => new { comparison, index })
            .OrderBy(item => item.comparison.ExactSignTestPValue)
            .ToArray();
        var adjustedByIndex = new double[comparisons.Count];
        var previous = 0d;
        for (var rank = 0; rank < ordered.Length; rank++)
        {
            var raw = ordered[rank].comparison.ExactSignTestPValue!.Value;
            var adjusted = Math.Min(1, raw * (ordered.Length - rank));
            previous = Math.Max(previous, adjusted);
            adjustedByIndex[ordered[rank].index] = previous;
        }

        return comparisons.Select((comparison, index) =>
        {
            var adjusted = adjustedByIndex[index];
            var baseline = comparison.Comparison.Replace("TGSLR vs ", string.Empty, StringComparison.Ordinal);
            var intervalSupportsPositive = comparison.ClusterBootstrapLower > 0;
            var intervalSupportsNegative = comparison.ClusterBootstrapUpper < 0;
            var conclusion = adjusted <= 0.05 && intervalSupportsPositive
                ? $"TGSLR cao hơn {baseline}; Holm p={adjusted:0.####}, project-cluster CI {comparison.ClusterBootstrapIntervalDisplay}."
                : adjusted <= 0.05 && intervalSupportsNegative
                    ? $"TGSLR thấp hơn {baseline}; Holm p={adjusted:0.####}, project-cluster CI {comparison.ClusterBootstrapIntervalDisplay}."
                    : $"Chưa phát hiện khác biệt có ý nghĩa so với {baseline} (Holm-adjusted p={adjusted:0.####}).";
            return comparison with { HolmAdjustedPValue = adjusted, Conclusion = conclusion };
        }).ToArray();
    }

    private static IReadOnlyList<Rq2OutcomeEffect> BuildOutcomeEffects(
        IReadOnlyList<ExperimentRunResult> results,
        StudyPlanEvidence plan,
        bool inferentialReady)
    {
        var definitions = new[]
        {
            new OutcomeDefinition("Repair success", "Primary", "proportion", false,
                plan.OutcomePlanDeclared ? plan.SuccessNonInferiorityMargin : null,
                result => result.Status == RunStatus.Success ? 1 : 0),
            new OutcomeDefinition("Total tokens", "Efficiency", "tokens", true, null,
                result => result.TokenAccountingComplete ? result.TotalTokens : null),
            new OutcomeDefinition("API calls", "Efficiency", "calls", true, null,
                result => result.TotalApiCalls),
            new OutcomeDefinition("Repair iterations", "Efficiency", "iterations", true, null,
                result => result.Iterations.Count),
            new OutcomeDefinition("Changed LOC", "Efficiency", "lines", true, null,
                result => result.TotalChangedLines),
            new OutcomeDefinition("Touched methods", "Efficiency", "methods", true, null,
                result => result.TotalTouchedMethods),
            new OutcomeDefinition("Regressed tests", "Guardrail", "tests", true, null,
                result => result.RegressedTests),
            new OutcomeDefinition("Line coverage", "Guardrail", "proportion", false,
                plan.OutcomePlanDeclared ? plan.CoverageNonInferiorityMargin : null,
                result => result.Status == RunStatus.Success &&
                          result.FinalCoverageStatus == MeasurementStatus.Succeeded
                    ? result.FinalLineCoverage
                    : null),
            new OutcomeDefinition("Mutation score", "Guardrail", "proportion", false,
                plan.OutcomePlanDeclared ? plan.MutationNonInferiorityMargin : null,
                result => result.Status == RunStatus.Success &&
                          result.FinalMutationStatus == MeasurementStatus.Succeeded
                    ? result.FinalMutationScore
                    : null)
        };
        var baselines = new[]
        {
            RepairStrategy.FailingTestMethod,
            RepairStrategy.FullClassRegeneration
        };
        var effects = new List<Rq2OutcomeEffect>();
        foreach (var baseline in baselines)
        {
            for (var outcomeIndex = 0; outcomeIndex < definitions.Length; outcomeIndex++)
            {
                effects.Add(BuildOutcomeEffect(
                    results,
                    baseline,
                    definitions[outcomeIndex],
                    outcomeIndex,
                    inferentialReady));
            }
        }
        return effects;
    }

    private static Rq2OutcomeEffect BuildOutcomeEffect(
        IReadOnlyList<ExperimentRunResult> results,
        RepairStrategy baseline,
        OutcomeDefinition definition,
        int outcomeIndex,
        bool inferentialReady)
    {
        var pairs = results
            .GroupBy(result => result.IndependenceUnitId, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var tgslrValues = group
                    .Where(result => result.Strategy == RepairStrategy.TraceabilityGuidedScenarioLocal &&
                                     ExperimentOutcomePolicy.IsAnalysisEligible(result))
                    .Select(definition.Selector)
                    .Where(value => value.HasValue)
                    .Select(value => value!.Value)
                    .ToArray();
                var baselineValues = group
                    .Where(result => result.Strategy == baseline &&
                                     ExperimentOutcomePolicy.IsAnalysisEligible(result))
                    .Select(definition.Selector)
                    .Where(value => value.HasValue)
                    .Select(value => value!.Value)
                    .ToArray();
                if (tgslrValues.Length == 0 || baselineValues.Length == 0) return null;
                var tgslrMean = tgslrValues.Average();
                var baselineMean = baselineValues.Average();
                return new OutcomePair(
                    group.Select(result => result.ProjectId).First(),
                    tgslrMean,
                    baselineMean,
                    tgslrMean - baselineMean);
            })
            .Where(pair => pair is not null)
            .Cast<OutcomePair>()
            .ToArray();
        var projectClusters = pairs.Select(pair => pair.ProjectId)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var interval = projectClusters < 2
            ? null
            : ProjectClusterBootstrapInterval(
                pairs,
                results[0].ScheduleSeed,
                baseline,
                outcomeIndex);
        var tgslr = pairs.Length == 0 ? 0 : pairs.Average(pair => pair.TgslrValue);
        var comparison = pairs.Length == 0 ? 0 : pairs.Average(pair => pair.BaselineValue);
        var delta = pairs.Length == 0 ? 0 : pairs.Average(pair => pair.Delta);
        var decision = BuildOutcomeDecision(definition, interval, inferentialReady);
        return new Rq2OutcomeEffect(
            $"TGSLR vs {baseline.ToDisplayName()}",
            definition.Name,
            definition.Role,
            definition.Unit,
            pairs.Length,
            projectClusters,
            tgslr,
            comparison,
            delta,
            interval?.Lower,
            interval?.Upper,
            definition.NonInferiorityMargin,
            decision);
    }

    private static string BuildOutcomeDecision(
        OutcomeDefinition definition,
        BootstrapInterval? interval,
        bool inferentialReady)
    {
        if (interval is null) return "Mô tả; cần ít nhất hai project để ước lượng cluster CI.";
        if (!inferentialReady) return "Mô tả; chưa đạt analysis-ready.";
        if (definition.NonInferiorityMargin.HasValue)
        {
            var margin = definition.NonInferiorityMargin.Value;
            return interval.Lower >= -margin
                ? $"Duy trì chất lượng (CI lower ≥ -{margin:P1})."
                : interval.Upper < -margin
                    ? $"Không đạt non-inferiority margin {margin:P1}."
                    : $"Chưa đủ bằng chứng non-inferiority với margin {margin:P1}.";
        }

        if (definition.LowerIsBetter)
        {
            if (interval.Upper < 0) return "TGSLR thấp hơn (thuận lợi).";
            if (interval.Lower > 0) return "TGSLR cao hơn (bất lợi).";
        }
        else
        {
            if (interval.Lower > 0) return "TGSLR cao hơn (thuận lợi).";
            if (interval.Upper < 0) return "TGSLR thấp hơn (bất lợi).";
        }
        return "CI chứa 0; chưa rõ khác biệt.";
    }

    private static double? CompleteTokenMean(IReadOnlyList<ExperimentRunResult> results) =>
        results.All(result => result.TokenAccountingComplete)
            ? results.Average(result => result.TotalTokens)
            : null;

    private static double ExactTwoSidedSignTest(int wins, int losses)
    {
        var trials = wins + losses;
        if (trials == 0) return 1;
        var tail = Math.Min(wins, losses);
        var probability = 0d;
        for (var k = 0; k <= tail; k++)
        {
            probability += BinomialCoefficient(trials, k) * Math.Pow(0.5, trials);
        }
        return Math.Min(1, 2 * probability);
    }

    private static double BinomialCoefficient(int n, int k)
    {
        var result = 1d;
        for (var i = 1; i <= k; i++) result *= (n - (k - i)) / (double)i;
        return result;
    }

    private static BootstrapInterval ProjectClusterBootstrapInterval(
        IReadOnlyList<TaskPair> pairs,
        int scheduleSeed,
        RepairStrategy baseline)
    {
        var clusters = pairs
            .GroupBy(pair => pair.ProjectId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.ToArray())
            .ToArray();
        var random = new Random(unchecked(scheduleSeed * 397 + (int)baseline * 7919));
        var estimates = new double[ClusterBootstrapReplicates];
        for (var replicate = 0; replicate < estimates.Length; replicate++)
        {
            var sum = 0d;
            var count = 0;
            for (var draw = 0; draw < clusters.Length; draw++)
            {
                var selected = clusters[random.Next(clusters.Length)];
                sum += selected.Sum(pair => pair.SuccessDelta);
                count += selected.Length;
            }
            estimates[replicate] = count == 0 ? 0 : sum / count;
        }

        Array.Sort(estimates);
        var tail = (1 - ClusterConfidenceLevel) / 2;
        return new BootstrapInterval(
            Percentile(estimates, tail),
            Percentile(estimates, 1 - tail));
    }

    private static BootstrapInterval ProjectClusterBootstrapInterval(
        IReadOnlyList<OutcomePair> pairs,
        int scheduleSeed,
        RepairStrategy baseline,
        int outcomeIndex)
    {
        var clusters = pairs
            .GroupBy(pair => pair.ProjectId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.ToArray())
            .ToArray();
        var random = new Random(unchecked(
            scheduleSeed * 397 + (int)baseline * 7919 + (outcomeIndex + 1) * 104729));
        var estimates = new double[ClusterBootstrapReplicates];
        for (var replicate = 0; replicate < estimates.Length; replicate++)
        {
            var sum = 0d;
            var count = 0;
            for (var draw = 0; draw < clusters.Length; draw++)
            {
                var selected = clusters[random.Next(clusters.Length)];
                sum += selected.Sum(pair => pair.Delta);
                count += selected.Length;
            }
            estimates[replicate] = count == 0 ? 0 : sum / count;
        }
        Array.Sort(estimates);
        var tail = (1 - ClusterConfidenceLevel) / 2;
        return new BootstrapInterval(Percentile(estimates, tail), Percentile(estimates, 1 - tail));
    }

    private static double Percentile(IReadOnlyList<double> sorted, double probability)
    {
        var position = probability * (sorted.Count - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper) return sorted[lower];
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }

    private static bool HasOneValue(
        IReadOnlyList<ExperimentRunResult> results,
        Func<ExperimentRunResult, string> selector) =>
        results.Select(selector).Distinct(StringComparer.Ordinal).Count() == 1 &&
        !string.IsNullOrWhiteSpace(selector(results[0]));

    private static string BuildInferentialConclusion(IReadOnlyList<Rq2PairwiseComparison> comparisons)
    {
        var supported = comparisons.Where(comparison =>
            comparison.HolmAdjustedPValue <= 0.05 && comparison.ClusterBootstrapLower > 0).ToArray();
        return supported.Length == comparisons.Count
            ? "Dữ liệu hỗ trợ TGSLR có success rate cao hơn cả FTMR và FCR: paired sign test qua hiệu chỉnh Holm và project-cluster bootstrap CI đều loại trừ hiệu ứng bằng 0."
            : "Chưa có bằng chứng nhất quán rằng TGSLR cao hơn cả hai baseline ở mức α=0.05. Không diễn giải 'không có ý nghĩa' thành 'hai phương pháp tương đương'.";
    }

    private sealed record TaskPair(
        string ProjectId,
        double TgslrRate,
        double BaselineRate,
        double SuccessDelta,
        double? TokenDelta);

    private sealed record BootstrapInterval(double Lower, double Upper);

    private sealed record OutcomeDefinition(
        string Name,
        string Role,
        string Unit,
        bool LowerIsBetter,
        double? NonInferiorityMargin,
        Func<ExperimentRunResult, double?> Selector);

    private sealed record OutcomePair(
        string ProjectId,
        double TgslrValue,
        double BaselineValue,
        double Delta);
}
