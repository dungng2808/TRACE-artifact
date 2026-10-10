using System.Text;
using System.Text.Json;
using RQ2Experiment.Core.Abstractions;
using RQ2Experiment.Core.Domain;
using RQ2Experiment.Infrastructure.Execution;
using RQ2Experiment.Infrastructure.OpenAI;

namespace RQ2Experiment.Infrastructure.Real;

public sealed class RealRepairExecutor(
    ILlmRepairClient llmClient,
    string outputRoot) : IRepairExecutor
{
    public async Task<IRepairSession> CreateSessionAsync(
        Guid runId,
        ExperimentTask task,
        RepairStrategy strategy,
        int repetition,
        ExperimentConfiguration configuration,
        CancellationToken cancellationToken)
    {
        task.ValidateForRealExecution();
        var runDirectory = Path.Combine(Path.GetFullPath(outputRoot), "runs", runId.ToString("N"));
        var workspace = Path.Combine(runDirectory, "workspace");
        var buggyWorkspace = task.RequiresBuggyOracle
            ? Path.Combine(runDirectory, "buggy-workspace")
            : null;
        var artifacts = Path.Combine(runDirectory, "artifacts");
        Directory.CreateDirectory(artifacts);
        await WorkspaceCopier.CopyAsync(task.RepositoryPath, workspace, outputRoot, cancellationToken);
        if (buggyWorkspace is not null)
        {
            await WorkspaceCopier.CopyAsync(
                task.BuggyRepositoryPath,
                buggyWorkspace,
                outputRoot,
                cancellationToken);
        }

        var metadata = new
        {
            runId,
            task.TaskId,
            task.ScenarioId,
            strategy = strategy.ToString(),
            repetition,
            configuration.ModelName,
            configuration.ReasoningEffort,
            configuration.ProviderName,
            configuration.ApiProtocol,
            treatmentContractVersion = TreatmentPromptBuilder.ContractVersion,
            contextPolicy = strategy == RepairStrategy.FailingTestMethod
                ? "failure-evidence-only"
                : "scenario+traceability",
            editScope = strategy == RepairStrategy.FullClassRegeneration
                ? "full-test-class"
                : "focal-method",
            apiKeyFingerprints = llmClient.KeyFingerprints,
            sourceRepository = Path.GetFullPath(task.RepositoryPath),
            buggyRepository = task.RequiresBuggyOracle
                ? Path.GetFullPath(task.BuggyRepositoryPath)
                : null,
            workspace,
            buggyWorkspace,
            createdAt = DateTimeOffset.UtcNow
        };
        await File.WriteAllTextAsync(
            Path.Combine(artifacts, "run-metadata.json"),
            JsonSerializer.Serialize(metadata, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }),
            cancellationToken);

        return new RealRepairSession(
            task,
            strategy,
            configuration,
            llmClient,
            workspace,
            buggyWorkspace,
            artifacts);
    }

    private sealed class RealRepairSession : IRepairSession
    {
        private readonly ExperimentTask task;
        private readonly RepairStrategy strategy;
        private readonly ExperimentConfiguration configuration;
        private readonly ILlmRepairClient llmClient;
        private readonly string workspace;
        private readonly string? buggyWorkspace;
        private readonly string artifacts;
        private readonly string testFilePath;
        private readonly string? buggyTestFilePath;
        private readonly string originalTestSource;
        private readonly JavaBuildRunner buildRunner;
        private readonly JavaBuildRunner? buggyBuildRunner;
        private string previousEvidence;

        public RealRepairSession(
            ExperimentTask task,
            RepairStrategy strategy,
            ExperimentConfiguration configuration,
            ILlmRepairClient llmClient,
            string workspace,
            string? buggyWorkspace,
            string artifacts)
        {
            this.task = task;
            this.strategy = strategy;
            this.configuration = configuration;
            this.llmClient = llmClient;
            this.workspace = workspace;
            this.buggyWorkspace = buggyWorkspace;
            this.artifacts = artifacts;
            testFilePath = JavaBuildRunner.ResolveWithin(workspace, task.TestFilePath);
            if (!File.Exists(testFilePath))
            {
                throw new FileNotFoundException("Generated test file was not found in the run workspace.", testFilePath);
            }

            originalTestSource = File.ReadAllText(testFilePath);
            previousEvidence = task.InitialEvidence;
            buildRunner = new JavaBuildRunner(task, workspace, artifacts);
            if (buggyWorkspace is not null)
            {
                buggyTestFilePath = JavaBuildRunner.ResolveWithin(buggyWorkspace, task.TestFilePath);
                if (!File.Exists(buggyTestFilePath))
                {
                    throw new FileNotFoundException(
                        "Matching test file was not found in the buggy run workspace.",
                        buggyTestFilePath);
                }

                buggyBuildRunner = new JavaBuildRunner(task, buggyWorkspace, artifacts);
            }
        }

        public async Task<RepairIterationResult> ExecuteIterationAsync(
            int iteration,
            CancellationToken cancellationToken)
        {
            var currentTestSource = await File.ReadAllTextAsync(testFilePath, cancellationToken);
            var auditArtifact = $"iteration-{iteration:00}-prompt-audit.json";
            var promptBuild = BuildPrompt(currentTestSource, auditArtifact);
            await File.WriteAllTextAsync(
                Path.Combine(artifacts, auditArtifact),
                JsonSerializer.Serialize(
                    promptBuild.Audit,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }),
                cancellationToken);
            if (!promptBuild.Audit.ContractSatisfied)
            {
                throw new ExperimentExecutionException(
                    RunFailureCategory.TreatmentIsolationViolation,
                    promptBuild.Audit.Violation,
                    promptAuditEvidence: promptBuild.Audit);
            }

            LlmRepairResponse response;
            try
            {
                response = await llmClient.GenerateRepairAsync(
                    configuration.ModelName,
                    configuration.ReasoningEffort,
                    PromptInstructions,
                    promptBuild.Prompt,
                    artifacts,
                    iteration,
                    cancellationToken);
            }
            catch (ExperimentExecutionException exception)
            {
                throw WithPromptAudit(exception, promptBuild.Audit);
            }
            var stages = new RepairStageEvidence
            {
                LlmOutput = RepairStageOutcome.Success(
                    response.FirstPassJsonValid ? "Valid first-pass JSON." : $"Normalized via {response.OutputNormalization}.")
            };

            string repairedSource;
            try
            {
                repairedSource = strategy == RepairStrategy.FullClassRegeneration
                    ? response.ReplacementSource.Trim()
                    : JavaMethodEditor.ReplaceMethod(currentTestSource, task.FailingTestMethod, response.ReplacementSource);
                if (repairedSource.Contains("```", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("The generated Java source contains Markdown fences.");
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new ExperimentExecutionException(
                    RunFailureCategory.InvalidModelOutput,
                    $"Generated source could not be applied: {exception.Message}",
                    exception,
                    response.ApiCalls,
                    response.RetryCount,
                    response.KeyFailoverCount,
                    response.ApiKeyFingerprint,
                    response.InputTokens,
                    response.OutputTokens,
                    response.CachedInputTokens,
                    response.CacheWriteInputTokens,
                    response.ReasoningTokens,
                    response.TokenUsageReported ? 0 : 1,
                    stages with
                    {
                        SourceApplication = RepairStageOutcome.Failure(exception.Message)
                    },
                    promptBuild.Audit);
            }

            await File.WriteAllTextAsync(
                Path.Combine(artifacts, $"iteration-{iteration:00}-before.java"),
                currentTestSource,
                cancellationToken);
            await File.WriteAllTextAsync(testFilePath, repairedSource, cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(artifacts, $"iteration-{iteration:00}-after.java"),
                repairedSource,
                cancellationToken);
            stages = stages with
            {
                SourceApplication = RepairStageOutcome.Success("Generated source written to the isolated workspace.")
            };

            var changedLines = JavaMethodEditor.CountChangedLines(originalTestSource, repairedSource);
            var touchedMethods = strategy == RepairStrategy.FullClassRegeneration
                ? Math.Max(1, JavaMethodEditor.CountTestMethods(repairedSource))
                : 1;

            if (strategy == RepairStrategy.FullClassRegeneration)
            {
                var conformance = JavaTestClassConformance.Analyze(
                    originalTestSource,
                    repairedSource,
                    task.FailingTestMethod);
                if (!conformance.IsConformant)
                {
                    previousEvidence = conformance.BuildSummary() +
                                       " Regenerate from the original class and change only the focal test behavior.";
                    await File.WriteAllTextAsync(testFilePath, originalTestSource, cancellationToken);
                    stages = stages with
                    {
                        SourceApplication = RepairStageOutcome.Failure(conformance.BuildSummary())
                    };
                    return Result(
                        response,
                        changedLines,
                        touchedMethods,
                        false,
                        conformance.ViolationCount,
                        null,
                        null,
                        previousEvidence,
                        false,
                        task.RequiresBuggyOracle,
                        false,
                        stages,
                        promptBuild.Audit);
                }
            }

            var compile = await buildRunner.CompileAsync(iteration, cancellationToken);
            if (!compile.Succeeded)
            {
                previousEvidence = Truncate(compile.CombinedOutput);
                stages = stages with
                {
                    Compilation = RepairStageOutcome.Failure("Fixed-revision compilation failed.")
                };
                return Result(response, changedLines, touchedMethods, false, 0, null, null,
                    "Compilation failed; feedback captured for the next iteration.", false,
                    task.RequiresBuggyOracle, false, stages, promptBuild.Audit);
            }
            stages = stages with { Compilation = RepairStageOutcome.Success() };

            var focal = await buildRunner.RunFocalTestAsync(iteration, cancellationToken);
            if (!focal.Succeeded)
            {
                previousEvidence = Truncate(focal.CombinedOutput);
                stages = stages with
                {
                    FocalTest = RepairStageOutcome.Failure("Focal test failed on the fixed revision.")
                };
                return Result(response, changedLines, touchedMethods, false, 0, null, null,
                    "Focal test failed; feedback captured for the next iteration.", false,
                    task.RequiresBuggyOracle, false, stages, promptBuild.Audit);
            }
            stages = stages with { FocalTest = RepairStageOutcome.Success() };

            var bugReproduced = false;
            if (buggyBuildRunner is not null && buggyTestFilePath is not null)
            {
                await File.WriteAllTextAsync(buggyTestFilePath, repairedSource, cancellationToken);
                var buggyCompile = await buggyBuildRunner.CompileAsync(iteration, cancellationToken);
                if (!buggyCompile.Succeeded)
                {
                    previousEvidence =
                        "The repaired test passes the fixed revision but does not compile against the buggy revision. " +
                        "Preserve API compatibility across both revisions.\n" +
                        Truncate(buggyCompile.CombinedOutput);
                    stages = stages with
                    {
                        BuggyOracle = RepairStageOutcome.Failure("Repaired test did not compile on the buggy revision.")
                    };
                    return Result(response, changedLines, touchedMethods, true, 0, null, null,
                        "Fixed revision passed, but buggy-revision compilation failed.", false,
                        true, false, stages, promptBuild.Audit);
                }

                var buggyFocal = await buggyBuildRunner.RunFocalTestAsync(iteration, cancellationToken);
                if (buggyFocal.Succeeded)
                {
                    previousEvidence =
                        "The repaired test passes both fixed and buggy revisions. Strengthen the oracle so it " +
                        "passes only the fixed behavior and still exposes the original bug. Do not add knowledge " +
                        "of implementation-specific patch details.";
                    stages = stages with
                    {
                        BuggyOracle = RepairStageOutcome.Failure("Test also passed on the buggy revision.")
                    };
                    return Result(response, changedLines, touchedMethods, true, 0, null, null,
                        "Test also passed on the buggy revision; bug-reproduction oracle rejected.", false,
                        true, false, stages, promptBuild.Audit);
                }

                bugReproduced = true;
                stages = stages with { BuggyOracle = RepairStageOutcome.Success() };
            }
            else
            {
                stages = stages with
                {
                    BuggyOracle = RepairStageOutcome.Skip("No buggy-revision oracle configured.")
                };
            }

            var regression = await buildRunner.RunRegressionAsync(iteration, cancellationToken);
            if (regression.RegressionCount > 0)
            {
                previousEvidence = regression.Summary;
                stages = stages with { Regression = RepairStageOutcome.Failure(regression.Summary) };
                return Result(response, changedLines, touchedMethods, true, regression.RegressionCount, null, null,
                    regression.Summary, false, task.RequiresBuggyOracle, bugReproduced, stages, promptBuild.Audit);
            }
            stages = stages with { Regression = RepairStageOutcome.Success() };

            var coverage = await buildRunner.RunCoverageAsync(iteration, cancellationToken);
            var mutation = await buildRunner.RunMutationAsync(iteration, cancellationToken);
            stages = stages with
            {
                Coverage = MeasurementStage(coverage.Status, coverage.Detail),
                Mutation = MeasurementStage(mutation.Status, mutation.Detail)
            };
            var adequacyVerified = coverage.Status == MeasurementStatus.Succeeded &&
                                   mutation.Status == MeasurementStatus.Succeeded;
            previousEvidence = adequacyVerified
                ? "Full verification passed."
                : "Coverage or mutation report was unavailable; inspect tool logs.";
            var summary = adequacyVerified
                ? task.RequiresBuggyOracle
                    ? $"Verified repair and bug reproduction. Coverage={coverage.Value!.Value:P1}; mutation={mutation.Value!.Value:P1}."
                    : $"Verified repair. Coverage={coverage.Value!.Value:P1}; mutation={mutation.Value!.Value:P1}."
                : $"Tests passed; measurement incomplete (coverage={coverage.Status}: {coverage.Detail}; " +
                  $"mutation={mutation.Status}: {mutation.Detail}).";

            return Result(
                response,
                changedLines,
                touchedMethods,
                true,
                0,
                coverage.Value,
                mutation.Value,
                summary,
                adequacyVerified,
                task.RequiresBuggyOracle,
                bugReproduced,
                stages,
                promptBuild.Audit,
                coverage.Status,
                mutation.Status,
                coverage.Detail,
                mutation.Detail,
                coverage.ReportPath,
                mutation.ReportPath);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private TreatmentPromptBuild BuildPrompt(string currentTestSource, string auditArtifact) =>
            TreatmentPromptBuilder.Build(
                task,
                strategy,
                currentTestSource,
                previousEvidence,
                ReadFocalSources(),
                PromptInstructions,
                auditArtifact);

        private string ReadFocalSources()
        {
            var builder = new StringBuilder();
            foreach (var relativePath in task.FocalSources)
            {
                var path = JavaBuildRunner.ResolveWithin(workspace, relativePath);
                if (!File.Exists(path)) continue;
                builder.AppendLine($"--- {relativePath} ---");
                builder.AppendLine(File.ReadAllText(path));
            }

            const int maximumCharacters = 120_000;
            return builder.Length <= maximumCharacters
                ? builder.ToString()
                : builder.ToString(0, maximumCharacters) + Environment.NewLine + "[source context truncated]";
        }

        private static RepairIterationResult Result(
            LlmRepairResponse response,
            int changedLines,
            int touchedMethods,
            bool focalPassed,
            int regressions,
            double? coverage,
            double? mutation,
            string summary,
            bool adequacyVerified,
            bool buggyOracleRequired,
            bool bugReproduced,
            RepairStageEvidence stages,
            PromptAuditEvidence promptAudit,
            MeasurementStatus coverageStatus = MeasurementStatus.NotRun,
            MeasurementStatus mutationStatus = MeasurementStatus.NotRun,
            string coverageDetail = "",
            string mutationDetail = "",
            string coverageReportPath = "",
            string mutationReportPath = "") => new()
            {
                Tokens = new TokenUsage(
                    response.InputTokens,
                    response.OutputTokens,
                    response.CachedInputTokens,
                    response.CacheWriteInputTokens,
                    response.ReasoningTokens,
                    UsageReported: response.TokenUsageReported),
                Api = new ApiUsage(
                    response.ApiCalls,
                    response.RetryCount,
                    response.KeyFailoverCount,
                    response.ApiKeyFingerprint),
                Verification = new RepairVerification(
                    changedLines,
                    touchedMethods,
                    regressions,
                    focalPassed,
                    adequacyVerified,
                    buggyOracleRequired,
                    bugReproduced),
                Quality = new QualityMeasurement(
                    coverage,
                    mutation,
                    coverageStatus,
                    mutationStatus,
                    coverageDetail,
                    mutationDetail,
                    coverageReportPath,
                    mutationReportPath),
                Output = new LlmOutputEvidence(
                    response.FirstPassJsonValid,
                    response.OutputNormalization),
                Stages = stages,
                PromptAudit = promptAudit,
                Summary = $"{summary} Model={response.ActualModel}; response={response.ResponseId}; " +
                    $"key={response.ApiKeyFingerprint}; retries={response.RetryCount}; failovers={response.KeyFailoverCount}; " +
                    $"output={(response.FirstPassJsonValid ? "first-pass" : response.OutputNormalization)}."
            };

        private static ExperimentExecutionException WithPromptAudit(
            ExperimentExecutionException exception,
            PromptAuditEvidence promptAudit) => new(
                exception.Category,
                exception.Message,
                exception,
                exception.ApiCalls,
                exception.ApiRetries,
                exception.KeyFailovers,
                exception.ApiKeyFingerprint,
                exception.InputTokens,
                exception.OutputTokens,
                exception.CachedInputTokens,
                exception.CacheWriteInputTokens,
                exception.ReasoningTokens,
                exception.UnreportedUsageResponses,
                exception.StageEvidence,
                promptAudit);

        private static RepairStageOutcome MeasurementStage(MeasurementStatus status, string detail) => status switch
        {
            MeasurementStatus.Succeeded => RepairStageOutcome.Success(detail),
            MeasurementStatus.NotRun => new RepairStageOutcome(),
            _ => RepairStageOutcome.Failure(string.IsNullOrWhiteSpace(detail) ? status.ToString() : detail)
        };

        private static string Truncate(string value) =>
            value.Length <= 12_000 ? value : value[^12_000..];

        private const string PromptInstructions = """
            You repair generated Java tests. Return only the JSON object required by the response schema.
            Preserve behavioral intent and assertion strength. Never delete, disable, ignore, or weaken a test merely to make it pass.
            Do not modify production code. Do not add sleeps, empty catches, unconditional assertions, or exception swallowing.
            Output valid compilable Java source in replacementSource without Markdown fences.

            Treat [TOOL_EVIDENCE] as authoritative feedback from the previous attempt. If it contains a compiler error,
            fix compilation before changing test semantics: use only APIs available to the target revision, preserve the
            target method signature and checked exceptions, and do not invent imports, helper methods, or symbols that
            are absent from the supplied test/production context. If it contains a focal-test or buggy-oracle failure,
            repair the assertion/oracle while keeping the test valid on both revisions. Before returning, mentally check
            that replacementSource can be inserted exactly into the declared edit target without a class wrapper, Markdown,
            duplicate imports, or references outside the supplied context.
            """;
    }
}
