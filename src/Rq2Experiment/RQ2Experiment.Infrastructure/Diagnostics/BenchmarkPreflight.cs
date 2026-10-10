using RQ2Experiment.Infrastructure.Benchmark;
using RQ2Experiment.Infrastructure.Execution;
using RQ2Experiment.Core.Domain;
using RQ2Experiment.Core.Engine;
using System.Text.Json;

namespace RQ2Experiment.Infrastructure.Diagnostics;

public static class BenchmarkPreflight
{
    public static async Task<IReadOnlyList<string>> RunAsync(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        var configuration = new ExperimentConfiguration(
            "preflight-model",
            MaxIterations: 1,
            TokenBudget: 1,
            Repetitions: 3,
            Strategies:
            [
                RepairStrategy.TraceabilityGuidedScenarioLocal,
                RepairStrategy.FailingTestMethod,
                RepairStrategy.FullClassRegeneration
            ]);
        return await RunAsync(
            manifestPath,
            repositoryOverride: null,
            configuration,
            cancellationToken);
    }

    public static async Task<IReadOnlyList<string>> RunAsync(
        string manifestPath,
        string? repositoryOverride,
        ExperimentConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var benchmark = await BenchmarkManifestLoader.LoadDefinitionAsync(
            manifestPath,
            repositoryOverride,
            cancellationToken);
        var tasks = benchmark.Tasks;
        var studyPlan = StudyDesignPlanner.Assess(tasks, benchmark.StudyDesign);
        var effectiveConfiguration = configuration with { StudyPlan = studyPlan.Evidence };
        var design = ExperimentDesignValidator.Validate(tasks, effectiveConfiguration);
        var messages = new List<string>
        {
            $"Design: {design.Summary}.",
            $"Sampling: {studyPlan.Summary}.",
            $"POWER: FWER alpha={studyPlan.Evidence.FamilyWiseAlpha:0.###}; " +
            $"planning alpha={studyPlan.Evidence.PlanningAlpha:0.###}; target={studyPlan.Evidence.TargetPower:P0}; " +
            $"achieved={studyPlan.Evidence.AchievedPower:P1}; required discordant={studyPlan.Evidence.RequiredDiscordantUnits}; " +
            $"required independent={studyPlan.Evidence.RequiredIndependentUnits}.",
            $"ANALYSIS: cluster=projectId; confidence={Rq2StatisticalAnalyzer.ClusterConfidenceLevel:P0}; " +
            $"bootstrap replicates={Rq2StatisticalAnalyzer.ClusterBootstrapReplicates}; " +
            $"minimum project clusters={Rq2StatisticalAnalyzer.MinimumProjectClustersForInference}.",
            $"OUTCOMES: contract={studyPlan.Evidence.OutcomeContractVersion}; primary=repair success; " +
            $"efficiency=token/API/iterations/edit scope; guardrails=regression/coverage/mutation; " +
            $"NI margins success={studyPlan.Evidence.SuccessNonInferiorityMargin:P0}, " +
            $"coverage={studyPlan.Evidence.CoverageNonInferiorityMargin:P0}, " +
            $"mutation={studyPlan.Evidence.MutationNonInferiorityMargin:P0}."
            + $" provisional={studyPlan.Evidence.OutcomePlanProvisional}.",
            "MEASUREMENT: readiness preflight validates repository/build/failing-test reproducibility only. " +
            "It does not certify JaCoCo or PIT compatibility because those tools must execute against a repaired, passing test. " +
            "Inspect measurement-diagnostics.csv after a small pilot before launching a large campaign."
        };
        messages.AddRange(design.Warnings.Select(warning => $"WARNING: {warning}"));
        messages.AddRange(studyPlan.Warnings.Select(warning => $"WARNING: {warning}"));
        if (studyPlan.SelectedProjects < Rq2StatisticalAnalyzer.MinimumProjectClustersForInference)
        {
            messages.Add(
                $"WARNING: Project-cluster inference requires " +
                $"{Rq2StatisticalAnalyzer.MinimumProjectClustersForInference} projects; selected={studyPlan.SelectedProjects}.");
        }

        var logRoot = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(manifestPath))!,
            "preflight-logs",
            DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(logRoot);
        await File.WriteAllTextAsync(
            Path.Combine(logRoot, "study-plan-assessment.json"),
            JsonSerializer.Serialize(
                new
                {
                    benchmark.StudyDesign,
                    assessment = studyPlan,
                    clusterAnalysisPlan = new
                    {
                        clusterField = "projectId",
                        confidenceLevel = Rq2StatisticalAnalyzer.ClusterConfidenceLevel,
                        bootstrapReplicates = Rq2StatisticalAnalyzer.ClusterBootstrapReplicates,
                        minimumProjectClusters = Rq2StatisticalAnalyzer.MinimumProjectClustersForInference
                    }
                },
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }),
            cancellationToken);

        var readinessRows = new List<BenchmarkTaskReadiness>(tasks.Count);
        foreach (var task in tasks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var taskLogs = Path.Combine(logRoot, Sanitize(task.TaskId));
            var fixedLogs = Path.Combine(taskLogs, "fixed");
            Directory.CreateDirectory(fixedLogs);
            var initialFailureObserved = false;
            bool? buggyBaselineCompiles = task.RequiresBuggyOracle ? false : null;
            var exclusionReason = string.Empty;
            try
            {
                var fixedRunner = new JavaBuildRunner(task, task.RepositoryPath, fixedLogs);
                var fixedCompile = await fixedRunner.CompileAsync(0, cancellationToken);
                initialFailureObserved = !fixedCompile.Succeeded;
                if (fixedCompile.Succeeded)
                {
                    var fixedFocal = await fixedRunner.RunFocalTestAsync(0, cancellationToken);
                    initialFailureObserved = !fixedFocal.Succeeded;
                }

                if (!initialFailureObserved)
                {
                    exclusionReason = "Initial generated-test failure was not reproduced on the fixed revision.";
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                exclusionReason = $"Fixed-revision check failed: {exception.Message}";
            }

            if (initialFailureObserved && task.RequiresBuggyOracle)
            {
                var buggyLogs = Path.Combine(taskLogs, "buggy");
                Directory.CreateDirectory(buggyLogs);
                try
                {
                    var buggyRunner = new JavaBuildRunner(task, task.BuggyRepositoryPath, buggyLogs);
                    var buggyCompile = await buggyRunner.CompileAsync(0, cancellationToken);
                    buggyBaselineCompiles = buggyCompile.Succeeded;
                    if (!buggyCompile.Succeeded)
                    {
                        exclusionReason = $"Buggy baseline does not compile. See {buggyLogs}.";
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    buggyBaselineCompiles = false;
                    exclusionReason = $"Buggy-revision check failed: {exception.Message}";
                }
            }

            var ready = initialFailureObserved &&
                        (!task.RequiresBuggyOracle || buggyBaselineCompiles == true);
            readinessRows.Add(new BenchmarkTaskReadiness(
                task.TaskId,
                task.ProjectId,
                task.DatasetId,
                task.ArtifactId,
                task.IndependenceUnitId,
                task.SamplingStratum,
                ManifestValid: true,
                initialFailureObserved,
                task.RequiresBuggyOracle,
                buggyBaselineCompiles,
                ready,
                exclusionReason));
            messages.Add(ready
                ? $"READY {task.TaskId}: fixed failure reproduced; " +
                  (task.RequiresBuggyOracle ? "buggy baseline compiles." : "buggy oracle is not required.")
                : $"EXCLUDED {task.TaskId}: {exclusionReason}");
        }

        var readinessExport = await BenchmarkReadinessExporter.ExportAsync(
            readinessRows,
            logRoot,
            cancellationToken);
        var readyTasks = readinessRows.Count(row => row.Ready);
        var readyUnits = readinessRows.Where(row => row.Ready)
            .Select(row => row.IndependenceUnitId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var readyProjects = readinessRows.Where(row => row.Ready)
            .Select(row => row.ProjectId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var readyStrata = readinessRows.Where(row => row.Ready)
            .Select(row => row.SamplingStratum)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        messages.Add(
            $"READINESS: {readyTasks}/{tasks.Count} task(s), {readyUnits} independent unit(s), " +
            $"{readyProjects} project(s), {readyStrata} stratum/strata ready.");
        messages.Add($"Task readiness CSV: {readinessExport.TaskReadinessPath}");
        messages.Add($"Sampling-frame summary CSV: {readinessExport.SamplingFrameSummaryPath}");
        messages.Add($"Benchmark readiness JSON: {readinessExport.JsonPath}");

        if (readyTasks != tasks.Count)
        {
            throw new InvalidOperationException(
                $"Preflight excluded {tasks.Count - readyTasks}/{tasks.Count} task(s); no LLM call is allowed. " +
                $"Review {readinessExport.TaskReadinessPath}");
        }

        messages.Add($"Preflight passed: {design.ExpectedRunCount} planned run(s); no LLM call was made.");
        messages.Add($"Preflight logs: {logRoot}");
        return messages;
    }

    private static string Sanitize(string value) =>
        string.Concat(value.Select(character => char.IsLetterOrDigit(character) ? character : '-'));
}
