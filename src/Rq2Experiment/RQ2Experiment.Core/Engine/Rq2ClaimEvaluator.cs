using RQ2Experiment.Core.Domain;

namespace RQ2Experiment.Core.Engine;

public sealed record Rq2ClaimCriterion(string Name, bool Passed, string Evidence);

public sealed record Rq2PilotPowerProjection(
    string Comparison,
    int PilotPairedUnits,
    int Wins,
    int Losses,
    double DiscordantRate,
    double ConditionalWinProbability,
    int? ProjectedRequiredIndependentUnits,
    double? ProjectedPowerAt100)
{
    public string RequiredDisplay => ProjectedRequiredIndependentUnits?.ToString() ?? "N/A";
    public string PowerAt100Display => ProjectedPowerAt100.HasValue ? $"{ProjectedPowerAt100.Value:P1}" : "N/A";
}

public sealed record Rq2ClaimAssessment(
    bool CanClaimSuperiority,
    string Status,
    string Explanation,
    IReadOnlyList<Rq2ClaimCriterion> Criteria,
    IReadOnlyList<Rq2PilotPowerProjection> PilotPowerProjections);

public static class Rq2ClaimEvaluator
{
    public static Rq2ClaimAssessment Evaluate(
        IReadOnlyList<ExperimentRunResult> results,
        Rq2StatisticalAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(analysis);
        var criteria = new List<Rq2ClaimCriterion>
        {
            new("Analysis-ready", analysis.Readiness.InferentialReady,
                analysis.Readiness.InferentialReady
                    ? "Sampling, protocol, pairing, provenance, and project-cluster gates passed."
                    : string.Join(" | ", analysis.Readiness.Issues))
        };

        foreach (var comparison in analysis.Comparisons)
        {
            var passed = comparison.HolmAdjustedPValue <= 0.05 &&
                         comparison.ClusterBootstrapLower > 0;
            criteria.Add(new Rq2ClaimCriterion(
                $"Primary superiority: {comparison.Comparison}",
                passed,
                $"delta={comparison.SuccessRateDeltaDisplay}; Holm p={comparison.HolmAdjustedPValueDisplay}; cluster CI={comparison.ClusterBootstrapIntervalDisplay}."));

            foreach (var outcomeName in new[] { "Line coverage", "Mutation score" })
            {
                var outcome = analysis.Outcomes.FirstOrDefault(item =>
                    item.Comparison == comparison.Comparison && item.Outcome == outcomeName);
                var guardrailPassed = outcome is not null &&
                                      outcome.PairedUnits >= analysis.Readiness.RequiredIndependentUnits &&
                                      outcome.ProjectClusters >= analysis.Readiness.RequiredProjectClusters &&
                                      outcome.NonInferiorityMargin.HasValue &&
                                      outcome.ClusterBootstrapLower >= -outcome.NonInferiorityMargin.Value;
                criteria.Add(new Rq2ClaimCriterion(
                    $"Guardrail {outcomeName}: {comparison.Comparison}",
                    guardrailPassed,
                    outcome is null
                        ? "No paired measurement evidence."
                        : $"paired={outcome.PairedUnits}; projects={outcome.ProjectClusters}; CI={outcome.ClusterIntervalDisplay}; NI margin={outcome.MarginDisplay}."));
            }

            var regression = analysis.Outcomes.FirstOrDefault(item =>
                item.Comparison == comparison.Comparison && item.Outcome == "Regressed tests");
            var regressionPassed = regression is not null &&
                                   regression.PairedUnits >= analysis.Readiness.RequiredIndependentUnits &&
                                   regression.ProjectClusters >= analysis.Readiness.RequiredProjectClusters &&
                                   regression.ClusterBootstrapUpper <= 0;
            criteria.Add(new Rq2ClaimCriterion(
                $"Guardrail regressions: {comparison.Comparison}",
                regressionPassed,
                regression is null
                    ? "No paired regression evidence."
                    : $"paired={regression.PairedUnits}; projects={regression.ProjectClusters}; CI={regression.ClusterIntervalDisplay}."));
        }

        var canClaim = criteria.All(item => item.Passed);
        var projections = analysis.Comparisons.Select(ProjectPower).ToArray();
        return new Rq2ClaimAssessment(
            canClaim,
            canClaim ? "Đủ bằng chứng để kết luận TGSLR tốt hơn cả FTMR và FCR" : "Chưa đủ bằng chứng để kết luận superiority",
            canClaim
                ? "Hai primary comparisons vượt Holm-corrected paired test, cluster CI nằm trên 0, và mọi guardrail đạt yêu cầu."
                : "Ít nhất một điều kiện về power/protocol, primary superiority hoặc quality guardrail chưa đạt.",
            criteria,
            projections);
    }

    private static Rq2PilotPowerProjection ProjectPower(Rq2PairwiseComparison comparison)
    {
        var discordant = comparison.TaskWins + comparison.TaskLosses;
        if (comparison.PairedTasks == 0 || discordant == 0 || comparison.TaskWins <= comparison.TaskLosses)
            return new Rq2PilotPowerProjection(comparison.Comparison, comparison.PairedTasks,
                comparison.TaskWins, comparison.TaskLosses,
                comparison.PairedTasks == 0 ? 0 : discordant / (double)comparison.PairedTasks,
                discordant == 0 ? 0.5 : comparison.TaskWins / (double)discordant,
                null, null);

        var discordantRate = discordant / (double)comparison.PairedTasks;
        var conditionalWinProbability = comparison.TaskWins / (double)discordant;
        // Exact planning requires p > .5. Avoid unstable boundary values from tiny pilots.
        conditionalWinProbability = Math.Clamp(conditionalWinProbability, 0.5001, 0.95);
        var specification = new PowerPlanSpecification(
            FamilyWiseAlpha: 0.05,
            TargetPower: 0.80,
            AlternativeWinProbability: conditionalWinProbability,
            ExpectedDiscordantUnitRate: discordantRate,
            ComparisonCount: 2,
            MinimumProjects: 5,
            Provisional: true);
        try
        {
            var required = StudyDesignPlanner.Calculate(specification).RequiredIndependentUnits;
            var powerAt100 = StudyDesignPlanner.EstimateAchievedPower(specification, 100);
            return new Rq2PilotPowerProjection(
                comparison.Comparison, comparison.PairedTasks, comparison.TaskWins, comparison.TaskLosses,
                discordantRate, conditionalWinProbability, required, powerAt100);
        }
        catch (InvalidOperationException)
        {
            return new Rq2PilotPowerProjection(
                comparison.Comparison, comparison.PairedTasks, comparison.TaskWins, comparison.TaskLosses,
                discordantRate, conditionalWinProbability, null, null);
        }
    }
}
