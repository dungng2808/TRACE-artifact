using System.Globalization;
using System.Text;
using RQ2Experiment.Core.Domain;

namespace RQ2Experiment.Infrastructure.Persistence;

public sealed record CsvExportResult(string RunsPath, string IterationsPath);

public static class CsvExperimentExporter
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static async Task<CsvExportResult> ExportAsync(
        IReadOnlyList<ExperimentRunResult> results,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (results.Count == 0) throw new ArgumentException("Cannot export an empty result set.", nameof(results));

        var batchIds = results.Select(result => result.BatchId).Distinct(StringComparer.Ordinal).ToArray();
        if (batchIds.Length != 1 || string.IsNullOrWhiteSpace(batchIds[0]))
        {
            throw new ArgumentException("CSV export requires exactly one non-legacy batch.", nameof(results));
        }

        var analysisDirectory = Path.Combine(Path.GetFullPath(outputDirectory), "analysis");
        Directory.CreateDirectory(analysisDirectory);
        var safeBatchId = SanitizeFileName(batchIds[0]);
        var runsPath = Path.Combine(analysisDirectory, $"batch-{safeBatchId}-runs.csv");
        var iterationsPath = Path.Combine(analysisDirectory, $"batch-{safeBatchId}-iterations.csv");

        await WriteRunsAsync(runsPath, results, cancellationToken);
        await WriteIterationsAsync(iterationsPath, results, cancellationToken);
        return new CsvExportResult(runsPath, iterationsPath);
    }

    private static async Task WriteRunsAsync(
        string path,
        IReadOnlyList<ExperimentRunResult> results,
        CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        await writer.WriteLineAsync(
            "schema_version,source_schema_version,batch_id,schedule_seed,schedule_index,block_id,treatment_position,expected_batch_runs," +
            "run_id,project_id,dataset_id,artifact_id,independence_unit_id,sampling_stratum,task_id,scenario_id,strategy,repetition,status,failure_category," +
            "study_id,sampling_plan_sha256,power_plan_sha256,selection_seed,planned_independent_units,required_independent_units," +
            "required_discordant_units,minimum_projects,family_wise_alpha,planning_alpha,target_power,achieved_power," +
            "alternative_win_probability,expected_discordant_unit_rate,power_plan_provisional,sampling_plan_complete,power_plan_declared," +
            "outcome_plan_sha256,outcome_contract_version,success_noninferiority_margin,coverage_noninferiority_margin," +
            "mutation_noninferiority_margin,outcome_plan_declared,outcome_plan_provisional," +
            "failed_stage,stage_evidence_inferred,llm_output_stage,source_application_stage,compilation_stage," +
            "focal_test_stage,buggy_oracle_stage,regression_stage,coverage_stage,mutation_stage," +
            "prompt_contract_version,prompt_contract_satisfied,prompt_audit_inferred,context_policy,edit_scope," +
            "scenario_included,traceability_included,focal_method_included,full_test_class_included,forbidden_context_detected," +
            "prompt_sha256,instructions_sha256,prompt_audit_artifact,prompt_contract_violation,started_at_utc,completed_at_utc," +
            "duration_seconds,total_input_tokens,total_output_tokens,total_tokens,total_cached_input_tokens," +
            "total_cache_write_input_tokens,total_reasoning_tokens,token_accounting_complete,unreported_usage_responses," +
            "total_api_calls,total_api_retries,total_key_failovers,last_api_key_fingerprint," +
            "total_changed_lines,regressed_tests,buggy_oracle_required,final_bug_reproduced,coverage_status," +
            "coverage_detail,coverage_report_path,final_line_coverage,mutation_status,mutation_detail,mutation_report_path," +
            "final_mutation_score,provider,model,api_protocol,configured_token_budget," +
            "configured_max_iterations,iteration_count,stop_reason");

        foreach (var result in results.OrderBy(result => result.ScheduleIndex))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(Row(
                result.SchemaVersion,
                result.SourceSchemaVersion,
                result.BatchId,
                result.ScheduleSeed,
                result.ScheduleIndex,
                result.BlockId,
                result.TreatmentPosition,
                result.ExpectedBatchRuns,
                result.RunId,
                result.ProjectId,
                result.DatasetId,
                result.ArtifactId,
                result.IndependenceUnitId,
                result.SamplingStratum,
                result.TaskId,
                result.ScenarioId,
                result.Strategy.ToDisplayName(),
                result.Repetition,
                result.Status,
                result.FailureCategory,
                result.StudyPlan.StudyId,
                result.StudyPlan.SamplingPlanSha256,
                result.StudyPlan.PowerPlanSha256,
                result.StudyPlan.SelectionSeed,
                result.StudyPlan.PlannedIndependentUnits,
                result.StudyPlan.RequiredIndependentUnits,
                result.StudyPlan.RequiredDiscordantUnits,
                result.StudyPlan.MinimumProjects,
                result.StudyPlan.FamilyWiseAlpha,
                result.StudyPlan.PlanningAlpha,
                result.StudyPlan.TargetPower,
                result.StudyPlan.AchievedPower,
                result.StudyPlan.AlternativeWinProbability,
                result.StudyPlan.ExpectedDiscordantUnitRate,
                result.StudyPlan.Provisional,
                result.StudyPlan.SamplingPlanComplete,
                result.StudyPlan.PowerPlanDeclared,
                result.StudyPlan.OutcomePlanSha256,
                result.StudyPlan.OutcomeContractVersion,
                result.StudyPlan.SuccessNonInferiorityMargin,
                result.StudyPlan.CoverageNonInferiorityMargin,
                result.StudyPlan.MutationNonInferiorityMargin,
                result.StudyPlan.OutcomePlanDeclared,
                result.StudyPlan.OutcomePlanProvisional,
                result.Stages.FailedStage,
                result.Stages.InferredFromLegacy,
                result.Stages.LlmOutput.Status,
                result.Stages.SourceApplication.Status,
                result.Stages.Compilation.Status,
                result.Stages.FocalTest.Status,
                result.Stages.BuggyOracle.Status,
                result.Stages.Regression.Status,
                result.Stages.Coverage.Status,
                result.Stages.Mutation.Status,
                result.PromptAudit.ContractVersion,
                result.PromptAudit.ContractSatisfied,
                result.PromptAudit.InferredFromLegacy,
                result.PromptAudit.ContextPolicy,
                result.PromptAudit.EditScope,
                result.PromptAudit.ScenarioIncluded,
                result.PromptAudit.TraceabilityIncluded,
                result.PromptAudit.FocalMethodIncluded,
                result.PromptAudit.FullTestClassIncluded,
                result.PromptAudit.ForbiddenContextDetected,
                result.PromptAudit.PromptSha256,
                result.PromptAudit.InstructionsSha256,
                result.PromptAudit.AuditArtifact,
                result.PromptAudit.Violation,
                result.StartedAt,
                result.CompletedAt,
                (result.CompletedAt - result.StartedAt).TotalSeconds,
                result.TotalInputTokens,
                result.TotalOutputTokens,
                result.TotalTokens,
                result.TotalCachedInputTokens,
                result.TotalCacheWriteInputTokens,
                result.TotalReasoningTokens,
                result.TokenAccountingComplete,
                result.UnreportedUsageResponses,
                result.TotalApiCalls,
                result.TotalApiRetries,
                result.TotalKeyFailovers,
                result.LastApiKeyFingerprint,
                result.TotalChangedLines,
                result.RegressedTests,
                result.BuggyOracleRequired,
                result.FinalBugReproduced,
                result.FinalCoverageStatus,
                result.FinalCoverageDetail,
                result.FinalCoverageReportPath,
                result.FinalLineCoverage,
                result.FinalMutationStatus,
                result.FinalMutationDetail,
                result.FinalMutationReportPath,
                result.FinalMutationScore,
                result.ProviderName,
                result.ModelName,
                result.ApiProtocol,
                result.ConfiguredTokenBudget,
                result.ConfiguredMaxIterations,
                result.Iterations.Count,
                result.StopReason));
        }
    }

    private static async Task WriteIterationsAsync(
        string path,
        IReadOnlyList<ExperimentRunResult> results,
        CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        await writer.WriteLineAsync(
            "schema_version,source_schema_version,batch_id,schedule_seed,schedule_index,block_id,treatment_position,run_id,project_id,dataset_id,artifact_id," +
            "independence_unit_id,sampling_stratum,study_id,sampling_plan_sha256,power_plan_sha256,outcome_plan_sha256,outcome_contract_version,task_id," +
            "scenario_id,strategy,repetition,iteration,recorded_at_utc,failed_stage,stage_evidence_inferred," +
            "llm_output_stage,source_application_stage,compilation_stage,focal_test_stage,buggy_oracle_stage," +
            "regression_stage,coverage_stage,mutation_stage,prompt_contract_version,prompt_contract_satisfied," +
            "prompt_audit_inferred,context_policy,edit_scope,scenario_included,traceability_included,focal_method_included," +
            "full_test_class_included,forbidden_context_detected,prompt_sha256,instructions_sha256,prompt_audit_artifact," +
            "prompt_contract_violation,input_tokens,output_tokens,total_tokens," +
            "cached_input_tokens,cache_write_input_tokens,reasoning_tokens,api_calls,api_retries,key_failovers," +
            "api_key_fingerprint,token_usage_reported,first_pass_json_valid,output_normalization,changed_lines,touched_methods,regressed_tests,focal_test_passed,buggy_oracle_required," +
            "bug_reproduced,adequacy_verified,coverage_status,coverage_detail,coverage_report_path,line_coverage," +
            "mutation_status,mutation_detail,mutation_report_path,mutation_score,summary");

        foreach (var result in results.OrderBy(result => result.ScheduleIndex))
        {
            foreach (var iteration in result.Iterations.OrderBy(item => item.Iteration))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var value = iteration.Result;
                await writer.WriteLineAsync(Row(
                    result.SchemaVersion,
                    result.SourceSchemaVersion,
                    result.BatchId,
                    result.ScheduleSeed,
                    result.ScheduleIndex,
                    result.BlockId,
                    result.TreatmentPosition,
                    result.RunId,
                    result.ProjectId,
                    result.DatasetId,
                    result.ArtifactId,
                    result.IndependenceUnitId,
                    result.SamplingStratum,
                    result.StudyPlan.StudyId,
                    result.StudyPlan.SamplingPlanSha256,
                    result.StudyPlan.PowerPlanSha256,
                    result.StudyPlan.OutcomePlanSha256,
                    result.StudyPlan.OutcomeContractVersion,
                    result.TaskId,
                    result.ScenarioId,
                    result.Strategy.ToDisplayName(),
                    result.Repetition,
                    iteration.Iteration,
                    iteration.StartedAt,
                    value.Stages.FailedStage,
                    value.Stages.InferredFromLegacy,
                    value.Stages.LlmOutput.Status,
                    value.Stages.SourceApplication.Status,
                    value.Stages.Compilation.Status,
                    value.Stages.FocalTest.Status,
                    value.Stages.BuggyOracle.Status,
                    value.Stages.Regression.Status,
                    value.Stages.Coverage.Status,
                    value.Stages.Mutation.Status,
                    value.PromptAudit.ContractVersion,
                    value.PromptAudit.ContractSatisfied,
                    value.PromptAudit.InferredFromLegacy,
                    value.PromptAudit.ContextPolicy,
                    value.PromptAudit.EditScope,
                    value.PromptAudit.ScenarioIncluded,
                    value.PromptAudit.TraceabilityIncluded,
                    value.PromptAudit.FocalMethodIncluded,
                    value.PromptAudit.FullTestClassIncluded,
                    value.PromptAudit.ForbiddenContextDetected,
                    value.PromptAudit.PromptSha256,
                    value.PromptAudit.InstructionsSha256,
                    value.PromptAudit.AuditArtifact,
                    value.PromptAudit.Violation,
                    value.InputTokens,
                    value.OutputTokens,
                    value.TotalTokens,
                    value.CachedInputTokens,
                    value.CacheWriteInputTokens,
                    value.ReasoningTokens,
                    value.ApiCalls,
                    value.ApiRetryCount,
                    value.KeyFailoverCount,
                    value.ApiKeyFingerprint,
                    value.TokenUsageReported,
                    value.FirstPassJsonValid,
                    value.OutputNormalization,
                    value.ChangedLines,
                    value.TouchedMethods,
                    value.RegressedTests,
                    value.FocalTestPassed,
                    value.BuggyOracleRequired,
                    value.BugReproduced,
                    value.AdequacyVerified,
                    value.CoverageStatus,
                    value.CoverageDetail,
                    value.CoverageReportPath,
                    value.LineCoverage,
                    value.MutationStatus,
                    value.MutationDetail,
                    value.MutationReportPath,
                    value.MutationScore,
                    value.Summary));
            }
        }
    }

    private static string Row(params object?[] values) => string.Join(',', values.Select(FormatValue));

    private static string FormatValue(object? value)
    {
        var text = value switch
        {
            null => string.Empty,
            DateTimeOffset dateTime => dateTime.ToUniversalTime().ToString("O", Invariant),
            double number => number.ToString("0.###############", Invariant),
            float number => number.ToString("0.###############", Invariant),
            decimal number => number.ToString(Invariant),
            bool boolean => boolean ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, Invariant),
            _ => value.ToString() ?? string.Empty
        };

        return text.IndexOfAny([',', '"', '\r', '\n']) < 0
            ? text
            : $"\"{text.Replace("\"", "\"\"")}\"";
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(character =>
            invalid.Contains(character) || char.IsWhiteSpace(character) ? '-' : character).ToArray());
        return sanitized.Trim('-');
    }
}
