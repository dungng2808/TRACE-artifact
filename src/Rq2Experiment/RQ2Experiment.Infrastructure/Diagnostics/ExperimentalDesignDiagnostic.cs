using RQ2Experiment.Core.Domain;
using RQ2Experiment.Core.Engine;
using RQ2Experiment.Infrastructure.Persistence;

namespace RQ2Experiment.Infrastructure.Diagnostics;

internal static class ExperimentalDesignDiagnostic
{
    public static async Task<string> RunAsync(CancellationToken cancellationToken)
    {
        var tasks = new[]
        {
            CreateTask("TASK-A"),
            CreateTask("TASK-B")
        };
        var configuration = new ExperimentConfiguration(
            "diagnostic-model",
            MaxIterations: 4,
            TokenBudget: 10_000,
            Repetitions: 3,
            Strategies:
            [
                RepairStrategy.TraceabilityGuidedScenarioLocal,
                RepairStrategy.FailingTestMethod,
                RepairStrategy.FullClassRegeneration
            ],
            BatchId: "phase-5-diagnostic",
            ScheduleSeed: 20260814);

        var first = ExperimentScheduleBuilder.Build(tasks, configuration);
        var second = ExperimentScheduleBuilder.Build(tasks, configuration);
        if (first.Count != 18 ||
            !first.Select(ItemSignature).SequenceEqual(second.Select(ItemSignature), StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Phase 5 schedule is incomplete or not reproducible for a fixed seed.");
        }

        foreach (var task in tasks)
        {
            var taskItems = first.Where(item => item.Task.TaskId == task.TaskId).ToArray();
            for (var repetition = 1; repetition <= configuration.Repetitions; repetition++)
            {
                var block = taskItems.Where(item => item.Repetition == repetition).ToArray();
                if (block.Length != configuration.Strategies.Count ||
                    block.Select(item => item.Strategy).Distinct().Count() != configuration.Strategies.Count)
                {
                    throw new InvalidOperationException("Phase 5 paired block does not contain every treatment exactly once.");
                }
            }

            foreach (var strategy in configuration.Strategies)
            {
                var positions = taskItems
                    .Where(item => item.Strategy == strategy)
                    .Select(item => item.TreatmentPosition)
                    .Order()
                    .ToArray();
                if (!positions.SequenceEqual([1, 2, 3]))
                {
                    throw new InvalidOperationException("Phase 5 treatment positions are not counterbalanced.");
                }
            }
        }

        var exportRoot = Path.Combine(
            Environment.CurrentDirectory,
            "artifacts",
            "diagnostics-phase5",
            Guid.NewGuid().ToString("N"));
        var exportResults = first.Take(2).Select(item => CreateResult(item, first.Count, configuration)).ToArray();
        var export = await CsvExperimentExporter.ExportAsync(exportResults, exportRoot, cancellationToken);
        var runsCsv = await File.ReadAllTextAsync(export.RunsPath, cancellationToken);
        var iterationsCsv = await File.ReadAllTextAsync(export.IterationsPath, cancellationToken);
        if (!runsCsv.StartsWith("schema_version,source_schema_version,batch_id,schedule_seed,schedule_index", StringComparison.Ordinal) ||
            !runsCsv.Contains("phase-5-diagnostic", StringComparison.Ordinal) ||
            !runsCsv.Contains("token_accounting_complete", StringComparison.Ordinal) ||
            !runsCsv.Contains("project_id", StringComparison.Ordinal) ||
            !runsCsv.Contains("coverage_detail", StringComparison.Ordinal) ||
            !runsCsv.Contains("compilation_stage", StringComparison.Ordinal) ||
            !runsCsv.Contains("prompt_contract_satisfied", StringComparison.Ordinal) ||
            !runsCsv.Contains("independence_unit_id", StringComparison.Ordinal) ||
            !runsCsv.Contains("power_plan_sha256", StringComparison.Ordinal) ||
            !iterationsCsv.Contains("api_key_fingerprint", StringComparison.Ordinal) ||
            !iterationsCsv.Contains("token_usage_reported", StringComparison.Ordinal) ||
            !iterationsCsv.Contains("buggy_oracle_stage", StringComparison.Ordinal) ||
            !iterationsCsv.Contains("forbidden_context_detected", StringComparison.Ordinal) ||
            !iterationsCsv.Contains("mutation_report_path", StringComparison.Ordinal) ||
            !iterationsCsv.Contains("\"Repair, verified\"", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Phase 5 analysis CSV export is missing provenance or valid escaping.");
        }

        var calculatedPower = StudyDesignPlanner.Calculate(new PowerPlanSpecification());
        if (calculatedPower.RequiredIndependentUnits <= 0 ||
            calculatedPower.RequiredDiscordantUnits <= 0 ||
            Math.Abs(calculatedPower.PlanningAlpha - 0.025) > 1e-12)
        {
            throw new InvalidOperationException("Phase 7.4 exact sign-test power calculation failed.");
        }

        var studyPlan = new StudyPlanEvidence(
            StudyId: "DIAGNOSTIC-STUDY",
            SamplingPlanSha256: "DIAGNOSTIC-SAMPLING-HASH",
            PowerPlanSha256: "DIAGNOSTIC-POWER-HASH",
            SelectionSeed: 20260814,
            PlannedIndependentUnits: 5,
            RequiredIndependentUnits: 5,
            RequiredDiscordantUnits: 5,
            MinimumProjects: 5,
            FamilyWiseAlpha: 0.05,
            PlanningAlpha: 0.025,
            TargetPower: 0.8,
            AchievedPower: 0.9,
            AlternativeWinProbability: 0.9,
            ExpectedDiscordantUnitRate: 1,
            Provisional: false,
            SamplingPlanComplete: true,
            PowerPlanDeclared: true,
            OutcomePlanSha256: "DIAGNOSTIC-OUTCOME-HASH",
            OutcomeContractVersion: "RQ2-OUTCOME-PLAN-1",
            SuccessNonInferiorityMargin: 0.05,
            CoverageNonInferiorityMargin: 0.05,
            MutationNonInferiorityMargin: 0.05,
            OutcomePlanDeclared: true,
            OutcomePlanProvisional: false);
        var analysisTasks = Enumerable.Range(1, 5)
            .Select(index => CreateTask($"ANALYSIS-{index}", $"PROJECT-{index}"))
            .ToArray();
        var analysisConfiguration = configuration with
        {
            Repetitions = 1,
            BatchId = "phase-7-4-diagnostic",
            StudyPlan = studyPlan
        };
        var analysisSchedule = ExperimentScheduleBuilder.Build(analysisTasks, analysisConfiguration);
        var analysisResults = analysisSchedule
            .Select(item => CreateResult(item, analysisSchedule.Count, analysisConfiguration))
            .ToArray();
        var statisticalAnalysis = Rq2StatisticalAnalyzer.Analyze(analysisResults);
        var ftmrTokenEffect = statisticalAnalysis.Outcomes.Single(outcome =>
            outcome.Comparison == "TGSLR vs FTMR" && outcome.Outcome == "Total tokens");
        var fcrTokenEffect = statisticalAnalysis.Outcomes.Single(outcome =>
            outcome.Comparison == "TGSLR vs FCR" && outcome.Outcome == "Total tokens");
        var ftmrSuccessEffect = statisticalAnalysis.Outcomes.Single(outcome =>
            outcome.Comparison == "TGSLR vs FTMR" && outcome.Outcome == "Repair success");
        if (!statisticalAnalysis.Readiness.InferentialReady ||
            !statisticalAnalysis.Readiness.ClusterAnalysisReady ||
            !statisticalAnalysis.Readiness.OutcomePlanComplete ||
            statisticalAnalysis.Comparisons.Count != 2 ||
            statisticalAnalysis.Outcomes.Count != 18 ||
            statisticalAnalysis.Outcomes.Any(outcome =>
                outcome.ProjectClusters != 5 ||
                outcome.ClusterBootstrapLower is null ||
                outcome.ClusterBootstrapUpper is null) ||
            ftmrTokenEffect.MeanDelta != -10 ||
            ftmrTokenEffect.ClusterBootstrapLower != -10 ||
            ftmrTokenEffect.ClusterBootstrapUpper != -10 ||
            fcrTokenEffect.MeanDelta != -60 ||
            fcrTokenEffect.ClusterBootstrapLower != -60 ||
            fcrTokenEffect.ClusterBootstrapUpper != -60 ||
            ftmrSuccessEffect.MeanDelta != 0 ||
            ftmrSuccessEffect.ClusterBootstrapLower != 0 ||
            ftmrSuccessEffect.ClusterBootstrapUpper != 0 ||
            statisticalAnalysis.Comparisons.Any(comparison =>
                comparison.HolmAdjustedPValue != 1 ||
                comparison.ProjectClusters != 5 ||
                comparison.ClusterBootstrapLower != 0 ||
                comparison.ClusterBootstrapUpper != 0))
        {
            throw new InvalidOperationException("Phase 6F task-level readiness or paired statistics diagnostic failed.");
        }

        var analysisExport = await Rq2AnalysisExporter.ExportAsync(
            analysisResults,
            statisticalAnalysis,
            exportRoot,
            cancellationToken);
        var pairwiseCsv = await File.ReadAllTextAsync(analysisExport.PairwisePath, cancellationToken);
        var taskLevelCsv = await File.ReadAllTextAsync(analysisExport.TaskLevelPath, cancellationToken);
        var projectLevelCsv = await File.ReadAllTextAsync(analysisExport.ProjectLevelPath, cancellationToken);
        var outcomesCsv = await File.ReadAllTextAsync(analysisExport.OutcomesPath, cancellationToken);
        if (!pairwiseCsv.Contains("holm_adjusted_p", StringComparison.Ordinal) ||
            !pairwiseCsv.Contains("cluster_bootstrap_ci_lower", StringComparison.Ordinal) ||
            !taskLevelCsv.Contains("source_schema_version,study_id,sampling_plan_sha256,power_plan_sha256", StringComparison.Ordinal) ||
            !taskLevelCsv.Contains("independence_unit_id", StringComparison.Ordinal) ||
            !projectLevelCsv.Contains("project_unit_mean_success_rate", StringComparison.Ordinal) ||
            projectLevelCsv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length != 16 ||
            !outcomesCsv.Contains("noninferiority_margin", StringComparison.Ordinal) ||
            !outcomesCsv.Contains("TGSLR vs FTMR,Total tokens,Efficiency,tokens,5,5,120,130,-10,-10,-10", StringComparison.Ordinal) ||
            outcomesCsv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length != 19)
        {
            throw new InvalidOperationException("Phase 6F statistical CSV export is incomplete.");
        }

        var readinessExport = await BenchmarkReadinessExporter.ExportAsync(
        [
            new BenchmarkTaskReadiness(
                "READY-1", "PROJECT-1", "DATASET", "ARTIFACT-1", "UNIT-1", "parsing",
                true, true, true, true, true, string.Empty),
            new BenchmarkTaskReadiness(
                "EXCLUDED-1", "PROJECT-2", "DATASET", "ARTIFACT-2", "UNIT-2", "validation",
                true, true, true, false, false, "Buggy baseline does not compile, dependency missing."),
            new BenchmarkTaskReadiness(
                "READY-2", "PROJECT-2", "DATASET", "ARTIFACT-3", "UNIT-3", "validation",
                true, true, false, null, true, string.Empty)
        ],
            exportRoot,
            cancellationToken);
        var readinessCsv = await File.ReadAllTextAsync(readinessExport.TaskReadinessPath, cancellationToken);
        var samplingFrameCsv = await File.ReadAllTextAsync(readinessExport.SamplingFrameSummaryPath, cancellationToken);
        if (!readinessCsv.Contains("fixed_initial_failure_reproduced", StringComparison.Ordinal) ||
            !readinessCsv.Contains("EXCLUDED-1,PROJECT-2,DATASET,ARTIFACT-2,UNIT-2,validation,TRUE,TRUE,TRUE,FALSE,FALSE", StringComparison.Ordinal) ||
            !readinessCsv.Contains("\"Buggy baseline does not compile, dependency missing.\"", StringComparison.Ordinal) ||
            !samplingFrameCsv.Contains("overall,all,3,3,2,2", StringComparison.Ordinal) ||
            !samplingFrameCsv.Contains("project,PROJECT-2,2,2,1,1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Phase 7.7 benchmark-readiness CSV diagnostic failed.");
        }

        return "Reproducible schedule, frozen outcome contract, independent-unit paired effects, power gate, project-cluster bootstrap CI, Holm correction, analysis CSV export, and benchmark-readiness audit: OK";
    }

    private static ExperimentTask CreateTask(string taskId, string projectId = "PROJECT-A") => new(
        taskId,
        "repository",
        $"S-{taskId}",
        $"T-{taskId}",
        "generatedTest",
        "Scenario",
        "Scenario -> test",
        "Initial failure",
        ProjectId: projectId,
        DatasetId: "DIAGNOSTIC-DATASET",
        ArtifactId: $"ARTIFACT-{taskId}",
        SourceUri: "https://example.invalid/diagnostic",
        IndependenceUnitId: $"UNIT-{taskId}",
        SamplingStratum: "diagnostic");

    private static string ItemSignature(ExperimentScheduleItem item) =>
        $"{item.ScheduleIndex}|{item.Task.TaskId}|{item.Repetition}|{item.Strategy}|{item.TreatmentPosition}|{item.BlockId}";

    private static ExperimentRunResult CreateResult(
        ExperimentScheduleItem item,
        int expectedRuns,
        ExperimentConfiguration configuration)
    {
        var now = DateTimeOffset.UtcNow;
        var (inputTokens, outputTokens, apiCalls, changedLines, touchedMethods) = item.Strategy switch
        {
            RepairStrategy.TraceabilityGuidedScenarioLocal => (100, 20, 1, 3, 1),
            RepairStrategy.FailingTestMethod => (110, 20, 1, 4, 1),
            RepairStrategy.FullClassRegeneration => (150, 30, 2, 8, 2),
            _ => throw new ArgumentOutOfRangeException(nameof(item), item.Strategy, "Unknown repair strategy.")
        };
        var iterationResult = new RepairIterationResult
        {
            Tokens = new TokenUsage(InputTokens: inputTokens, OutputTokens: outputTokens),
            Api = new ApiUsage(Calls: apiCalls, LastKeyFingerprint: "ABCDEF12"),
            Verification = new RepairVerification(
                ChangedLines: changedLines,
                TouchedMethods: touchedMethods,
                FocalTestPassed: true,
                AdequacyVerified: true),
            Quality = new QualityMeasurement(
                LineCoverage: 0.8,
                MutationScore: 0.7,
                CoverageStatus: MeasurementStatus.Succeeded,
                MutationStatus: MeasurementStatus.Succeeded),
            Stages = SuccessfulStages(),
            PromptAudit = SuccessfulPromptAudit(item.Strategy),
            Summary = "Repair, verified"
        };
        return new ExperimentRunResult
        {
            Identity = new RunIdentity(
                Guid.NewGuid(),
                item.Task.TaskId,
                item.Task.ScenarioId,
                item.Strategy,
                item.Repetition),
            Provenance = new RunProvenance(
                configuration.BatchId,
                configuration.ProviderName,
                configuration.ModelName,
                configuration.ApiProtocol,
                configuration.TokenBudget,
                configuration.MaxIterations,
                item.Task.ProjectId,
                item.Task.DatasetId,
                item.Task.ArtifactId,
                IndependenceUnitId: item.Task.IndependenceUnitId,
                SamplingStratum: item.Task.SamplingStratum),
            Design = new ExperimentDesignMetadata(
                configuration.ScheduleSeed,
                item.ScheduleIndex,
                item.BlockId,
                item.TreatmentPosition,
                expectedRuns,
                configuration.StudyPlan),
            Timing = new ExecutionTiming(now, now.AddSeconds(1)),
            Tokens = iterationResult.Tokens,
            Api = iterationResult.Api,
            Verification = iterationResult.Verification,
            Quality = iterationResult.Quality,
            Stages = iterationResult.Stages,
            PromptAudit = iterationResult.PromptAudit,
            Status = RunStatus.Success,
            Iterations = [new IterationRecord(1, now, iterationResult)],
            StopReason = "Core repair verified."
        };
    }

    private static RepairStageEvidence SuccessfulStages() => new()
    {
        LlmOutput = RepairStageOutcome.Success(),
        SourceApplication = RepairStageOutcome.Success(),
        Compilation = RepairStageOutcome.Success(),
        FocalTest = RepairStageOutcome.Success(),
        BuggyOracle = RepairStageOutcome.Skip("Diagnostic has no buggy oracle."),
        Regression = RepairStageOutcome.Success(),
        Coverage = RepairStageOutcome.Success(),
        Mutation = RepairStageOutcome.Success()
    };

    private static PromptAuditEvidence SuccessfulPromptAudit(RepairStrategy strategy) => new()
    {
        ContractVersion = PromptAuditEvidence.CurrentContractVersion,
        ContextPolicy = strategy == RepairStrategy.FailingTestMethod
            ? "failure-evidence-only"
            : "scenario+traceability",
        EditScope = strategy == RepairStrategy.FullClassRegeneration
            ? "full-test-class"
            : "focal-method",
        ScenarioIncluded = strategy != RepairStrategy.FailingTestMethod,
        TraceabilityIncluded = strategy != RepairStrategy.FailingTestMethod,
        FocalMethodIncluded = strategy != RepairStrategy.FullClassRegeneration,
        FullTestClassIncluded = strategy == RepairStrategy.FullClassRegeneration,
        ToolEvidenceIncluded = true,
        ProductionContextIncluded = true,
        ContractSatisfied = true,
        PromptSha256 = "DIAGNOSTIC-PROMPT",
        InstructionsSha256 = "DIAGNOSTIC-INSTRUCTIONS"
    };
}
