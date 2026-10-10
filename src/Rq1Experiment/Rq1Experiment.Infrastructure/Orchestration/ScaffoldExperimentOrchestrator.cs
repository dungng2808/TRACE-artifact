using System.Text.Json;
using Rq1Experiment.Contracts.Config;
using Rq1Experiment.Contracts.Experiments;
using Rq1Experiment.Contracts.Generation;
using Rq1Experiment.Contracts.Prompts;
using Rq1Experiment.Contracts.Subjects;
using Rq1Experiment.Contracts.Targets;
using Rq1Experiment.Core.Abstractions;
using Rq1Experiment.Infrastructure.Persistence;

namespace Rq1Experiment.Infrastructure.Orchestration;

/// <summary>
/// RQ1 orchestration pipeline. Implemented stages are backed by evidence-producing
/// services; future experiment/analysis stages remain explicit boundaries.
/// </summary>
public sealed class ScaffoldExperimentOrchestrator(
    WorkspaceLayout layout,
    IApiKeyPool? apiKeyPool = null,
    IRepositoryPreflightService? preflightService = null,
    ISubjectSelectionService? subjectSelectionService = null,
    ISmokeSubjectService? smokeSubjectService = null,
    IStructuralTargetService? structuralTargetService = null,
    IRepositoryContextPromptService? repositoryContextPromptService = null,
    IDirectBaselineService? directBaselineService = null,
    IPlannedPipelineService? plannedPipelineService = null,
    IToolExecutionService? toolExecutionService = null,
    IMetricsLedgerService? metricsLedgerService = null,
    IRunManagementService? runManagementService = null) : IExperimentOrchestrator
{
    private static readonly PipelineStageDefinition[] Stages =
    [
        new("preflight", "Environment preflight"),
        new("subjects", "Subject manifest"),
        new("targets", "Independent targets"),
        new("context-prompts", "Repository context / prompts"),
        new("direct", "DIRECT baseline"),
        new("planned", "PLANNED pipeline"),
        new("compile-test", "Compile / JUnit"),
        new("adequacy", "JaCoCo / PIT"),
        new("ledger", "Run ledger"),
        new("analysis", "Statistical analysis")
    ];

    private volatile bool _pauseRequested;
    private int _nextStageIndex;
    private bool _hasCheckpoint;

    public bool IsRunning { get; private set; }
    public bool CanResume => !IsRunning
                             && ((_hasCheckpoint && _nextStageIndex < Stages.Length)
                                 || HasResumableCheckpointOnDisk());

    public async Task<PipelineRunResult> RunAsync(
        ExperimentConfig config,
        IProgress<PipelineProgress> progress,
        CancellationToken cancellationToken)
    {
        _nextStageIndex = 0;
        _pauseRequested = false;
        _hasCheckpoint = false;
        return await ExecuteAsync(config, progress, cancellationToken);
    }

    public void PauseAfterCurrentStage() => _pauseRequested = true;

    public async Task<PipelineRunResult> ResumeAsync(
        ExperimentConfig config,
        IProgress<PipelineProgress> progress,
        CancellationToken cancellationToken)
    {
        if (_nextStageIndex == 0 || _nextStageIndex >= Stages.Length)
            await RestoreCheckpointAsync(config, cancellationToken);

        if (_nextStageIndex > 4 && HasMissingRuns(config))
        {
            _nextStageIndex = 4; // Start from "direct" generation stage
        }

        _pauseRequested = false;
        return await ExecuteAsync(config, progress, cancellationToken);
    }

    private bool HasMissingRuns(ExperimentConfig config)
    {
        try
        {
            var outputRoot = layout.ResolveOutputRoot(config.OutputRoot);
            var runsRoot = Path.Combine(outputRoot, "runs", config.ExperimentId);
            if (!Directory.Exists(runsRoot)) return false;
            foreach (var subjectDir in Directory.GetDirectories(runsRoot))
            {
                var directDir = Path.Combine(subjectDir, "DIRECT", "R01");
                if (Directory.Exists(directDir) && !File.Exists(Path.Combine(directDir, "run.json")))
                    return true;

                var plannedDir = Path.Combine(subjectDir, "PLANNED", "R01");
                if (Directory.Exists(plannedDir) && !File.Exists(Path.Combine(plannedDir, "run.json")))
                    return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    public async Task<PipelineRunResult> RunFromStageAsync(
        ExperimentConfig config,
        string stageId,
        IProgress<PipelineProgress> progress,
        CancellationToken cancellationToken)
    {
        var stageIndex = Array.FindIndex(Stages, s => s.Id.Equals(stageId, StringComparison.OrdinalIgnoreCase));
        if (stageIndex < 0) stageIndex = 0;
        _nextStageIndex = stageIndex;
        _pauseRequested = false;
        _hasCheckpoint = true;
        return await ExecuteAsync(config, progress, cancellationToken);
    }

    public async Task<PipelineRunResult> RetryFailedRunsAsync(
        ExperimentConfig config,
        IProgress<PipelineProgress> progress,
        CancellationToken cancellationToken,
        bool onlyTokenFailures = false)
    {
        var runManager = runManagementService ?? new RunManagementService();
        var outputRoot = layout.ResolveOutputRoot(config.OutputRoot);
        await runManager.ResetFailedRunsAsync(
            new ResetRunsRequest(outputRoot, config.ExperimentId, OnlyTokenFailures: onlyTokenFailures),
            cancellationToken);

        return await RunFromStageAsync(config, "direct", progress, cancellationToken);
    }

    private async Task<PipelineRunResult> ExecuteAsync(
        ExperimentConfig config,
        IProgress<PipelineProgress> progress,
        CancellationToken cancellationToken)
    {
        if (IsRunning)
            throw new InvalidOperationException("The experiment pipeline is already running.");

        IsRunning = true;
        try
        {
            for (var index = _nextStageIndex; index < Stages.Length; index++)
            {
                var stage = Stages[index];

                if (stage.Id == "preflight" && preflightService is not null)
                {
                    var preflightResult = await ExecutePreflightAsync(
                        config,
                        stage,
                        index,
                        progress,
                        cancellationToken);
                    if (preflightResult is not null)
                        return preflightResult;
                    continue;
                }

                if (stage.Id == "subjects" && subjectSelectionService is not null)
                {
                    var selectionResult = await ExecuteSubjectSelectionAsync(
                        config, stage, index, progress, cancellationToken);
                    if (selectionResult is not null) return selectionResult;
                    continue;
                }

                if (stage.Id == "targets" && structuralTargetService is not null)
                {
                    var targetResult = await ExecuteStructuralTargetsAsync(
                        config, stage, index, progress, cancellationToken);
                    if (targetResult is not null) return targetResult;
                    continue;
                }

                if (stage.Id == "context-prompts" && repositoryContextPromptService is not null)
                {
                    var contextResult = await ExecuteRepositoryContextPromptsAsync(
                        config, stage, index, progress, cancellationToken);
                    if (contextResult is not null) return contextResult;
                    continue;
                }

                if (stage.Id == "direct" && directBaselineService is not null)
                {
                    var directResult = await ExecuteDirectBaselineAsync(
                        config, stage, index, progress, cancellationToken);
                    if (directResult is not null) return directResult;
                    continue;
                }

                if (stage.Id == "planned" && plannedPipelineService is not null)
                {
                    var plannedResult = await ExecutePlannedPipelineAsync(
                        config, stage, index, progress, cancellationToken);
                    if (plannedResult is not null) return plannedResult;
                    continue;
                }

                if (stage.Id == "compile-test" && toolExecutionService is not null)
                {
                    var toolResult = await ExecuteCompileTestAsync(config, stage, index, progress, cancellationToken);
                    if (toolResult is not null) return toolResult;
                    continue;
                }

                if (stage.Id == "adequacy" && toolExecutionService is not null)
                {
                    var adequacyResult = await ExecuteAdequacyAsync(config, stage, index, progress, cancellationToken);
                    if (adequacyResult is not null) return adequacyResult;
                    continue;
                }

                if (stage.Id == "ledger" && metricsLedgerService is not null)
                {
                    var ledgerResult = await ExecuteLedgerAsync(config, stage, index, progress, cancellationToken);
                    if (ledgerResult is not null) return ledgerResult;
                    continue;
                }

                if ((stage.Id == "direct" || stage.Id == "planned") && apiKeyPool is not null)
                {
                    var lease = await apiKeyPool.AcquireAsync(config.Llm.Provider, cancellationToken);
                    if (lease is null)
                    {
                        _nextStageIndex = index;
                        await SaveCheckpointAsync(config, cancellationToken);
                        Report(
                            progress,
                            stage,
                            PipelineStageState.WaitingForApiKey,
                            index,
                            $"No usable {config.Llm.Provider} key. Add a key and resume the same request.");
                        return new PipelineRunResult(
                            false,
                            false,
                            "WAITING_FOR_API_KEY: add a key for the selected provider, then Resume.",
                            DateTimeOffset.UtcNow);
                    }
                }

                Report(progress, stage, PipelineStageState.Running, index, $"Running {stage.DisplayName}...");

                await Task.Delay(TimeSpan.FromMilliseconds(350), cancellationToken);
                await WriteStageEvidenceAsync(config, stage, cancellationToken);

                _nextStageIndex = index + 1;
                await SaveCheckpointAsync(config, cancellationToken);
                Report(progress, stage, PipelineStageState.Passed, _nextStageIndex, $"{stage.DisplayName} completed.");

                if (_pauseRequested && _nextStageIndex < Stages.Length)
                {
                    return new PipelineRunResult(
                        false,
                        false,
                        "Paused after the current stage.",
                        DateTimeOffset.UtcNow);
                }
            }

            await WriteDummyRunContractAsync(config, cancellationToken);
            return new PipelineRunResult(true, false, "Scaffold pipeline completed.", DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            await SaveCheckpointAsync(config, CancellationToken.None);
            return new PipelineRunResult(false, true, "Pipeline cancelled; checkpoint saved.", DateTimeOffset.UtcNow);
        }
        finally
        {
            IsRunning = false;
        }
    }

    private async Task<PipelineRunResult?> ExecutePreflightAsync(
        ExperimentConfig config,
        PipelineStageDefinition stage,
        int stageIndex,
        IProgress<PipelineProgress> progress,
        CancellationToken cancellationToken)
    {
        Report(progress, stage, PipelineStageState.Running, stageIndex, "Running repository preflight...");
        var projectId = Path.GetFileName(Path.TrimEndingDirectorySeparator(config.RepositoryRoot));
        if (string.IsNullOrWhiteSpace(projectId))
            projectId = config.ExperimentId;

        var request = new PreflightRequest(
            projectId,
            config.RepositoryRoot,
            layout.ResolveOutputRoot(config.OutputRoot),
            config.Tools.CompileTimeoutSeconds,
            config.Tools.TestTimeoutSeconds,
            config.Tools.JacocoTimeoutSeconds,
            config.Tools.PitTimeoutSeconds,
            config.Tools.JacocoVersion,
            config.Tools.PitVersion,
            config.Tools.JavaHome,
            config.Tools.ModulePath);
        var preflightProgress = new InlineProgress<PreflightProgress>(item =>
            Report(
                progress,
                stage,
                PipelineStageState.Running,
                stageIndex,
                $"[{item.CompletedChecks}/{item.TotalChecks}] {item.Message}"));
        var report = await preflightService!.RunAsync(request, preflightProgress, cancellationToken);
        if (!report.Eligible)
        {
            await WriteStageEvidenceAsync(config, stage, cancellationToken, "FAILED");
            _nextStageIndex = stageIndex;
            await SaveCheckpointAsync(config, cancellationToken);
            var reason = report.ExclusionReason ?? "Unknown preflight failure.";
            Report(progress, stage, PipelineStageState.Failed, stageIndex, "Preflight failed: " + reason);
            return new PipelineRunResult(
                false,
                false,
                "PHASE_2_FAILED: " + reason,
                DateTimeOffset.UtcNow);
        }

        await WriteStageEvidenceAsync(config, stage, cancellationToken);
        _nextStageIndex = stageIndex + 1;
        await SaveCheckpointAsync(config, cancellationToken);
        Report(progress, stage, PipelineStageState.Passed, _nextStageIndex, "Repository preflight completed with eligible=true.");
        return null;
    }

    private async Task<PipelineRunResult?> ExecuteSubjectSelectionAsync(
        ExperimentConfig config,
        PipelineStageDefinition stage,
        int stageIndex,
        IProgress<PipelineProgress> progress,
        CancellationToken cancellationToken)
    {
        Report(progress, stage, PipelineStageState.Running, stageIndex, "Extracting and selecting focal-method subjects...");
        var outputRoot = Path.Combine(layout.ResolveOutputRoot(config.OutputRoot), "subjects");
        if (config.Mode.Equals("Smoke", StringComparison.OrdinalIgnoreCase) && smokeSubjectService is not null)
        {
            try
            {
                var discovery = new SmokeMethodDiscoveryRequest(
                    config.RepositoryRoot, outputRoot, layout.WorkspaceRoot,
                    ResolveRevision(config.RepositoryRoot, config.Subjects.BenchmarkRevision), config.Tools.JavaHome,
                    config.Tools.ModulePath);
                var subject = await smokeSubjectService.FreezeAsync(
                    new SmokeSubjectFreezeRequest(discovery, config.Subjects.SmokeCandidateKey),
                    new InlineProgress<string>(message =>
                        Report(progress, stage, PipelineStageState.Running, stageIndex, message)), cancellationToken);
                await WriteStageEvidenceAsync(config, stage, cancellationToken);
                _nextStageIndex = stageIndex + 1;
                await SaveCheckpointAsync(config, cancellationToken);
                Report(progress, stage, PipelineStageState.Passed, _nextStageIndex,
                    $"Smoke focal method frozen: {subject.FocalClass}.{subject.FocalMethodSignature}.");
                return null;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return await FailStageAsync(config, stage, stageIndex, progress,
                    "SMOKE_SUBJECT_FAILED: " + exception.Message, cancellationToken);
            }
        }

        var projectsRoot = layout.ResolveOutputRoot(config.Subjects.DatasetProjectsRoot);
        var request = new SubjectSelectionRequest(
            projectsRoot,
            outputRoot,
            layout.WorkspaceRoot,
            config.Subjects.BenchmarkRevision,
            config.Tools.JavaHome,
            config.Subjects.PilotCount,
            config.Subjects.OfficialCount,
            config.Subjects.MaxPerProject,
            config.Subjects.SelectionSeed);
        SubjectSelectionReport report;
        try
        {
            report = await subjectSelectionService!.RunAsync(
                request,
                new InlineProgress<string>(message =>
                    Report(progress, stage, PipelineStageState.Running, stageIndex, message)),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _nextStageIndex = stageIndex;
            await WriteStageEvidenceAsync(config, stage, cancellationToken, "FAILED");
            await SaveCheckpointAsync(config, cancellationToken);
            var message = "PHASE_3_FAILED: " + exception.Message;
            Report(progress, stage, PipelineStageState.Failed, stageIndex, message);
            return new PipelineRunResult(false, false, message, DateTimeOffset.UtcNow);
        }
        if (!report.AutomatedSelectionComplete)
        {
            await WriteStageEvidenceAsync(config, stage, cancellationToken, "FAILED");
            _nextStageIndex = stageIndex;
            await SaveCheckpointAsync(config, cancellationToken);
            Report(progress, stage, PipelineStageState.Failed, stageIndex, "Automated subject-selection invariants failed.");
            return new PipelineRunResult(false, false, "PHASE_3_FAILED: subject-selection invariants failed.", DateTimeOffset.UtcNow);
        }

        if (!report.ManualReviewComplete)
        {
            await WriteStageEvidenceAsync(config, stage, cancellationToken, "WAITING_FOR_APPROVAL");
            _nextStageIndex = stageIndex;
            await SaveCheckpointAsync(config, cancellationToken);
            var message = $"WAITING_FOR_APPROVAL: review {report.PilotCount + report.OfficialCount} focal methods in subjects/manual-review.csv.";
            Report(progress, stage, PipelineStageState.WaitingForApproval, stageIndex, message);
            return new PipelineRunResult(false, false, message, DateTimeOffset.UtcNow);
        }

        await WriteStageEvidenceAsync(config, stage, cancellationToken);
        _nextStageIndex = stageIndex + 1;
        await SaveCheckpointAsync(config, cancellationToken);
        Report(progress, stage, PipelineStageState.Passed, _nextStageIndex, "Subject manifest reviewed and frozen.");
        return null;
    }

    private static void Report(
        IProgress<PipelineProgress> progress,
        PipelineStageDefinition stage,
        PipelineStageState state,
        int completed,
        string message) =>
        progress.Report(new PipelineProgress(
            stage.Id,
            stage.DisplayName,
            state,
            completed,
            Stages.Length,
            message,
            DateTimeOffset.UtcNow));

    private async Task<PipelineRunResult?> ExecuteStructuralTargetsAsync(
        ExperimentConfig config,
        PipelineStageDefinition stage,
        int stageIndex,
        IProgress<PipelineProgress> progress,
        CancellationToken cancellationToken)
    {
        Report(progress, stage, PipelineStageState.Running, stageIndex,
            "Extracting independent source-mapped structural targets...");
        var outputBase = layout.ResolveOutputRoot(config.OutputRoot);
        var request = new StructuralTargetRequest(
            layout.WorkspaceRoot,
            ResolveProjectsRoot(config),
            Path.Combine(outputBase, "subjects"),
            Path.Combine(outputBase, "targets"),
            config.Tools.JavaHome,
            config.Mode);
        StructuralTargetReport report;
        try
        {
            report = await structuralTargetService!.RunAsync(
                request,
                new InlineProgress<string>(message =>
                    Report(progress, stage, PipelineStageState.Running, stageIndex, message)),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _nextStageIndex = stageIndex;
            await WriteStageEvidenceAsync(config, stage, cancellationToken, "FAILED");
            await SaveCheckpointAsync(config, cancellationToken);
            var message = "PHASE_4_FAILED: " + exception.Message;
            Report(progress, stage, PipelineStageState.Failed, stageIndex, message);
            return new PipelineRunResult(false, false, message, DateTimeOffset.UtcNow);
        }

        if (!report.Complete)
        {
            _nextStageIndex = stageIndex;
            await WriteStageEvidenceAsync(config, stage, cancellationToken, "FAILED");
            await SaveCheckpointAsync(config, cancellationToken);
            const string message = "PHASE_4_FAILED: structural-target invariants failed; inspect target-extraction-report.json.";
            Report(progress, stage, PipelineStageState.Failed, stageIndex, message);
            return new PipelineRunResult(false, false, message, DateTimeOffset.UtcNow);
        }

        await WriteStageEvidenceAsync(config, stage, cancellationToken);
        _nextStageIndex = stageIndex + 1;
        await SaveCheckpointAsync(config, cancellationToken);
        Report(progress, stage, PipelineStageState.Passed, _nextStageIndex,
            $"Independent target inventory frozen: {report.OfficialBranchSiteTargetCount} official branch sites across {report.SubjectCount} subjects.");
        return null;
    }

    private async Task<PipelineRunResult?> ExecuteRepositoryContextPromptsAsync(
        ExperimentConfig config,
        PipelineStageDefinition stage,
        int stageIndex,
        IProgress<PipelineProgress> progress,
        CancellationToken cancellationToken)
    {
        Report(progress, stage, PipelineStageState.Running, stageIndex,
            "Building shared repository contexts and frozen prompt contracts...");
        var output = layout.ResolveOutputRoot(config.OutputRoot);
        var request = new RepositoryContextPromptRequest(
            layout.WorkspaceRoot,
            ResolveProjectsRoot(config),
            Path.Combine(output, "subjects"),
            Path.Combine(output, "targets"),
            output,
            config.Tools.JavaHome,
            config.Llm.Provider,
            config.Llm.BaseUrl,
            config.Llm.ModelId,
            config.Mode,
            config.ExperimentId);
        RepositoryContextPromptReport report;
        try
        {
            report = await repositoryContextPromptService!.RunAsync(
                request,
                new InlineProgress<string>(message =>
                    Report(progress, stage, PipelineStageState.Running, stageIndex, message)),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _nextStageIndex = stageIndex;
            await WriteStageEvidenceAsync(config, stage, cancellationToken, "FAILED");
            await SaveCheckpointAsync(config, cancellationToken);
            var message = "PHASE_5_FAILED: " + exception.Message;
            Report(progress, stage, PipelineStageState.Failed, stageIndex, message);
            return new PipelineRunResult(false, false, message, DateTimeOffset.UtcNow);
        }

        if (!report.Complete)
        {
            _nextStageIndex = stageIndex;
            await WriteStageEvidenceAsync(config, stage, cancellationToken, "FAILED");
            await SaveCheckpointAsync(config, cancellationToken);
            const string message = "PHASE_5_FAILED: context/prompt invariants failed; inspect phase5-context-prompt-report.json.";
            Report(progress, stage, PipelineStageState.Failed, stageIndex, message);
            return new PipelineRunResult(false, false, message, DateTimeOffset.UtcNow);
        }

        await WriteStageEvidenceAsync(config, stage, cancellationToken);
        _nextStageIndex = stageIndex + 1;
        await SaveCheckpointAsync(config, cancellationToken);
        Report(progress, stage, PipelineStageState.Passed, _nextStageIndex,
            $"Shared contexts and prompt contracts frozen for {report.SubjectCount} subjects.");
        return null;
    }

    private async Task<PipelineRunResult?> ExecuteDirectBaselineAsync(
        ExperimentConfig config,
        PipelineStageDefinition stage,
        int stageIndex,
        IProgress<PipelineProgress> progress,
        CancellationToken cancellationToken)
    {
        Report(progress, stage, PipelineStageState.Running, stageIndex,
            "Running zero-shot DIRECT baseline without planning or repair...");
        var request = new DirectBaselineRequest(
            config.ExperimentId, config.Mode, layout.ResolveOutputRoot(config.OutputRoot),
            config.Llm.Provider, config.Llm.BaseUrl, config.Llm.ModelId, config.Llm.Temperature,
            config.Llm.TimeoutSeconds, config.Generation.MaxTestMethods,
            config.Generation.TotalTokenCeilingPerRun, config.Subjects.Repetitions,
            config.Llm.TransportRetryCount, config.Generation.MaxOutputTokensPerCall,
            config.Llm.DeepSeekThinkingMode, config.Generation.MaxParallelLlmCalls);
        DirectBaselineReport report;
        try
        {
            report = await directBaselineService!.RunAsync(request,
                new InlineProgress<string>(message =>
                    Report(progress, stage, PipelineStageState.Running, stageIndex, message)),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _nextStageIndex = stageIndex;
            await WriteStageEvidenceAsync(config, stage, cancellationToken, "FAILED");
            await SaveCheckpointAsync(config, cancellationToken);
            var message = "PHASE_6_FAILED: " + exception.Message;
            Report(progress, stage, PipelineStageState.Failed, stageIndex, message);
            return new PipelineRunResult(false, false, message, DateTimeOffset.UtcNow);
        }

        if (report.WaitingForApiKeyCount > 0)
        {
            _nextStageIndex = stageIndex;
            await WriteStageEvidenceAsync(config, stage, cancellationToken, "WAITING_FOR_API_KEY");
            await SaveCheckpointAsync(config, cancellationToken);
            const string message = "WAITING_FOR_API_KEY: add a key for the selected provider, then Resume.";
            Report(progress, stage, PipelineStageState.WaitingForApiKey, stageIndex, message);
            return new PipelineRunResult(false, false, message, DateTimeOffset.UtcNow);
        }
        if (!report.Complete || !report.NoHiddenRetryInvariant || !report.NoPlannerOrRepairTrace)
        {
            _nextStageIndex = stageIndex;
            await WriteStageEvidenceAsync(config, stage, cancellationToken, "FAILED");
            await SaveCheckpointAsync(config, cancellationToken);
            const string message = "PHASE_6_FAILED: DIRECT execution invariants failed; inspect direct-baseline-report.json.";
            Report(progress, stage, PipelineStageState.Failed, stageIndex, message);
            return new PipelineRunResult(false, false, message, DateTimeOffset.UtcNow);
        }
        await WriteStageEvidenceAsync(config, stage, cancellationToken);
        _nextStageIndex = stageIndex + 1;
        await SaveCheckpointAsync(config, cancellationToken);
        Report(progress, stage, PipelineStageState.Passed, _nextStageIndex,
            $"DIRECT completed: valid={report.ValidOutputCount}, invalid retained={report.InvalidOutputCount}.");
        return null;
    }

    private async Task<PipelineRunResult?> ExecutePlannedPipelineAsync(
        ExperimentConfig config,
        PipelineStageDefinition stage,
        int stageIndex,
        IProgress<PipelineProgress> progress,
        CancellationToken cancellationToken)
    {
        Report(progress, stage, PipelineStageState.Running, stageIndex,
            "Running Structural Planner and traceable PLANNED generator without repair...");
        var request = new PlannedPipelineRequest(
            config.ExperimentId, config.Mode, layout.ResolveOutputRoot(config.OutputRoot),
            config.Llm.Provider, config.Llm.BaseUrl, config.Llm.ModelId, config.Llm.Temperature,
            config.Llm.TimeoutSeconds, config.Generation.MaxTestMethods,
            config.Generation.TotalTokenCeilingPerRun, config.Subjects.Repetitions,
            config.Llm.TransportRetryCount, config.Generation.MaxOutputTokensPerCall,
            config.Llm.DeepSeekThinkingMode, config.Generation.MaxParallelLlmCalls);
        PlannedPipelineReport report;
        try
        {
            report = await plannedPipelineService!.RunAsync(request,
                new InlineProgress<string>(message =>
                    Report(progress, stage, PipelineStageState.Running, stageIndex, message)),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _nextStageIndex = stageIndex;
            await WriteStageEvidenceAsync(config, stage, cancellationToken, "FAILED");
            await SaveCheckpointAsync(config, cancellationToken);
            var message = "PHASE_7_FAILED: " + exception.Message;
            Report(progress, stage, PipelineStageState.Failed, stageIndex, message);
            return new PipelineRunResult(false, false, message, DateTimeOffset.UtcNow);
        }
        if (report.WaitingForApiKeyCount > 0)
        {
            _nextStageIndex = stageIndex;
            await WriteStageEvidenceAsync(config, stage, cancellationToken, "WAITING_FOR_API_KEY");
            await SaveCheckpointAsync(config, cancellationToken);
            const string message = "WAITING_FOR_API_KEY: add a key for the selected provider, then Resume.";
            Report(progress, stage, PipelineStageState.WaitingForApiKey, stageIndex, message);
            return new PipelineRunResult(false, false, message, DateTimeOffset.UtcNow);
        }
        if (!report.Complete || !report.NoHiddenRetryInvariant || !report.NoRepairOrReviewerTrace)
        {
            _nextStageIndex = stageIndex;
            await WriteStageEvidenceAsync(config, stage, cancellationToken, "FAILED");
            await SaveCheckpointAsync(config, cancellationToken);
            const string message = "PHASE_7_FAILED: PLANNED invariants failed; inspect planned-pipeline-report.json.";
            Report(progress, stage, PipelineStageState.Failed, stageIndex, message);
            return new PipelineRunResult(false, false, message, DateTimeOffset.UtcNow);
        }
        await WriteStageEvidenceAsync(config, stage, cancellationToken);
        _nextStageIndex = stageIndex + 1;
        await SaveCheckpointAsync(config, cancellationToken);
        Report(progress, stage, PipelineStageState.Passed, _nextStageIndex,
            $"PLANNED completed: valid={report.ValidOutputCount}, invalid retained={report.InvalidPlannerCount + report.InvalidGeneratorCount}.");
        return null;
    }

    private async Task<PipelineRunResult?> ExecuteCompileTestAsync(
        ExperimentConfig config,
        PipelineStageDefinition stage,
        int stageIndex,
        IProgress<PipelineProgress> progress,
        CancellationToken cancellationToken)
    {
        Report(progress, stage, PipelineStageState.Running, stageIndex,
            "Creating isolated run workspaces and executing Compile → JUnit gates...");
        try
        {
            var report = await toolExecutionService!.RunCompileAndTestAsync(
                CreateToolRequest(config),
                new InlineProgress<string>(message =>
                    Report(progress, stage, PipelineStageState.Running, stageIndex, message)),
                cancellationToken);
            if (!report.Complete || !report.GateOrderingValid || !report.GeneratedTestsOnly)
                return await FailStageAsync(config, stage, stageIndex, progress,
                    "PHASE_8_FAILED: Compile/JUnit harness invariants failed; inspect compile-test-report.json.", cancellationToken);
            await PassStageAsync(config, stage, stageIndex, progress,
                $"Compile/JUnit completed: compile-pass={report.CompilePassCount}, junit-process-success={report.JunitProcessSuccessCount}, actual-test-pass={report.ExecutionPassCount}, test-discovery-fail={report.TestDiscoveryFailCount}, generation-invalid={report.GenerationInvalidCount}.",
                cancellationToken);
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return await FailStageAsync(config, stage, stageIndex, progress,
                "PHASE_8_FAILED: " + exception.Message, cancellationToken);
        }
    }

    private async Task<PipelineRunResult?> ExecuteAdequacyAsync(
        ExperimentConfig config,
        PipelineStageDefinition stage,
        int stageIndex,
        IProgress<PipelineProgress> progress,
        CancellationToken cancellationToken)
    {
        Report(progress, stage, PipelineStageState.Running, stageIndex,
            "Executing generated-test-only JaCoCo and focal-method-filtered PIT...");
        try
        {
            var report = await toolExecutionService!.RunAdequacyAsync(
                CreateToolRequest(config),
                new InlineProgress<string>(message =>
                    Report(progress, stage, PipelineStageState.Running, stageIndex, message)),
                cancellationToken);
            if (!report.Complete || !report.GateOrderingValid || !report.GeneratedTestsOnly)
                return await FailStageAsync(config, stage, stageIndex, progress,
                    "PHASE_8_FAILED: JaCoCo/PIT harness invariants failed; inspect adequacy-report.json.", cancellationToken);
            await PassStageAsync(config, stage, stageIndex, progress,
                $"JaCoCo/PIT completed: coverage={report.CoveragePassCount}, mutation={report.MutationPassCount}, tool-errors={report.CoverageToolErrorCount + report.MutationToolErrorCount}.",
                cancellationToken);
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return await FailStageAsync(config, stage, stageIndex, progress,
                "PHASE_8_FAILED: " + exception.Message, cancellationToken);
        }
    }

    private async Task<PipelineRunResult?> ExecuteLedgerAsync(
        ExperimentConfig config,
        PipelineStageDefinition stage,
        int stageIndex,
        IProgress<PipelineProgress> progress,
        CancellationToken cancellationToken)
    {
        Report(progress, stage, PipelineStageState.Running, stageIndex,
            "Rebuilding authoritative metrics ledger from immutable run artifacts...");
        try
        {
            var configPath = Path.Combine(layout.ExperimentConfigHistoryDirectory, config.ExperimentId + ".json");
            var report = await metricsLedgerService!.RebuildAsync(new MetricsLedgerRequest(
                    config.ExperimentId, config.Mode, layout.ResolveOutputRoot(config.OutputRoot),
                    layout.WorkspaceRoot, configPath),
                new InlineProgress<string>(message =>
                    Report(progress, stage, PipelineStageState.Running, stageIndex, message)),
                cancellationToken);
            if (!report.Complete)
                return await FailStageAsync(config, stage, stageIndex, progress,
                    $"PHASE_9_FAILED: ledger rejected {report.InvalidRowCount} invalid row(s); inspect {report.DataQualityReportPath}.",
                    cancellationToken);
            await PassStageAsync(config, stage, stageIndex, progress,
                $"Ledger completed: runs={report.RunCount}, DIRECT={report.DirectRunCount}, PLANNED={report.PlannedRunCount}, SHA-256={report.LedgerSha256}.",
                cancellationToken);
            _nextStageIndex = Stages.Length;
            await SaveCheckpointAsync(config, cancellationToken);
            return new PipelineRunResult(true, false,
                "Phase 9 metrics/evidence ledger completed. Pilot execution begins in Phase 10.",
                DateTimeOffset.UtcNow);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return await FailStageAsync(config, stage, stageIndex, progress,
                "PHASE_9_FAILED: " + exception.Message, cancellationToken);
        }
    }

    private ToolExecutionRequest CreateToolRequest(ExperimentConfig config) => new(
        config.ExperimentId,
        config.Mode,
        layout.ResolveOutputRoot(config.OutputRoot),
        layout.WorkspaceRoot,
        ResolveProjectsRoot(config),
        config.Tools.JavaHome,
        config.Tools.CompileTimeoutSeconds,
        config.Tools.TestTimeoutSeconds,
        config.Tools.JacocoTimeoutSeconds,
        config.Tools.PitTimeoutSeconds,
        config.Tools.JacocoVersion,
        config.Tools.PitVersion,
        config.Tools.MaxParallelToolRuns);

    private string ResolveProjectsRoot(ExperimentConfig config) =>
        config.Mode.Equals("Smoke", StringComparison.OrdinalIgnoreCase)
            ? Directory.GetParent(Path.GetFullPath(config.RepositoryRoot))?.FullName
              ?? throw new InvalidOperationException("Smoke repository must have a parent directory.")
            : layout.ResolveOutputRoot(config.Subjects.DatasetProjectsRoot);

    private static string ResolveRevision(string repositoryRoot, string fallback)
    {
        try
        {
            var head = Path.Combine(repositoryRoot, ".git", "HEAD");
            if (!File.Exists(head)) return "UNVERSIONED";
            var value = File.ReadAllText(head).Trim();
            if (!value.StartsWith("ref: ", StringComparison.Ordinal)) return value;
            var reference = Path.Combine(repositoryRoot, ".git", value[5..].Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(reference) ? File.ReadAllText(reference).Trim() : fallback;
        }
        catch { return fallback; }
    }

    private async Task PassStageAsync(ExperimentConfig config, PipelineStageDefinition stage, int stageIndex,
        IProgress<PipelineProgress> progress, string message, CancellationToken token)
    {
        await WriteStageEvidenceAsync(config, stage, token);
        _nextStageIndex = stageIndex + 1;
        await SaveCheckpointAsync(config, token);
        Report(progress, stage, PipelineStageState.Passed, _nextStageIndex, message);
    }

    private async Task<PipelineRunResult> FailStageAsync(ExperimentConfig config, PipelineStageDefinition stage,
        int stageIndex, IProgress<PipelineProgress> progress, string message, CancellationToken token)
    {
        _nextStageIndex = stageIndex;
        await WriteStageEvidenceAsync(config, stage, token, "FAILED");
        await SaveCheckpointAsync(config, token);
        Report(progress, stage, PipelineStageState.Failed, stageIndex, message);
        return new PipelineRunResult(false, false, message, DateTimeOffset.UtcNow);
    }

    private async Task WriteStageEvidenceAsync(
        ExperimentConfig config,
        PipelineStageDefinition stage,
        CancellationToken cancellationToken,
        string status = "PASSED")
    {
        var directory = GetScaffoldRunDirectory(config);
        var logDirectory = Path.Combine(directory, "logs");
        Directory.CreateDirectory(logDirectory);
        var line = $"{DateTimeOffset.UtcNow:O}\t{stage.Id}\t{status}{Environment.NewLine}";
        await File.AppendAllTextAsync(Path.Combine(logDirectory, "pipeline.log"), line, cancellationToken);
    }

    private async Task SaveCheckpointAsync(ExperimentConfig config, CancellationToken cancellationToken)
    {
        var checkpoint = new PipelineCheckpoint
        {
            ExperimentId = config.ExperimentId,
            NextStageIndex = _nextStageIndex,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await JsonSupport.WriteAsync(layout.PipelineCheckpointPath, checkpoint, cancellationToken);
        _hasCheckpoint = true;
    }

    private async Task RestoreCheckpointAsync(ExperimentConfig config, CancellationToken cancellationToken)
    {
        var checkpoint = await JsonSupport.ReadAsync<PipelineCheckpoint>(layout.PipelineCheckpointPath, cancellationToken);
        if (checkpoint is not null && checkpoint.ExperimentId == config.ExperimentId)
        {
            _nextStageIndex = Math.Clamp(checkpoint.NextStageIndex, 0, Stages.Length);
            _hasCheckpoint = true;
        }
    }

    private bool HasResumableCheckpointOnDisk()
    {
        try
        {
            if (!File.Exists(layout.PipelineCheckpointPath)) return false;
            var checkpoint = JsonSerializer.Deserialize<PipelineCheckpoint>(
                File.ReadAllText(layout.PipelineCheckpointPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return checkpoint is not null
                   && checkpoint.NextStageIndex >= 0
                   && checkpoint.NextStageIndex < Stages.Length;
        }
        catch
        {
            return false;
        }
    }

    private async Task WriteDummyRunContractAsync(ExperimentConfig config, CancellationToken cancellationToken)
    {
        var directory = GetScaffoldRunDirectory(config);
        foreach (var child in new[] { "input", "generation", "tools", "tools/pit", "logs" })
            Directory.CreateDirectory(Path.Combine(directory, child));

        var metadata = new
        {
            config.ExperimentId,
            kind = "PHASE_1_SCAFFOLD_SMOKE",
            providerId = config.Llm.Provider,
            requestedModelId = config.Llm.ModelId,
            completedAt = DateTimeOffset.UtcNow,
            warning = "This is UI/orchestration scaffold evidence, not an RQ1 generation result."
        };

        await WriteJsonAsync(Path.Combine(directory, "run.json"), metadata, cancellationToken);
        await WriteJsonAsync(Path.Combine(directory, "metrics.json"), new
        {
            status = "SCAFFOLD_COMPLETED",
            isOfficialResult = false
        }, cancellationToken);
    }

    private string GetScaffoldRunDirectory(ExperimentConfig config) =>
        Path.Combine(layout.ResolveOutputRoot(config.OutputRoot), "runs", config.ExperimentId, "scaffold-smoke");

    private static async Task WriteJsonAsync(
        string path,
        object value,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(value, JsonSupport.Options),
            cancellationToken);
    }

    private sealed class PipelineCheckpoint
    {
        public string ExperimentId { get; set; } = string.Empty;
        public int NextStageIndex { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
