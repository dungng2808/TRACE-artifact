using System.Text.Json;
using RQ2Experiment.Core.Domain;

namespace RQ2Experiment.Infrastructure.Persistence;

public static class ExperimentRunResultSchemaMigrator
{
    public static ExperimentRunResult Deserialize(string json, JsonSerializerOptions options)
    {
        using var document = JsonDocument.Parse(json);
        var schemaVersion = document.RootElement.TryGetProperty("schemaVersion", out var schema)
            ? schema.GetInt32()
            : 0;
        if (schemaVersion >= 11)
        {
            return JsonSerializer.Deserialize<ExperimentRunResult>(json, options)
                ?? throw new JsonException("Current result schema deserialized to null.");
        }

        if (schemaVersion is 7 or 8 or 9 or 10)
        {
            var grouped = JsonSerializer.Deserialize<ExperimentRunResult>(json, options)
                ?? throw new JsonException($"Schema-{schemaVersion} result deserialized to null.");
            return UpgradeGrouped(grouped, schemaVersion);
        }

        var legacy = JsonSerializer.Deserialize<LegacyRun>(json, options)
            ?? throw new JsonException("Legacy result deserialized to null.");
        return Migrate(legacy);
    }

    private static ExperimentRunResult Migrate(LegacyRun legacy)
    {
        var iterations = legacy.Iterations.Select(iteration => new IterationRecord(
            iteration.Iteration,
            iteration.StartedAt,
            Migrate(iteration.Result, legacy.SchemaVersion))).ToArray();
        return new ExperimentRunResult
        {
            SchemaVersion = 11,
            Identity = new RunIdentity(
                legacy.RunId,
                legacy.TaskId,
                legacy.ScenarioId,
                legacy.Strategy,
                legacy.Repetition),
            Provenance = new RunProvenance(
                legacy.BatchId,
                legacy.ProviderName,
                legacy.ModelName,
                legacy.ApiProtocol,
                legacy.ConfiguredTokenBudget,
                legacy.ConfiguredMaxIterations,
                legacy.ProjectId,
                legacy.DatasetId,
                legacy.ArtifactId,
                legacy.SchemaVersion),
            Design = new ExperimentDesignMetadata(
                legacy.ScheduleSeed,
                legacy.ScheduleIndex,
                legacy.BlockId,
                legacy.TreatmentPosition,
                legacy.ExpectedBatchRuns),
            Timing = new ExecutionTiming(legacy.StartedAt, legacy.CompletedAt),
            Tokens = new TokenUsage(
                legacy.TotalInputTokens,
                legacy.TotalOutputTokens,
                legacy.TotalCachedInputTokens,
                legacy.TotalCacheWriteInputTokens,
                legacy.TotalReasoningTokens,
                legacy.UnreportedUsageResponses,
                legacy.TokenAccountingComplete,
                LegacyTotalTokens: PreserveLegacyTotal(
                    legacy.TotalTokens,
                    legacy.TotalInputTokens,
                    legacy.TotalOutputTokens)),
            Api = new ApiUsage(
                legacy.TotalApiCalls,
                legacy.TotalApiRetries,
                legacy.TotalKeyFailovers,
                legacy.LastApiKeyFingerprint),
            Verification = new RepairVerification(
                legacy.TotalChangedLines,
                iterations.LastOrDefault()?.Result.TouchedMethods ?? 0,
                legacy.RegressedTests,
                iterations.LastOrDefault()?.Result.FocalTestPassed ?? legacy.Status == RunStatus.Success,
                iterations.LastOrDefault()?.Result.AdequacyVerified ?? false,
                legacy.BuggyOracleRequired,
                legacy.FinalBugReproduced),
            Quality = new QualityMeasurement(
                legacy.FinalLineCoverage,
                legacy.FinalMutationScore,
                legacy.FinalCoverageStatus,
                legacy.FinalMutationStatus,
                legacy.FinalCoverageDetail,
                legacy.FinalMutationDetail,
                legacy.FinalCoverageReportPath,
                legacy.FinalMutationReportPath),
            Status = legacy.Status,
            FailureCategory = legacy.FailureCategory,
            Iterations = iterations,
            StopReason = legacy.StopReason,
            Stages = iterations.LastOrDefault()?.Result.Stages ?? InferLegacyRunFailure(legacy),
            PromptAudit = iterations.LastOrDefault()?.Result.PromptAudit ?? LegacyPromptAudit(legacy.SchemaVersion)
        };
    }

    private static ExperimentRunResult UpgradeGrouped(ExperimentRunResult grouped, int sourceSchemaVersion)
    {
        var iterations = grouped.Iterations
            .Select(iteration => iteration with
            {
                Result = iteration.Result with
                {
                    Stages = HasStageEvidence(iteration.Result.Stages)
                        ? iteration.Result.Stages
                        : InferGroupedStages(iteration.Result),
                    PromptAudit = HasPromptAudit(iteration.Result.PromptAudit)
                        ? iteration.Result.PromptAudit
                        : LegacyPromptAudit(sourceSchemaVersion)
                }
            })
            .ToArray();
        var runStages = HasStageEvidence(grouped.Stages)
            ? grouped.Stages
            : iterations.LastOrDefault()?.Result.Stages ?? InferGroupedRunFailure(grouped);
        var promptAudit = HasPromptAudit(grouped.PromptAudit)
            ? grouped.PromptAudit
            : iterations.LastOrDefault()?.Result.PromptAudit ?? LegacyPromptAudit(sourceSchemaVersion);

        return grouped with
        {
            SchemaVersion = 11,
            Provenance = grouped.Provenance with { SourceSchemaVersion = sourceSchemaVersion },
            Iterations = iterations,
            Stages = runStages,
            PromptAudit = promptAudit
        };
    }

    private static RepairIterationResult Migrate(LegacyIteration legacy, int sourceSchemaVersion) => new()
    {
        Tokens = new TokenUsage(
            legacy.InputTokens,
            legacy.OutputTokens,
            legacy.CachedInputTokens,
            legacy.CacheWriteInputTokens,
            legacy.ReasoningTokens,
            UsageReported: legacy.TokenUsageReported,
            LegacyTotalTokens: PreserveLegacyTotal(
                legacy.TotalTokens,
                legacy.InputTokens,
                legacy.OutputTokens)),
        Api = new ApiUsage(
            legacy.ApiCalls,
            legacy.ApiRetryCount,
            legacy.KeyFailoverCount,
            legacy.ApiKeyFingerprint),
        Verification = new RepairVerification(
            legacy.ChangedLines,
            legacy.TouchedMethods,
            legacy.RegressedTests,
            legacy.FocalTestPassed,
            legacy.AdequacyVerified,
            legacy.BuggyOracleRequired,
            legacy.BugReproduced),
        Quality = new QualityMeasurement(
            legacy.LineCoverage,
            legacy.MutationScore,
            legacy.CoverageStatus,
            legacy.MutationStatus,
            legacy.CoverageDetail,
            legacy.MutationDetail,
            legacy.CoverageReportPath,
            legacy.MutationReportPath),
        Output = new LlmOutputEvidence(
            legacy.FirstPassJsonValid,
            legacy.OutputNormalization),
        Stages = InferLegacyStages(legacy),
        PromptAudit = LegacyPromptAudit(sourceSchemaVersion),
        Summary = legacy.Summary
    };

    private static RepairStageEvidence InferLegacyStages(LegacyIteration legacy)
    {
        var compileFailed = legacy.Summary.Contains("Compilation failed", StringComparison.OrdinalIgnoreCase);
        var focalFailed = legacy.Summary.Contains("Focal test failed", StringComparison.OrdinalIgnoreCase);
        var sourceFailed = legacy.Summary.Contains("conformance", StringComparison.OrdinalIgnoreCase) ||
                           legacy.Summary.Contains("preserve", StringComparison.OrdinalIgnoreCase) &&
                           legacy.RegressedTests > 0 && !legacy.FocalTestPassed;
        var oracleReached = legacy.FocalTestPassed && legacy.BuggyOracleRequired;
        var regressionReached = legacy.FocalTestPassed &&
                                (!legacy.BuggyOracleRequired || legacy.BugReproduced);
        return new RepairStageEvidence
        {
            InferredFromLegacy = true,
            LlmOutput = RepairStageOutcome.Success("Inferred from persisted legacy iteration."),
            SourceApplication = sourceFailed
                ? RepairStageOutcome.Failure("Inferred source/conformance rejection.")
                : RepairStageOutcome.Success("Inferred from persisted legacy iteration."),
            Compilation = compileFailed
                ? RepairStageOutcome.Failure("Inferred compilation failure.")
                : sourceFailed
                    ? new RepairStageOutcome()
                    : RepairStageOutcome.Success("Inferred from downstream legacy evidence."),
            FocalTest = legacy.FocalTestPassed
                ? RepairStageOutcome.Success()
                : focalFailed
                    ? RepairStageOutcome.Failure("Inferred focal-test failure.")
                    : new RepairStageOutcome(),
            BuggyOracle = !legacy.BuggyOracleRequired
                ? RepairStageOutcome.Skip("No buggy oracle configured.")
                : legacy.BugReproduced
                    ? RepairStageOutcome.Success()
                    : oracleReached
                        ? RepairStageOutcome.Failure("Legacy iteration did not reproduce the bug.")
                        : new RepairStageOutcome(),
            Regression = legacy.RegressedTests > 0
                ? RepairStageOutcome.Failure($"{legacy.RegressedTests} regression(s).")
                : regressionReached
                    ? RepairStageOutcome.Success()
                    : new RepairStageOutcome(),
            Coverage = InferMeasurement(legacy.CoverageStatus, legacy.CoverageDetail),
            Mutation = InferMeasurement(legacy.MutationStatus, legacy.MutationDetail)
        };
    }

    private static RepairStageEvidence InferGroupedStages(RepairIterationResult result)
    {
        var compileFailed = result.Summary.Contains("Compilation failed", StringComparison.OrdinalIgnoreCase);
        var focalFailed = result.Summary.Contains("Focal test failed", StringComparison.OrdinalIgnoreCase);
        var sourceFailed = result.Summary.Contains("conformance", StringComparison.OrdinalIgnoreCase) ||
                           result.Summary.Contains("preserve", StringComparison.OrdinalIgnoreCase) &&
                           result.RegressedTests > 0 && !result.FocalTestPassed;
        var oracleReached = result.FocalTestPassed && result.BuggyOracleRequired;
        var regressionReached = result.FocalTestPassed &&
                                (!result.BuggyOracleRequired || result.BugReproduced);
        return new RepairStageEvidence
        {
            InferredFromLegacy = true,
            LlmOutput = RepairStageOutcome.Success("Inferred from schema-7 iteration."),
            SourceApplication = sourceFailed
                ? RepairStageOutcome.Failure("Inferred source/conformance rejection.")
                : RepairStageOutcome.Success("Inferred from schema-7 iteration."),
            Compilation = compileFailed
                ? RepairStageOutcome.Failure("Inferred compilation failure.")
                : sourceFailed
                    ? new RepairStageOutcome()
                    : RepairStageOutcome.Success("Inferred from downstream schema-7 evidence."),
            FocalTest = result.FocalTestPassed
                ? RepairStageOutcome.Success()
                : focalFailed
                    ? RepairStageOutcome.Failure("Inferred focal-test failure.")
                    : new RepairStageOutcome(),
            BuggyOracle = !result.BuggyOracleRequired
                ? RepairStageOutcome.Skip("No buggy oracle configured.")
                : result.BugReproduced
                    ? RepairStageOutcome.Success()
                    : oracleReached
                        ? RepairStageOutcome.Failure("Schema-7 iteration did not reproduce the bug.")
                        : new RepairStageOutcome(),
            Regression = result.RegressedTests > 0
                ? RepairStageOutcome.Failure($"{result.RegressedTests} regression(s).")
                : regressionReached
                    ? RepairStageOutcome.Success()
                    : new RepairStageOutcome(),
            Coverage = InferMeasurement(result.CoverageStatus, result.CoverageDetail),
            Mutation = InferMeasurement(result.MutationStatus, result.MutationDetail)
        };
    }

    private static RepairStageEvidence InferLegacyRunFailure(LegacyRun legacy) =>
        legacy.FailureCategory is RunFailureCategory.InvalidModelOutput or
            RunFailureCategory.ApiRateLimit or
            RunFailureCategory.ProviderUnavailable or
            RunFailureCategory.AuthenticationFailure or
            RunFailureCategory.NetworkFailure
            ? new RepairStageEvidence
            {
                InferredFromLegacy = true,
                LlmOutput = RepairStageOutcome.Failure(legacy.StopReason)
            }
            : new RepairStageEvidence { InferredFromLegacy = true };

    private static RepairStageEvidence InferGroupedRunFailure(ExperimentRunResult result) =>
        result.FailureCategory is RunFailureCategory.InvalidModelOutput or
            RunFailureCategory.ApiRateLimit or
            RunFailureCategory.ProviderUnavailable or
            RunFailureCategory.AuthenticationFailure or
            RunFailureCategory.NetworkFailure
            ? new RepairStageEvidence
            {
                InferredFromLegacy = true,
                LlmOutput = RepairStageOutcome.Failure(result.StopReason)
            }
            : new RepairStageEvidence { InferredFromLegacy = true };

    private static bool HasStageEvidence(RepairStageEvidence stages) =>
        !stages.InferredFromLegacy &&
        (stages.LlmOutput.Status != RepairStageStatus.NotRun ||
         stages.SourceApplication.Status != RepairStageStatus.NotRun ||
         stages.Compilation.Status != RepairStageStatus.NotRun ||
         stages.FocalTest.Status != RepairStageStatus.NotRun ||
         stages.BuggyOracle.Status != RepairStageStatus.NotRun ||
         stages.Regression.Status != RepairStageStatus.NotRun ||
         stages.Coverage.Status != RepairStageStatus.NotRun ||
         stages.Mutation.Status != RepairStageStatus.NotRun);

    private static bool HasPromptAudit(PromptAuditEvidence audit) =>
        !string.IsNullOrWhiteSpace(audit.ContractVersion);

    private static PromptAuditEvidence LegacyPromptAudit(int sourceSchemaVersion) => new()
    {
        InferredFromLegacy = true,
        ContractSatisfied = false,
        Violation = $"Prompt audit was not persisted by source schema {sourceSchemaVersion}."
    };

    private static RepairStageOutcome InferMeasurement(MeasurementStatus status, string detail) => status switch
    {
        MeasurementStatus.Succeeded => RepairStageOutcome.Success(detail),
        MeasurementStatus.NotRun => new RepairStageOutcome(),
        _ => RepairStageOutcome.Failure(string.IsNullOrWhiteSpace(detail) ? status.ToString() : detail)
    };

    private static int? PreserveLegacyTotal(int total, int input, int output) =>
        total != input + output ? total : null;

    private sealed record LegacyIterationRecord
    {
        public int Iteration { get; init; }
        public DateTimeOffset StartedAt { get; init; }
        public LegacyIteration Result { get; init; } = new();
    }

    private sealed record LegacyIteration
    {
        public int InputTokens { get; init; }
        public int OutputTokens { get; init; }
        public int TotalTokens { get; init; }
        public int ApiCalls { get; init; }
        public int ChangedLines { get; init; }
        public int TouchedMethods { get; init; }
        public int RegressedTests { get; init; }
        public bool FocalTestPassed { get; init; }
        public double? LineCoverage { get; init; }
        public double? MutationScore { get; init; }
        public string Summary { get; init; } = "";
        public bool AdequacyVerified { get; init; }
        public int CachedInputTokens { get; init; }
        public int ReasoningTokens { get; init; }
        public int CacheWriteInputTokens { get; init; }
        public bool BuggyOracleRequired { get; init; }
        public bool BugReproduced { get; init; }
        public MeasurementStatus CoverageStatus { get; init; }
        public MeasurementStatus MutationStatus { get; init; }
        public int ApiRetryCount { get; init; }
        public int KeyFailoverCount { get; init; }
        public string ApiKeyFingerprint { get; init; } = "";
        public bool FirstPassJsonValid { get; init; } = true;
        public string OutputNormalization { get; init; } = "None";
        public bool TokenUsageReported { get; init; } = true;
        public string CoverageDetail { get; init; } = "";
        public string MutationDetail { get; init; } = "";
        public string CoverageReportPath { get; init; } = "";
        public string MutationReportPath { get; init; } = "";
    }

    private sealed record LegacyRun
    {
        public Guid RunId { get; init; }
        public string TaskId { get; init; } = "";
        public string ScenarioId { get; init; } = "";
        public RepairStrategy Strategy { get; init; }
        public int Repetition { get; init; }
        public RunStatus Status { get; init; }
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset CompletedAt { get; init; }
        public IReadOnlyList<LegacyIterationRecord> Iterations { get; init; } = [];
        public int TotalTokens { get; init; }
        public int TotalApiCalls { get; init; }
        public int TotalChangedLines { get; init; }
        public int RegressedTests { get; init; }
        public double? FinalLineCoverage { get; init; }
        public double? FinalMutationScore { get; init; }
        public string StopReason { get; init; } = "";
        public int TotalCachedInputTokens { get; init; }
        public int TotalReasoningTokens { get; init; }
        public int TotalCacheWriteInputTokens { get; init; }
        public bool BuggyOracleRequired { get; init; }
        public bool FinalBugReproduced { get; init; }
        public MeasurementStatus FinalCoverageStatus { get; init; }
        public MeasurementStatus FinalMutationStatus { get; init; }
        public RunFailureCategory FailureCategory { get; init; }
        public int TotalApiRetries { get; init; }
        public int TotalKeyFailovers { get; init; }
        public string LastApiKeyFingerprint { get; init; } = "";
        public string BatchId { get; init; } = "";
        public string ProviderName { get; init; } = "";
        public string ModelName { get; init; } = "";
        public string ApiProtocol { get; init; } = "";
        public int ConfiguredTokenBudget { get; init; }
        public int ConfiguredMaxIterations { get; init; }
        public int ScheduleSeed { get; init; }
        public int ScheduleIndex { get; init; }
        public string BlockId { get; init; } = "";
        public int TreatmentPosition { get; init; }
        public int ExpectedBatchRuns { get; init; }
        public int TotalInputTokens { get; init; }
        public int TotalOutputTokens { get; init; }
        public int UnreportedUsageResponses { get; init; }
        public bool TokenAccountingComplete { get; init; }
        public string FinalCoverageDetail { get; init; } = "";
        public string FinalMutationDetail { get; init; } = "";
        public string FinalCoverageReportPath { get; init; } = "";
        public string FinalMutationReportPath { get; init; } = "";
        public string ProjectId { get; init; } = "";
        public string DatasetId { get; init; } = "";
        public string ArtifactId { get; init; } = "";
        public int SchemaVersion { get; init; }
    }
}
