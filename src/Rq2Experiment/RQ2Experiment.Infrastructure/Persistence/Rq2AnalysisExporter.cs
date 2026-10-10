using System.Globalization;
using System.Text;
using RQ2Experiment.Core.Domain;
using RQ2Experiment.Core.Engine;

namespace RQ2Experiment.Infrastructure.Persistence;

public sealed record Rq2AnalysisExportResult(
    string SummaryPath,
    string PairwisePath,
    string TaskLevelPath,
    string ProjectLevelPath,
    string OutcomesPath);

public static class Rq2AnalysisExporter
{
    public static async Task<Rq2AnalysisExportResult> ExportAsync(
        IReadOnlyList<ExperimentRunResult> results,
        Rq2StatisticalAnalysis analysis,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(analysis);
        if (results.Count == 0) throw new ArgumentException("Cannot export empty analysis.", nameof(results));

        var batchId = results.Select(result => result.BatchId).Distinct(StringComparer.Ordinal).Single();
        var directory = Path.Combine(Path.GetFullPath(outputDirectory), "analysis");
        Directory.CreateDirectory(directory);
        var safeBatch = Sanitize(batchId);
        var summaryPath = Path.Combine(directory, $"batch-{safeBatch}-rq2-summary.csv");
        var pairwisePath = Path.Combine(directory, $"batch-{safeBatch}-rq2-pairwise.csv");
        var taskLevelPath = Path.Combine(directory, $"batch-{safeBatch}-rq2-task-level.csv");
        var projectLevelPath = Path.Combine(directory, $"batch-{safeBatch}-rq2-project-level.csv");
        var outcomesPath = Path.Combine(directory, $"batch-{safeBatch}-rq2-outcomes.csv");

        await WriteSummaryAsync(
            summaryPath,
            analysis,
            string.Join("|", results.Select(result => result.SourceSchemaVersion).Distinct().Order()),
            results[0].StudyPlan,
            cancellationToken);
        await WritePairwiseAsync(pairwisePath, analysis, cancellationToken);
        await WriteTaskLevelAsync(taskLevelPath, results, cancellationToken);
        await WriteProjectLevelAsync(projectLevelPath, results, cancellationToken);
        await WriteOutcomesAsync(outcomesPath, analysis.Outcomes, cancellationToken);
        return new Rq2AnalysisExportResult(summaryPath, pairwisePath, taskLevelPath, projectLevelPath, outcomesPath);
    }

    private static async Task WriteSummaryAsync(
        string path,
        Rq2StatisticalAnalysis analysis,
        string sourceSchemaVersions,
        StudyPlanEvidence studyPlan,
        CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        await writer.WriteLineAsync("metric,value");
        var readiness = analysis.Readiness;
        var rows = new (string Metric, object? Value)[]
        {
            ("analysis_status", readiness.DisplayStatus),
            ("batch_complete", readiness.BatchComplete),
            ("configuration_comparable", readiness.ConfigurationComparable),
            ("pairing_complete", readiness.PairingComplete),
            ("provenance_complete", readiness.ProvenanceComplete),
            ("treatment_isolation_complete", readiness.TreatmentIsolationComplete),
            ("sampling_plan_complete", readiness.SamplingPlanComplete),
            ("power_plan_satisfied", readiness.PowerPlanSatisfied),
            ("outcome_plan_complete", readiness.OutcomePlanComplete),
            ("cluster_analysis_ready", readiness.ClusterAnalysisReady),
            ("comparison_samples_complete", readiness.ComparisonSamplesComplete),
            ("independent_units", readiness.IndependentTasks),
            ("required_independent_units", readiness.RequiredIndependentUnits),
            ("projects", readiness.Projects),
            ("required_project_clusters", readiness.RequiredProjectClusters),
            ("cluster_field", "project_id"),
            ("cluster_bootstrap_replicates", Rq2StatisticalAnalyzer.ClusterBootstrapReplicates),
            ("cluster_confidence_level", Rq2StatisticalAnalyzer.ClusterConfidenceLevel),
            ("outcome_contract_version", studyPlan.OutcomeContractVersion),
            ("outcome_plan_sha256", studyPlan.OutcomePlanSha256),
            ("outcome_plan_provisional", studyPlan.OutcomePlanProvisional),
            ("success_noninferiority_margin", studyPlan.SuccessNonInferiorityMargin),
            ("coverage_noninferiority_margin", studyPlan.CoverageNonInferiorityMargin),
            ("mutation_noninferiority_margin", studyPlan.MutationNonInferiorityMargin),
            ("inferential_ready", readiness.InferentialReady),
            ("source_schema_versions", sourceSchemaVersions),
            ("issues", string.Join(" | ", readiness.Issues)),
            ("overall_conclusion", analysis.OverallConclusion)
        };
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteLineAsync($"{Escape(row.Metric)},{Escape(Format(row.Value))}");
        }
    }

    private static async Task WritePairwiseAsync(
        string path,
        Rq2StatisticalAnalysis analysis,
        CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        await writer.WriteLineAsync(
            "comparison,paired_independent_units,tgslr_unit_mean_success_rate,baseline_unit_mean_success_rate," +
            "success_rate_delta,unit_wins,unit_ties,unit_losses,project_clusters," +
            "cluster_bootstrap_ci_lower,cluster_bootstrap_ci_upper,raw_sign_test_p,holm_adjusted_p," +
            "mean_token_delta,conclusion");
        foreach (var comparison in analysis.Comparisons)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = new object?[]
            {
                comparison.Comparison,
                comparison.PairedTasks,
                comparison.TgslrTaskMeanSuccessRate,
                comparison.BaselineTaskMeanSuccessRate,
                comparison.SuccessRateDelta,
                comparison.TaskWins,
                comparison.TaskTies,
                comparison.TaskLosses,
                comparison.ProjectClusters,
                comparison.ClusterBootstrapLower,
                comparison.ClusterBootstrapUpper,
                comparison.ExactSignTestPValue,
                comparison.HolmAdjustedPValue,
                comparison.MeanTokenDelta,
                comparison.Conclusion
            };
            await writer.WriteLineAsync(string.Join(',', values.Select(value => Escape(Format(value)))));
        }
    }

    private static async Task WriteTaskLevelAsync(
        string path,
        IReadOnlyList<ExperimentRunResult> results,
        CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        await writer.WriteLineAsync(
            "source_schema_version,study_id,sampling_plan_sha256,power_plan_sha256,project_id,dataset_id,independence_unit_id," +
            "sampling_stratum,artifact_ids,task_ids,treatment,total_runs,eligible_runs,successes," +
            "unit_success_rate,token_accounted_runs,mean_tokens,infrastructure_failures");
        var rows = results
            .GroupBy(result => new
            {
                result.ProjectId,
                result.DatasetId,
                result.IndependenceUnitId,
                result.SamplingStratum,
                result.Strategy
            })
            .OrderBy(group => group.Key.ProjectId, StringComparer.Ordinal)
            .ThenBy(group => group.Key.IndependenceUnitId, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Strategy);
        foreach (var group in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var eligible = group.Where(ExperimentOutcomePolicy.IsAnalysisEligible).ToArray();
            var accounted = eligible.Where(result => result.TokenAccountingComplete).ToArray();
            var values = new object?[]
            {
                string.Join("|", group.Select(result => result.SourceSchemaVersion).Distinct().Order()),
                string.Join("|", group.Select(result => result.StudyPlan.StudyId).Distinct(StringComparer.Ordinal)),
                string.Join("|", group.Select(result => result.StudyPlan.SamplingPlanSha256).Distinct(StringComparer.Ordinal)),
                string.Join("|", group.Select(result => result.StudyPlan.PowerPlanSha256).Distinct(StringComparer.Ordinal)),
                group.Key.ProjectId,
                group.Key.DatasetId,
                group.Key.IndependenceUnitId,
                group.Key.SamplingStratum,
                string.Join("|", group.Select(result => result.ArtifactId).Distinct(StringComparer.OrdinalIgnoreCase)),
                string.Join("|", group.Select(result => result.TaskId).Distinct(StringComparer.OrdinalIgnoreCase)),
                group.Key.Strategy.ToDisplayName(),
                group.Count(),
                eligible.Length,
                eligible.Count(result => result.Status == RunStatus.Success),
                eligible.Length == 0 ? null : eligible.Count(result => result.Status == RunStatus.Success) / (double)eligible.Length,
                accounted.Length,
                accounted.Length == 0 ? null : accounted.Average(result => result.TotalTokens),
                group.Count(ExperimentOutcomePolicy.IsInfrastructureFailure)
            };
            await writer.WriteLineAsync(string.Join(',', values.Select(value => Escape(Format(value)))));
        }
    }

    private static async Task WriteProjectLevelAsync(
        string path,
        IReadOnlyList<ExperimentRunResult> results,
        CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        await writer.WriteLineAsync(
            "source_schema_version,study_id,project_id,treatment,independent_units,total_runs,eligible_runs,successes," +
            "project_unit_mean_success_rate,token_accounted_runs,mean_tokens,infrastructure_failures");
        var groups = results
            .GroupBy(result => new { result.ProjectId, result.Strategy })
            .OrderBy(group => group.Key.ProjectId, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Strategy);
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var eligible = group.Where(ExperimentOutcomePolicy.IsAnalysisEligible).ToArray();
            var unitRates = eligible
                .GroupBy(result => result.IndependenceUnitId, StringComparer.OrdinalIgnoreCase)
                .Select(unit => unit.Count(result => result.Status == RunStatus.Success) / (double)unit.Count())
                .ToArray();
            var accounted = eligible.Where(result => result.TokenAccountingComplete).ToArray();
            var values = new object?[]
            {
                string.Join("|", group.Select(result => result.SourceSchemaVersion).Distinct().Order()),
                string.Join("|", group.Select(result => result.StudyPlan.StudyId).Distinct(StringComparer.Ordinal)),
                group.Key.ProjectId,
                group.Key.Strategy.ToDisplayName(),
                group.Select(result => result.IndependenceUnitId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                group.Count(),
                eligible.Length,
                eligible.Count(result => result.Status == RunStatus.Success),
                unitRates.Length == 0 ? null : unitRates.Average(),
                accounted.Length,
                accounted.Length == 0 ? null : accounted.Average(result => result.TotalTokens),
                group.Count(ExperimentOutcomePolicy.IsInfrastructureFailure)
            };
            await writer.WriteLineAsync(string.Join(',', values.Select(value => Escape(Format(value)))));
        }
    }

    private static async Task WriteOutcomesAsync(
        string path,
        IReadOnlyList<Rq2OutcomeEffect> outcomes,
        CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        await writer.WriteLineAsync(
            "comparison,outcome,role,unit,paired_independent_units,project_clusters,tgslr_mean,baseline_mean," +
            "mean_delta,cluster_bootstrap_ci_lower,cluster_bootstrap_ci_upper,noninferiority_margin,decision");
        foreach (var outcome in outcomes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = new object?[]
            {
                outcome.Comparison,
                outcome.Outcome,
                outcome.Role,
                outcome.Unit,
                outcome.PairedUnits,
                outcome.ProjectClusters,
                outcome.TgslrMean,
                outcome.BaselineMean,
                outcome.MeanDelta,
                outcome.ClusterBootstrapLower,
                outcome.ClusterBootstrapUpper,
                outcome.NonInferiorityMargin,
                outcome.Decision
            };
            await writer.WriteLineAsync(string.Join(',', values.Select(value => Escape(Format(value)))));
        }
    }

    private static string Format(object? value) => value switch
    {
        null => string.Empty,
        bool flag => flag ? "true" : "false",
        double number => number.ToString("0.###############", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    private static string Escape(string value) => value.IndexOfAny([',', '"', '\r', '\n']) < 0
        ? value
        : $"\"{value.Replace("\"", "\"\"")}\"";

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) ? '-' : character).ToArray());
    }
}
