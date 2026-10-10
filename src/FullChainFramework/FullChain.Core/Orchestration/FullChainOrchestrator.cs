using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FullChain.Contracts;
using FullChain.Core.Repair;
using FullChain.Core.Verification;

namespace FullChain.Core.Orchestration;

public sealed record OrchestratorRunRequest(
    FullChainConfiguration Configuration,
    TargetDescriptor Target,
    int RepetitionId,
    ToolLock ToolLock,
    string CampaignId = "campaign-full-chain",
    string Treatment = "FullChain",
    bool ResumeIfPossible = true
);

public sealed record OrchestratorCheckpointPayload(
    FullChainState State,
    string? TestClassName,
    string? TestPackageName,
    string? CurrentTestSource,
    string? PlanJson,
    string? PlanHash,
    IReadOnlyDictionary<string, string>? ScenarioToMethodMap,
    CommonEnvelope? LatestEnvelope,
    VerificationDecision? LatestVerificationDecision,
    List<string> RecordedArtifactPaths,
    FailureEvidence? LatestFailure = null,
    string SchemaVersion = "full-chain-orchestrator-checkpoint-v1"
);

public sealed record OrchestratorRunResult(
    FullChainRecord Record,
    ArtifactIndex ArtifactIndex,
    bool ResumedFromCheckpoint,
    string TerminalStatus,
    string? TerminalReason
);

public sealed class FullChainOrchestrator
{
    private const string CurrentCheckpointPayloadSchemaVersion = "full-chain-orchestrator-checkpoint-v2";
    private readonly FullChainStateMachine _stateMachine;
    private readonly ITestGenerator _testGenerator;
    private readonly ICandidateTestRunner _candidateRunner;
    private readonly ITestRepairExecutor _repairExecutor;
    private readonly ICommonEvaluator _evaluator;
    private readonly IVerifierAdapter _verifier;
    private readonly ICheckpointStore _checkpointStore;
    private readonly IArtifactIndexWriter _artifactIndexWriter;
    private readonly IWorkspaceManager _workspaceManager;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public event Action<string, string>? StageChanged;
    public event Action<string>? LogEmitted;

    public FullChainOrchestrator(
        FullChainStateMachine stateMachine,
        ITestGenerator testGenerator,
        ICandidateTestRunner candidateRunner,
        ITestRepairExecutor repairExecutor,
        ICommonEvaluator evaluator,
        IVerifierAdapter verifier,
        ICheckpointStore checkpointStore,
        IArtifactIndexWriter artifactIndexWriter,
        IWorkspaceManager workspaceManager)
    {
        _stateMachine = stateMachine ?? throw new ArgumentNullException(nameof(stateMachine));
        _testGenerator = testGenerator ?? throw new ArgumentNullException(nameof(testGenerator));
        _candidateRunner = candidateRunner ?? throw new ArgumentNullException(nameof(candidateRunner));
        _repairExecutor = repairExecutor ?? throw new ArgumentNullException(nameof(repairExecutor));
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _checkpointStore = checkpointStore ?? throw new ArgumentNullException(nameof(checkpointStore));
        _artifactIndexWriter = artifactIndexWriter ?? throw new ArgumentNullException(nameof(artifactIndexWriter));
        _workspaceManager = workspaceManager ?? throw new ArgumentNullException(nameof(workspaceManager));
    }

    public static string ComputeInputHash(FullChainConfiguration config, TargetDescriptor target, ToolLock toolLock)
    {
        using var sha = SHA256.Create();
        var canonical = $"{config.ExperimentId}:{config.Llm.ModelId}:{config.Llm.Temperature:F2}:{target.SubjectId}:{target.SourceSha256}:{toolLock.Environment.Java.Version}:{toolLock.Environment.Maven.Version}:{toolLock.Environment.Evosuite.Jar.Sha256}";
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    public async Task<OrchestratorRunResult> RunAsync(
        OrchestratorRunRequest request,
        IGlobalBudgetGovernor budget,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(budget);

        var inputHash = ComputeInputHash(request.Configuration, request.Target, request.ToolLock);
        var resumedFromCheckpoint = false;

        FullChainState state = FullChainStateMachine.CreateInitialState();
        string? testClassName = null;
        string? testPackageName = null;
        string? currentTestSource = null;
        string? planJson = null;
        string? planHash = null;
        IReadOnlyDictionary<string, string>? scenarioToMethodMap = null;
        CommonEnvelope? latestEnvelope = null;
        VerificationDecision? latestVerificationDecision = null;
        FailureEvidence? latestFailure = null;
        var recordedArtifactPaths = new List<string>();

        // 1. Check for checkpoint resume
        if (request.ResumeIfPossible)
        {
            var checkpoint = await _checkpointStore.LoadCheckpointAsync(
                request.CampaignId,
                request.Target.SubjectId,
                request.RepetitionId,
                request.Treatment,
                ct);

            if (checkpoint != null)
            {
                // Invariant: Resume rejects a changed config/manifest/tool lock
                if (!string.Equals(checkpoint.InputHash, inputHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Checkpoint input hash mismatch: configuration, manifest, or tool lock has changed. Expected {checkpoint.InputHash}, but current is {inputHash}.");
                }

                var payload = JsonSerializer.Deserialize<OrchestratorCheckpointPayload>(checkpoint.FullChainStateJson, JsonOptions);
                if (payload == null)
                {
                    throw new InvalidOperationException("Checkpoint payload could not be deserialized. Start a new campaign after preserving the corrupted checkpoint for diagnosis.");
                }

                if (!string.Equals(payload.SchemaVersion, CurrentCheckpointPayloadSchemaVersion, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Checkpoint schema '{payload.SchemaVersion}' predates resumable next-state checkpoints and cannot be resumed safely. Start a new campaign or migrate this checkpoint explicitly.");
                }

                if (!string.Equals(checkpoint.CurrentStage, payload.State.CurrentStage, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Checkpoint stage mismatch: envelope says '{checkpoint.CurrentStage}' but payload says '{payload.State.CurrentStage}'. Refusing unsafe resume.");
                }

                ValidateCheckpointPayload(payload);

                state = payload.State;
                testClassName = payload.TestClassName;
                testPackageName = payload.TestPackageName;
                currentTestSource = payload.CurrentTestSource;
                planJson = payload.PlanJson;
                planHash = payload.PlanHash;
                scenarioToMethodMap = payload.ScenarioToMethodMap;
                latestEnvelope = payload.LatestEnvelope;
                latestVerificationDecision = payload.LatestVerificationDecision;
                latestFailure = payload.LatestFailure;
                if (payload.RecordedArtifactPaths != null)
                {
                    recordedArtifactPaths.AddRange(payload.RecordedArtifactPaths);
                }

                resumedFromCheckpoint = true;
                Log($"[Checkpoint] Successfully resumed at next stage '{checkpoint.CurrentStage}' with input hash {checkpoint.InputHash[..8]}...");
            }
        }

        // 2. Prepare isolated workspace
        var workspacePath = await _workspaceManager.PrepareIsolatedWorkspaceAsync(
            request.Target.RepositoryPath,
            request.CampaignId,
            request.Target.SubjectId,
            request.RepetitionId,
            request.Treatment,
            "orchestrator",
            ct);

        Log($"[Workspace] Isolated workspace ready at: {workspacePath}");

        // 3. Execution loop
        try
        {
            while (!FullChainStateMachine.IsTerminal(state.CurrentStage))
            {
                ct.ThrowIfCancellationRequested();

                if (budget.IsExhausted)
                {
                    state = _stateMachine.TransitionToTerminal(state, TerminalStatus.BudgetExhausted, "Global budget exhausted.");
                    break;
                }

                NotifyStage(state.CurrentStage, state.Status);

                switch (state.CurrentStage)
                {
                    case FullChainStages.PlannedGeneration:
                    {
                        Log("[Stage: PlannedGeneration] Generating tests via RQ1 adapter...");
                        var genArtifact = await _testGenerator.GenerateTestsAsync(request.Target, contextJson: "", budget, ct);

                        testClassName = genArtifact.TestClassName?.Split('.').Last().Trim();
                        testPackageName = genArtifact.TestPackageName;
                        currentTestSource = genArtifact.TestSourceCode;
                        planJson = genArtifact.PlanJson;
                        planHash = genArtifact.PlanHash;
                        scenarioToMethodMap = genArtifact.ScenarioToMethodMap;

                        state = _stateMachine.TransitionFromGeneration(state, generationSuccess: true);
                        await SaveCheckpointAsync(request, state, inputHash, testClassName, testPackageName, currentTestSource, planJson, planHash, scenarioToMethodMap, latestEnvelope, latestVerificationDecision, latestFailure, recordedArtifactPaths, ct);
                        break;
                    }

                    case FullChainStages.CandidateCompileAndExecute:
                    {
                        Log("[Stage: CandidateCompileAndExecute] Compiling and executing test candidate...");
                        var cleanClassName = testClassName?.Split('.').Last().Trim() ?? "GeneratedTest";
                        var testFqn = string.IsNullOrWhiteSpace(testPackageName) ? cleanClassName : $"{testPackageName}.{cleanClassName}";

                        var execResult = await _candidateRunner.CompileAndExecuteCandidateAsync(
                            request.Target,
                            workspacePath,
                            testFqn,
                            currentTestSource!,
                            budget,
                            ct);

                        latestFailure = execResult.Failure;
                        bool isRepairEligible = false;
                        if (latestFailure != null)
                        {
                            var decision = RepairRoutePolicy.EvaluateEligibility(
                                compileSuccess: execResult.CompileSuccess,
                                testExecutionSuccess: execResult.ExecutionSuccess,
                                timedOut: false,
                                providerFault: false,
                                toolFailure: false,
                                failureOutput: $"{latestFailure.DiagnosticMessage}\n{latestFailure.Stdout}",
                                scenarioToMethodMap: scenarioToMethodMap ?? new Dictionary<string, string>(),
                                currentRound: state.RepairRound,
                                maxRounds: _stateMachine.MaxRepairRounds,
                                budgetAllowsRepair: !budget.IsExhausted
                            );
                            isRepairEligible = decision.IsEligible;
                            if (decision.IsEligible && !string.IsNullOrEmpty(decision.FailingTestMethod))
                            {
                                latestFailure = latestFailure with
                                {
                                    FailingTestMethodName = decision.FailingTestMethod,
                                    AssociatedScenarioId = decision.ScenarioId
                                };
                            }
                        }

                        bool hasRemainingBudget = !budget.IsExhausted && state.RepairRound < _stateMachine.MaxRepairRounds;

                        var testFilePath = $"{workspacePath}/src/test/java/{testFqn.Replace('.', '/')}.java";
                        await _artifactIndexWriter.RecordArtifactAsync(request.CampaignId, request.Target.SubjectId, request.RepetitionId, request.Treatment, FullChainStages.CandidateCompileAndExecute, "CandidateTestSource", testFilePath, ct);

                        state = _stateMachine.TransitionFromCompileAndExecute(
                            state,
                            compileSuccess: execResult.CompileSuccess,
                            executionSuccess: execResult.ExecutionSuccess,
                            isRepairEligible: isRepairEligible,
                            hasRemainingBudget: hasRemainingBudget);
                        await SaveCheckpointAsync(request, state, inputHash, testClassName, testPackageName, currentTestSource, planJson, planHash, scenarioToMethodMap, latestEnvelope, latestVerificationDecision, latestFailure, recordedArtifactPaths, ct);
                        break;
                    }

                    case FullChainStages.Repair:
                    {
                        Log($"[Stage: Repair] Attempting repair round {state.RepairRound} via TGSLR...");
                        var repairArtifact = await _repairExecutor.AttemptRepairAsync(
                            request.Target,
                            workspacePath,
                            currentTestSource!,
                            latestFailure!,
                            planJson,
                            state.RepairRound,
                            budget,
                            ct);

                        if (repairArtifact != null && !string.IsNullOrWhiteSpace(repairArtifact.FullRepairedSource))
                        {
                            currentTestSource = repairArtifact.FullRepairedSource;
                            Log($"[Repair] Round {state.RepairRound} produced candidate replacement for method '{repairArtifact.TargetMethodName}'.");
                        }
                        else
                        {
                            Log($"[Repair] Round {state.RepairRound} yielded no replacement; retaining existing candidate.");
                        }

                        state = _stateMachine.TransitionFromRepair(state);
                        await SaveCheckpointAsync(request, state, inputHash, testClassName, testPackageName, currentTestSource, planJson, planHash, scenarioToMethodMap, latestEnvelope, latestVerificationDecision, latestFailure, recordedArtifactPaths, ct);
                        break;
                    }

                    case FullChainStages.CommonEvaluation:
                    {
                        Log("[Stage: CommonEvaluation] Evaluating candidate via JaCoCo, PIT, and STS...");
                        var cleanClassName = testClassName?.Split('.').Last().Trim() ?? "GeneratedTest";
                        var testFqn = string.IsNullOrWhiteSpace(testPackageName) ? cleanClassName : $"{testPackageName}.{cleanClassName}";

                        latestEnvelope = await _evaluator.EvaluateSuiteAsync(
                            request.Target,
                            workspacePath,
                            testFqn,
                            currentTestSource!,
                            budget,
                            ct);

                        var jacocoXmlPath = $"{workspacePath}/target/site/jacoco/jacoco.xml";
                        await _artifactIndexWriter.RecordArtifactAsync(request.CampaignId, request.Target.SubjectId, request.RepetitionId, request.Treatment, FullChainStages.CommonEvaluation, "JaCoCoXml", jacocoXmlPath, ct);

                        state = _stateMachine.TransitionFromEvaluation(state);
                        await SaveCheckpointAsync(request, state, inputHash, testClassName, testPackageName, currentTestSource, planJson, planHash, scenarioToMethodMap, latestEnvelope, latestVerificationDecision, latestFailure, recordedArtifactPaths, ct);
                        break;
                    }

                    case FullChainStages.B2Verify:
                    {
                        Log($"[Stage: B2Verify] Running multi-agent blinded verification pass {state.VerificationPass}...");
                        var cleanClassName = testClassName?.Split('.').Last().Trim() ?? "GeneratedTest";
                        var testFqn = string.IsNullOrWhiteSpace(testPackageName) ? cleanClassName : $"{testPackageName}.{cleanClassName}";
                        var verificationRequest = new VerificationRequest(
                            Target: request.Target,
                            Evaluation: latestEnvelope!,
                            TestClassFqn: testFqn,
                            ArtifactRelativePath: $"src/test/java/{testFqn.Replace('.', '/')}.java",
                            CurrentVerificationPass: state.VerificationPass,
                            RemainingRepairRounds: Math.Max(0, _stateMachine.MaxRepairRounds - state.RepairRound),
                            ScenarioToMethodMap: scenarioToMethodMap,
                            PlanJson: planJson);
                        latestVerificationDecision = await _verifier.VerifyBlindedEnvelopeAsync(verificationRequest, budget, ct);

                        bool isLocalRepairEligible = latestVerificationDecision.ActionableRoute == VerificationRoutes.LocalTestRepair;
                        bool hasRemainingBudget = !budget.IsExhausted && state.RepairRound < _stateMachine.MaxRepairRounds;

                        if (isLocalRepairEligible && latestFailure == null)
                        {
                            latestFailure = CreateVerifierFailureEvidence(
                                latestVerificationDecision,
                                scenarioToMethodMap);
                        }

                        state = _stateMachine.TransitionFromVerification(
                            state,
                            latestVerificationDecision,
                            isLocalRepairEligible: isLocalRepairEligible,
                            hasRemainingBudget: hasRemainingBudget);
                        await SaveCheckpointAsync(request, state, inputHash, testClassName, testPackageName, currentTestSource, planJson, planHash, scenarioToMethodMap, latestEnvelope, latestVerificationDecision, latestFailure, recordedArtifactPaths, ct);
                        break;
                    }


                    default:
                        throw new InvalidOperationException($"Unhandled orchestrator stage: {state.CurrentStage}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ProviderFailureException ex)
        {
            Log($"[Stage Error] Provider failure in stage '{state.CurrentStage}': {ex.Message}");
            state = _stateMachine.TransitionToTerminal(state, TerminalStatus.ProviderFailure, $"Provider failure in {state.CurrentStage}: {ex.Message}");
        }
        catch (Exception ex)
        {
            Log($"[Stage Error] Unexpected exception in stage '{state.CurrentStage}': {ex.Message}");
            state = _stateMachine.TransitionToTerminal(state, TerminalStatus.ToolFailure, $"Unexpected failure in {state.CurrentStage}: {ex.Message}");
        }

        NotifyStage(state.CurrentStage, state.Status);

        // 4. Build FullChainRecord with Dual Accounting
        CandidateDiagnostic diagnostic;
        if (latestEnvelope != null)
        {
            diagnostic = new CandidateDiagnostic(
                Compiled: latestEnvelope.CompileSuccess,
                Executed: latestEnvelope.TestExecutionSuccess,
                BranchCoverage: latestEnvelope.BranchCoverage,
                MutationScore: latestEnvelope.MutationScore,
                StructuralTargetSatisfaction: latestEnvelope.StructuralTargetSatisfaction,
                HasMissingEvidence: false
            );
        }
        else
        {
            diagnostic = new CandidateDiagnostic(
                Compiled: false,
                Executed: false,
                BranchCoverage: 0.0,
                MutationScore: 0.0,
                StructuralTargetSatisfaction: 0.0,
                HasMissingEvidence: false
            );
        }

        DeliveredSystemOutcome outcome;
        if (string.Equals(state.CurrentStage, FullChainStages.Delivered, StringComparison.OrdinalIgnoreCase))
        {
            outcome = new DeliveredSystemOutcome(
                VerifiedSuiteRate: 1.0,
                CompilationSatisfactionRate: 1.0,
                ExecutionSatisfactionRate: 1.0,
                BranchCoverage: latestEnvelope!.BranchCoverage,
                MutationScore: latestEnvelope.MutationScore,
                StructuralTargetSatisfaction: latestEnvelope.StructuralTargetSatisfaction
            );
        }
        else
        {
            // Terminal failure/unverified delivers zero to production
            outcome = new DeliveredSystemOutcome(
                VerifiedSuiteRate: 0.0,
                CompilationSatisfactionRate: 0.0,
                ExecutionSatisfactionRate: 0.0,
                BranchCoverage: 0.0,
                MutationScore: 0.0,
                StructuralTargetSatisfaction: 0.0
            );
        }

        var runId = $"{request.Treatment}_{request.Target.SubjectId}_rep{request.RepetitionId}_{DateTime.UtcNow:yyyyMMddHHmmss}";
        var record = new FullChainRecord(
            SchemaVersion: "full-chain-record-v1",
            RunId: runId,
            SubjectId: request.Target.SubjectId,
            RepetitionId: request.RepetitionId,
            Treatment: request.Treatment,
            TerminalStatus: state.Status,
            TerminalReason: state.TerminalReason,
            Diagnostic: diagnostic,
            Outcome: outcome,
            TotalRepairRounds: state.RepairRound,
            TotalVerificationPasses: state.VerificationPass,
            TotalProviderRequests: budget.RequestsUsed,
            TotalTokensUsed: budget.TokensUsed,
            TotalWallClockSeconds: budget.TotalElapsedSeconds,
            WorkspaceDirectory: workspacePath,
            Timestamp: DateTime.UtcNow.ToString("o")
        );

        var artifactIndex = await _artifactIndexWriter.GetIndexAsync(
            request.CampaignId,
            request.Target.SubjectId,
            request.RepetitionId,
            request.Treatment,
            ct);

        Log($"[Complete] Run finished with terminal status '{state.Status}'. VerifiedSuiteRate: {outcome.VerifiedSuiteRate}, Diagnostic BC: {diagnostic.BranchCoverage:P1}");

        return new OrchestratorRunResult(
            Record: record,
            ArtifactIndex: artifactIndex,
            ResumedFromCheckpoint: resumedFromCheckpoint,
            TerminalStatus: state.Status,
            TerminalReason: state.TerminalReason
        );
    }

    private async Task SaveCheckpointAsync(
        OrchestratorRunRequest request,
        FullChainState state,
        string inputHash,
        string? testClassName,
        string? testPackageName,
        string? currentTestSource,
        string? planJson,
        string? planHash,
        IReadOnlyDictionary<string, string>? scenarioToMethodMap,
        CommonEnvelope? latestEnvelope,
        VerificationDecision? latestVerificationDecision,
        FailureEvidence? latestFailure,
        List<string> recordedArtifactPaths,
        CancellationToken ct)
    {
        var payload = new OrchestratorCheckpointPayload(
            State: state,
            TestClassName: testClassName,
            TestPackageName: testPackageName,
            CurrentTestSource: currentTestSource,
            PlanJson: planJson,
            PlanHash: planHash,
            ScenarioToMethodMap: scenarioToMethodMap,
            LatestEnvelope: latestEnvelope,
            LatestVerificationDecision: latestVerificationDecision,
            RecordedArtifactPaths: recordedArtifactPaths,
            LatestFailure: latestFailure,
            SchemaVersion: CurrentCheckpointPayloadSchemaVersion
        );

        var payloadJson = JsonSerializer.Serialize(payload, JsonOptions);
        var checkpoint = new CampaignCheckpoint(
            SchemaVersion: "campaign-checkpoint-v1",
            CampaignId: request.CampaignId,
            TargetId: request.Target.SubjectId,
            RepetitionId: request.RepetitionId,
            Treatment: request.Treatment,
            CurrentStage: state.CurrentStage,
            FullChainStateJson: payloadJson,
            InputHash: inputHash,
            CreatedAtUtc: DateTime.UtcNow.ToString("o")
        );


        await _checkpointStore.SaveCheckpointAsync(checkpoint, ct);
    }

    private static void ValidateCheckpointPayload(OrchestratorCheckpointPayload payload)
    {
        bool requiresTestSource = payload.State.CurrentStage is
            FullChainStages.CandidateCompileAndExecute or
            FullChainStages.Repair or
            FullChainStages.CommonEvaluation or
            FullChainStages.B2Verify;

        if (requiresTestSource && string.IsNullOrWhiteSpace(payload.CurrentTestSource))
        {
            throw new InvalidOperationException($"Checkpoint for stage '{payload.State.CurrentStage}' is missing CurrentTestSource.");
        }

        if (payload.State.CurrentStage == FullChainStages.Repair && payload.LatestFailure == null)
        {
            throw new InvalidOperationException("Checkpoint for REPAIR is missing failure evidence.");
        }

        if ((payload.State.CurrentStage is FullChainStages.B2Verify or FullChainStages.Delivered) && payload.LatestEnvelope == null)
        {
            throw new InvalidOperationException($"Checkpoint for {payload.State.CurrentStage} is missing evaluation evidence.");
        }
    }

    private static FailureEvidence CreateVerifierFailureEvidence(
        VerificationDecision decision,
        IReadOnlyDictionary<string, string>? scenarioToMethodMap)
    {
        string? scenarioId = null;
        string? failingMethod = null;
        var findings = string.Join(
            Environment.NewLine,
            decision.SpecialistFindings.Select(f => $"{f.SpecialistId}: {f.DetailedReason}"));

        if (scenarioToMethodMap?.Count > 0)
        {
            var mapping = scenarioToMethodMap
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .FirstOrDefault(pair => findings.Contains(pair.Value, StringComparison.OrdinalIgnoreCase)
                    || System.Text.RegularExpressions.Regex.IsMatch(
                        findings,
                        $@"(?<![A-Za-z0-9_]){System.Text.RegularExpressions.Regex.Escape(pair.Key)}(?![A-Za-z0-9_])",
                        System.Text.RegularExpressions.RegexOptions.CultureInvariant));

            if (string.IsNullOrWhiteSpace(mapping.Key))
            {
                mapping = scenarioToMethodMap.OrderBy(pair => pair.Key, StringComparer.Ordinal).First();
            }

            scenarioId = mapping.Key;
            failingMethod = mapping.Value;
        }

        return new FailureEvidence(
            FailureType: "VERIFIER",
            DiagnosticMessage: $"B2 requested {decision.ActionableRoute}: {findings}",
            FailingTestMethodName: failingMethod,
            AssociatedScenarioId: scenarioId,
            Stdout: string.Empty,
            Stderr: string.Empty,
            ExitCode: 0);
    }

    private void NotifyStage(string stage, string status)
    {
        StageChanged?.Invoke(stage, status);
    }

    private void Log(string message)
    {
        LogEmitted?.Invoke(message);
    }
}
