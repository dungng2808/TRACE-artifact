using RQ2Experiment.Core.Abstractions;
using RQ2Experiment.Core.Domain;
using RQ2Experiment.Core.Engine;

namespace RQ2Experiment.Infrastructure.Diagnostics;

internal static class CoordinatorAcceptanceDiagnostic
{
    public static async Task<string> RunAsync(CancellationToken cancellationToken)
    {
        var executor = new SingleSuccessfulRepairExecutor();
        var repository = new InMemoryRunRepository();
        var coordinator = new ExperimentCoordinator(executor, repository);
        var task = new ExperimentTask(
            "PHASE-1-DIAGNOSTIC",
            "fixed",
            "S-PHASE-1",
            "T-PHASE-1",
            "generatedTest",
            "The repaired test must distinguish fixed and buggy behavior.",
            "S-PHASE-1 -> T-PHASE-1",
            "Initial test failure.",
            BuggyRepositoryPath: "buggy");
        var configuration = new ExperimentConfiguration(
            "diagnostic-model",
            MaxIterations: 4,
            TokenBudget: 10_000,
            Repetitions: 1,
            Strategies: [RepairStrategy.TraceabilityGuidedScenarioLocal]);

        var results = await coordinator.RunAsync([task], configuration, cancellationToken);
        var result = results.Single();

        if (result.Status != RunStatus.Success ||
            result.Iterations.Count != 1 ||
            executor.ExecuteCount != 1 ||
            !result.FinalBugReproduced)
        {
            throw new InvalidOperationException(
                "Phase 1 diagnostic failed: a core-valid repair with unavailable measurement " +
                "must stop after one iteration and be recorded as successful.");
        }

        if (result.Stages.LlmOutput.Status != RepairStageStatus.Succeeded ||
            result.Stages.Compilation.Status != RepairStageStatus.Succeeded ||
            result.Stages.BuggyOracle.Status != RepairStageStatus.Succeeded ||
            result.Stages.Coverage.Status != RepairStageStatus.Failed)
        {
            throw new InvalidOperationException("Repair-stage evidence was not preserved by the coordinator.");
        }

        if (!result.StopReason.Contains("measurement was incomplete", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Phase 1 diagnostic failed: the result must disclose incomplete measurement.");
        }

        var overBudgetSuccess = (await new ExperimentCoordinator(
                new OverBudgetSuccessfulRepairExecutor(),
                new InMemoryRunRepository())
            .RunAsync(
                [task],
                configuration with { TokenBudget = 100 },
                cancellationToken)).Single();
        if (overBudgetSuccess.Status != RunStatus.Success ||
            overBudgetSuccess.TotalTokens != 150 ||
            !overBudgetSuccess.StopReason.Contains("budget was exceeded", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Completed successful repairs must remain successful when their reported usage exceeds the budget.");
        }

        if (repository.Saved.Count != 1 || repository.Saved[0].RunId != result.RunId)
        {
            throw new InvalidOperationException("Phase 1 diagnostic failed: result persistence was not invoked.");
        }

        var infrastructureRepository = new InMemoryRunRepository();
        var infrastructureCoordinator = new ExperimentCoordinator(
            new ClassifiedInfrastructureFailureExecutor(),
            infrastructureRepository);
        var infrastructureResult = (await infrastructureCoordinator.RunAsync(
            [task],
            configuration,
            cancellationToken)).Single();
        if (infrastructureResult.Status != RunStatus.Failed ||
            infrastructureResult.FailureCategory != RunFailureCategory.ProviderUnavailable ||
            infrastructureResult.TotalApiCalls != 4 ||
            infrastructureResult.TotalApiRetries != 3 ||
            infrastructureResult.TotalKeyFailovers != 0 ||
            infrastructureResult.Iterations.Count != 0)
        {
            throw new InvalidOperationException(
                "Infrastructure failure diagnostic failed: classified API attempts were not preserved.");
        }

        var accountedFailure = (await new ExperimentCoordinator(
                new AccountedInvalidOutputExecutor(usageReported: true),
                new InMemoryRunRepository())
            .RunAsync([task], configuration, cancellationToken)).Single();
        if (accountedFailure.FailureCategory != RunFailureCategory.InvalidModelOutput ||
            accountedFailure.TotalInputTokens != 120 ||
            accountedFailure.TotalOutputTokens != 30 ||
            accountedFailure.TotalTokens != 150 ||
            accountedFailure.TotalCachedInputTokens != 20 ||
            accountedFailure.TotalReasoningTokens != 5 ||
            accountedFailure.TotalApiCalls != 1 ||
            !accountedFailure.TokenAccountingComplete ||
            accountedFailure.UnreportedUsageResponses != 0)
        {
            throw new InvalidOperationException(
                "Failed-response accounting diagnostic lost reported provider usage.");
        }
        if (accountedFailure.Stages.LlmOutput.Status != RepairStageStatus.Failed)
        {
            throw new InvalidOperationException("Invalid model output was not classified in the failure funnel.");
        }

        var unreportedFailure = (await new ExperimentCoordinator(
                new AccountedInvalidOutputExecutor(usageReported: false),
                new InMemoryRunRepository())
            .RunAsync([task], configuration, cancellationToken)).Single();
        if (unreportedFailure.TokenAccountingComplete ||
            unreportedFailure.UnreportedUsageResponses != 1 ||
            unreportedFailure.TotalApiCalls != 1)
        {
            throw new InvalidOperationException(
                "Missing-usage diagnostic did not distinguish unavailable usage from zero tokens.");
        }

        if (string.IsNullOrWhiteSpace(result.BatchId) ||
            result.BatchId == infrastructureResult.BatchId ||
            result.ModelName != configuration.ModelName ||
            result.ProviderName != configuration.ProviderName ||
            result.ScheduleSeed != configuration.ScheduleSeed ||
            result.ScheduleIndex != 1 ||
            result.TreatmentPosition != 1 ||
            result.ExpectedBatchRuns != 1 ||
            string.IsNullOrWhiteSpace(result.BlockId) ||
            !ExperimentOutcomePolicy.IsAnalysisEligible(result) ||
            ExperimentOutcomePolicy.IsAnalysisEligible(infrastructureResult) ||
            !ExperimentOutcomePolicy.IsInfrastructureFailure(infrastructureResult))
        {
            throw new InvalidOperationException(
                "Experiment reporting diagnostic failed: batch provenance or analysis eligibility is invalid.");
        }

        var latestBatch = ExperimentResultBatchSelector.SelectLatest([result, infrastructureResult]);
        if (latestBatch.Count != 1 || latestBatch[0].BatchId != infrastructureResult.BatchId)
        {
            throw new InvalidOperationException(
                "Experiment reporting diagnostic failed: latest batch selection mixed independent runs.");
        }

        return "Core acceptance, complete failed-response token/API accounting, batch provenance, and analysis eligibility: OK";
    }

    private sealed class SingleSuccessfulRepairExecutor : IRepairExecutor
    {
        public int ExecuteCount { get; private set; }

        public Task<IRepairSession> CreateSessionAsync(
            Guid runId,
            ExperimentTask task,
            RepairStrategy strategy,
            int repetition,
            ExperimentConfiguration configuration,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IRepairSession>(new Session(this));
        }

        private sealed class Session(SingleSuccessfulRepairExecutor owner) : IRepairSession
        {
            public Task<RepairIterationResult> ExecuteIterationAsync(
                int iteration,
                CancellationToken cancellationToken)
            {
                owner.ExecuteCount++;
                return Task.FromResult(new RepairIterationResult
                {
                    Tokens = new TokenUsage(InputTokens: 100, OutputTokens: 50),
                    Api = new ApiUsage(Calls: 1),
                    Verification = new RepairVerification(
                        ChangedLines: 4,
                        TouchedMethods: 1,
                        RegressedTests: 0,
                        FocalTestPassed: true,
                        AdequacyVerified: false,
                        BuggyOracleRequired: true,
                        BugReproduced: true),
                    Quality = new QualityMeasurement(
                        CoverageStatus: MeasurementStatus.ToolFailed,
                        MutationStatus: MeasurementStatus.ToolFailed),
                    Stages = new RepairStageEvidence
                    {
                        LlmOutput = RepairStageOutcome.Success(),
                        SourceApplication = RepairStageOutcome.Success(),
                        Compilation = RepairStageOutcome.Success(),
                        FocalTest = RepairStageOutcome.Success(),
                        BuggyOracle = RepairStageOutcome.Success(),
                        Regression = RepairStageOutcome.Success(),
                        Coverage = RepairStageOutcome.Failure("tool failed"),
                        Mutation = RepairStageOutcome.Failure("tool failed")
                    },
                    Summary = "Core oracle passed; measurement unavailable."
                });
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class InMemoryRunRepository : IRunRepository
    {
        public List<ExperimentRunResult> Saved { get; } = [];

        public Task SaveAsync(ExperimentRunResult result, CancellationToken cancellationToken)
        {
            Saved.Add(result);
            return Task.CompletedTask;
        }
    }

    private sealed class OverBudgetSuccessfulRepairExecutor : IRepairExecutor
    {
        public Task<IRepairSession> CreateSessionAsync(
            Guid runId,
            ExperimentTask task,
            RepairStrategy strategy,
            int repetition,
            ExperimentConfiguration configuration,
            CancellationToken cancellationToken) => Task.FromResult<IRepairSession>(new Session());

        private sealed class Session : IRepairSession
        {
            public Task<RepairIterationResult> ExecuteIterationAsync(
                int iteration,
                CancellationToken cancellationToken) => Task.FromResult(new RepairIterationResult
                {
                    Tokens = new TokenUsage(InputTokens: 100, OutputTokens: 50),
                    Api = new ApiUsage(Calls: 1),
                    Verification = new RepairVerification(
                        ChangedLines: 1,
                        TouchedMethods: 1,
                        RegressedTests: 0,
                        FocalTestPassed: true,
                        AdequacyVerified: false,
                        BuggyOracleRequired: true,
                        BugReproduced: true),
                    Stages = new RepairStageEvidence
                    {
                        LlmOutput = RepairStageOutcome.Success(),
                        SourceApplication = RepairStageOutcome.Success(),
                        Compilation = RepairStageOutcome.Success(),
                        FocalTest = RepairStageOutcome.Success(),
                        BuggyOracle = RepairStageOutcome.Success(),
                        Regression = RepairStageOutcome.Success()
                    }
                });

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class ClassifiedInfrastructureFailureExecutor : IRepairExecutor
    {
        public Task<IRepairSession> CreateSessionAsync(
            Guid runId,
            ExperimentTask task,
            RepairStrategy strategy,
            int repetition,
            ExperimentConfiguration configuration,
            CancellationToken cancellationToken) => Task.FromResult<IRepairSession>(new Session());

        private sealed class Session : IRepairSession
        {
            public Task<RepairIterationResult> ExecuteIterationAsync(
                int iteration,
                CancellationToken cancellationToken) => throw new ExperimentExecutionException(
                    RunFailureCategory.ProviderUnavailable,
                    "Provider unavailable after retries.",
                    apiCalls: 4,
                    apiRetries: 3,
                    keyFailovers: 0,
                    apiKeyFingerprint: "ABCDEF12");

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class AccountedInvalidOutputExecutor(bool usageReported) : IRepairExecutor
    {
        public Task<IRepairSession> CreateSessionAsync(
            Guid runId,
            ExperimentTask task,
            RepairStrategy strategy,
            int repetition,
            ExperimentConfiguration configuration,
            CancellationToken cancellationToken) => Task.FromResult<IRepairSession>(new Session(usageReported));

        private sealed class Session(bool usageReported) : IRepairSession
        {
            public Task<RepairIterationResult> ExecuteIterationAsync(
                int iteration,
                CancellationToken cancellationToken) => throw new ExperimentExecutionException(
                    RunFailureCategory.InvalidModelOutput,
                    "Repair JSON did not match the schema.",
                    apiCalls: 1,
                    apiKeyFingerprint: "ABCDEF12",
                    inputTokens: usageReported ? 120 : 0,
                    outputTokens: usageReported ? 30 : 0,
                    cachedInputTokens: usageReported ? 20 : 0,
                    reasoningTokens: usageReported ? 5 : 0,
                    unreportedUsageResponses: usageReported ? 0 : 1);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
