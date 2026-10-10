using RQ2Experiment.Core.Abstractions;
using RQ2Experiment.Core.Domain;

namespace RQ2Experiment.Core.Engine;

public sealed class ExperimentCoordinator(
    IRepairExecutor repairExecutor,
    IRunRepository runRepository)
{
    public event EventHandler<ExperimentProgress>? ProgressChanged;

    public async Task<IReadOnlyList<ExperimentRunResult>> RunAsync(
        IReadOnlyList<ExperimentTask> tasks,
        ExperimentConfiguration configuration,
        CancellationToken cancellationToken,
        IReadOnlySet<RunSlot>? skippedSlots = null)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.Validate();
        if (string.IsNullOrWhiteSpace(configuration.BatchId))
        {
            configuration = configuration with
            {
                BatchId = $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}"[..26]
            };
        }

        if (tasks.Count == 0)
        {
            throw new ArgumentException("At least one experiment task is required.", nameof(tasks));
        }

        var schedule = ExperimentScheduleBuilder.Build(tasks, configuration);
        var pending = schedule
            .Where(item => skippedSlots?.Contains(new RunSlot(item.Task.TaskId, item.Strategy, item.Repetition)) != true)
            .ToArray();
        if (pending.Length == 0) return [];

        // Every run creates its own fixed/buggy workspace, and the JSONL
        // repository serializes writes.  Bounded parallelism therefore reduces
        // wall-clock time without sharing test sources or checkpoint writes.
        using var concurrencyGate = new SemaphoreSlim(Math.Min(configuration.MaxParallelRuns, pending.Length));
        var workers = pending.Select(ExecuteAndCheckpointAsync).ToArray();
        var completed = await Task.WhenAll(workers);
        return completed.OrderBy(result => result.Design.ScheduleIndex).ToArray();

        async Task<ExperimentRunResult> ExecuteAndCheckpointAsync(ExperimentScheduleItem item)
        {
            await concurrencyGate.WaitAsync(cancellationToken);
            try
            {
                var result = await RunSingleAsync(
                    item,
                    schedule.Count,
                    configuration,
                    cancellationToken);
                await runRepository.SaveAsync(result, CancellationToken.None);
                return result;
            }
            finally
            {
                concurrencyGate.Release();
            }
        }
    }

    private async Task<ExperimentRunResult> RunSingleAsync(
        ExperimentScheduleItem scheduleItem,
        int expectedBatchRuns,
        ExperimentConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var task = scheduleItem.Task;
        var strategy = scheduleItem.Strategy;
        var repetition = scheduleItem.Repetition;
        var runId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        var iterations = new List<IterationRecord>();
        var totalTokens = 0;
        var totalInputTokens = 0;
        var totalOutputTokens = 0;
        var totalCalls = 0;
        var totalChangedLines = 0;
        var regressions = 0;
        var totalCachedInputTokens = 0;
        var totalReasoningTokens = 0;
        var totalCacheWriteInputTokens = 0;
        var totalApiRetries = 0;
        var totalKeyFailovers = 0;
        var unreportedUsageResponses = 0;
        var lastApiKeyFingerprint = string.Empty;
        var status = RunStatus.Running;
        var failureCategory = RunFailureCategory.None;
        var stopReason = "Maximum iterations reached.";
        var terminalStages = new RepairStageEvidence();
        var terminalPromptAudit = new PromptAuditEvidence();

        Publish(runId, task, strategy, repetition, status, 0, 0, 0, 0, "Run started.");

        try
        {
            await using var session = await repairExecutor.CreateSessionAsync(
                runId,
                task,
                strategy,
                repetition,
                configuration,
                cancellationToken);

            for (var iteration = 1; iteration <= configuration.MaxIterations; iteration++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var iterationResult = await session.ExecuteIterationAsync(
                    iteration,
                    cancellationToken);
                terminalStages = iterationResult.Stages;
                terminalPromptAudit = iterationResult.PromptAudit;

                iterations.Add(new IterationRecord(iteration, DateTimeOffset.UtcNow, iterationResult));
                totalTokens += iterationResult.TotalTokens;
                totalInputTokens += iterationResult.InputTokens;
                totalOutputTokens += iterationResult.OutputTokens;
                totalCalls += iterationResult.ApiCalls;
                totalChangedLines = iterationResult.ChangedLines;
                regressions = iterationResult.RegressedTests;
                totalCachedInputTokens += iterationResult.CachedInputTokens;
                totalReasoningTokens += iterationResult.ReasoningTokens;
                totalCacheWriteInputTokens += iterationResult.CacheWriteInputTokens;
                totalApiRetries += iterationResult.ApiRetryCount;
                totalKeyFailovers += iterationResult.KeyFailoverCount;
                if (!iterationResult.TokenUsageReported)
                {
                    unreportedUsageResponses++;
                }
                if (!string.IsNullOrWhiteSpace(iterationResult.ApiKeyFingerprint))
                {
                    lastApiKeyFingerprint = iterationResult.ApiKeyFingerprint;
                }

                Publish(
                    runId,
                    task,
                    strategy,
                    repetition,
                    status,
                    iteration,
                    totalTokens,
                    totalChangedLines,
                    regressions,
                    iterationResult.Summary);

                // A provider response cannot be stopped retroactively once it has
                // completed.  Verify that response before enforcing the ceiling for
                // a *next* iteration; otherwise a repair that already passed every
                // core oracle is incorrectly recorded as a failure.  The stop reason
                // explicitly preserves this budget overrun for downstream analysis.
                if (iterationResult.FocalTestPassed &&
                    iterationResult.RegressedTests == 0 &&
                    (!iterationResult.BuggyOracleRequired || iterationResult.BugReproduced))
                {
                    status = RunStatus.Success;
                    stopReason = totalTokens > configuration.TokenBudget
                        ? $"{BuildCoreSuccessReason(iterationResult)} Token budget was exceeded by the completed API call; no further iteration was started."
                        : BuildCoreSuccessReason(iterationResult);
                    break;
                }

                if (totalTokens > configuration.TokenBudget)
                {
                    status = RunStatus.BudgetExhausted;
                    failureCategory = RunFailureCategory.BudgetExhausted;
                    stopReason = "Token budget was exceeded by the completed API call.";
                    break;
                }

                if (totalTokens == configuration.TokenBudget)
                {
                    status = RunStatus.BudgetExhausted;
                    failureCategory = RunFailureCategory.BudgetExhausted;
                    stopReason = "Token budget exhausted.";
                    break;
                }
            }

            if (status == RunStatus.Running)
            {
                status = RunStatus.Failed;
                failureCategory = iterations.LastOrDefault()?.Result.RegressedTests > 0
                    ? RunFailureCategory.RegressionFailure
                    : RunFailureCategory.RepairFailure;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            status = RunStatus.Cancelled;
            stopReason = "Cancelled by user.";
        }
        catch (ExperimentExecutionException exception)
        {
            status = RunStatus.Failed;
            failureCategory = exception.Category;
            totalCalls += exception.ApiCalls;
            totalTokens += exception.TotalTokens;
            totalInputTokens += exception.InputTokens;
            totalOutputTokens += exception.OutputTokens;
            totalCachedInputTokens += exception.CachedInputTokens;
            totalCacheWriteInputTokens += exception.CacheWriteInputTokens;
            totalReasoningTokens += exception.ReasoningTokens;
            totalApiRetries += exception.ApiRetries;
            totalKeyFailovers += exception.KeyFailovers;
            unreportedUsageResponses += exception.UnreportedUsageResponses;
            if (!string.IsNullOrWhiteSpace(exception.ApiKeyFingerprint))
            {
                lastApiKeyFingerprint = exception.ApiKeyFingerprint;
            }
            stopReason = exception.Message;
            terminalStages = exception.StageEvidence ?? BuildFailureStages(exception);
            terminalPromptAudit = exception.PromptAuditEvidence ?? new PromptAuditEvidence();
        }
        catch (Exception exception)
        {
            status = RunStatus.Failed;
            failureCategory = RunFailureCategory.Unexpected;
            stopReason = $"{exception.GetType().Name}: {exception.Message}";
        }

        var finalIteration = iterations.LastOrDefault()?.Result;
        var result = new ExperimentRunResult
        {
            Identity = new RunIdentity(runId, task.TaskId, task.ScenarioId, strategy, repetition),
            Provenance = new RunProvenance(
                configuration.BatchId,
                configuration.ProviderName,
                configuration.ModelName,
                configuration.ApiProtocol,
                configuration.TokenBudget,
                configuration.MaxIterations,
                task.ProjectId,
                task.DatasetId,
                task.ArtifactId,
                IndependenceUnitId: task.IndependenceUnitId,
                SamplingStratum: task.SamplingStratum,
                RunPlanFingerprint: configuration.RunPlanFingerprint),
            Design = new ExperimentDesignMetadata(
                configuration.ScheduleSeed,
                scheduleItem.ScheduleIndex,
                scheduleItem.BlockId,
                scheduleItem.TreatmentPosition,
                expectedBatchRuns,
                configuration.StudyPlan),
            Timing = new ExecutionTiming(startedAt, DateTimeOffset.UtcNow),
            Tokens = new TokenUsage(
                totalInputTokens,
                totalOutputTokens,
                totalCachedInputTokens,
                totalCacheWriteInputTokens,
                totalReasoningTokens,
                unreportedUsageResponses,
                unreportedUsageResponses == 0),
            Api = new ApiUsage(
                totalCalls,
                totalApiRetries,
                totalKeyFailovers,
                lastApiKeyFingerprint),
            Verification = new RepairVerification(
                totalChangedLines,
                finalIteration?.TouchedMethods ?? 0,
                regressions,
                finalIteration?.FocalTestPassed ?? false,
                finalIteration?.AdequacyVerified ?? false,
                task.RequiresBuggyOracle,
                finalIteration?.BugReproduced ?? false),
            Quality = new QualityMeasurement(
                finalIteration?.LineCoverage,
                finalIteration?.MutationScore,
                finalIteration?.CoverageStatus ?? MeasurementStatus.NotRun,
                finalIteration?.MutationStatus ?? MeasurementStatus.NotRun,
                finalIteration?.CoverageDetail ?? string.Empty,
                finalIteration?.MutationDetail ?? string.Empty,
                finalIteration?.CoverageReportPath ?? string.Empty,
                finalIteration?.MutationReportPath ?? string.Empty),
            Status = status,
            FailureCategory = failureCategory,
            Iterations = iterations,
            StopReason = stopReason,
            Stages = terminalStages,
            PromptAudit = terminalPromptAudit
        };

        Publish(
            runId,
            task,
            strategy,
            repetition,
            status,
            iterations.Count,
            totalTokens,
            totalChangedLines,
            regressions,
            stopReason);

        return result;
    }

    private static RepairStageEvidence BuildFailureStages(ExperimentExecutionException exception) =>
        exception.Category is RunFailureCategory.InvalidModelOutput or
            RunFailureCategory.ApiRateLimit or
            RunFailureCategory.ProviderUnavailable or
            RunFailureCategory.AuthenticationFailure or
            RunFailureCategory.NetworkFailure
            ? new RepairStageEvidence
            {
                LlmOutput = RepairStageOutcome.Failure(exception.Message)
            }
            : new RepairStageEvidence();

    private static string BuildCoreSuccessReason(RepairIterationResult iterationResult)
    {
        var oracle = iterationResult.BuggyOracleRequired
            ? "focal test passed on the fixed revision, reproduced the bug on the buggy revision, and introduced no regression"
            : "focal test passed and introduced no regression";
        var measurement = iterationResult.AdequacyVerified
            ? " Coverage and mutation measurement completed."
            : " Coverage or mutation measurement was incomplete; inspect the tool logs.";

        return $"Core repair verified: {oracle}.{measurement}";
    }

    private void Publish(
        Guid runId,
        ExperimentTask task,
        RepairStrategy strategy,
        int repetition,
        RunStatus status,
        int iteration,
        int totalTokens,
        int changedLines,
        int regressions,
        string message)
    {
        ProgressChanged?.Invoke(
            this,
            new ExperimentProgress(
                runId,
                task.TaskId,
                task.ScenarioId,
                strategy,
                repetition,
                status,
                iteration,
                totalTokens,
                changedLines,
                regressions,
                message));
    }
}
