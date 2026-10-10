using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FullChain.Contracts;
using FullChain.Core;
using FullChain.Core.Evaluation;
using FullChain.Core.Orchestration;
using FullChain.Core.Verification;
using FullChain.Infrastructure.Evaluation;
using FullChain.Infrastructure.LLM;
using FullChain.Infrastructure.Persistence;
using FullChain.Infrastructure.Processes;
using FullChain.Infrastructure.Rq1;
using FullChain.Infrastructure.Rq3;
using FullChain.Infrastructure.Java;
using FullChain.Runner;
using FullChain.Wpf;

namespace FullChain.Tests;

/// <summary>
/// Deterministic regressions for protocol-critical behavior. These tests use
/// fakes only: no provider, Maven, EvoSuite, or dataset checkout is invoked.
/// </summary>
internal static class OfflineRegressionTests
{
    public static async Task<int> RunAsync()
    {
        var tests = new (string Name, Func<Task> Execute)[]
        {
            ("configuration rejects embedded secrets", TestConfigurationSecretRejectionAsync),
            ("API-key resolution precedence", TestApiKeyResolverAsync),
            ("RQ3 request preserves target and bounded repair", TestRq3ContractAsync),
            ("RQ1 generator receives finalized plan", TestRq1GeneratorPlanInjectionAsync),
            ("B2 repair receives finalized plan and mapped scenario", TestB2RepairContextAsync),
            ("PIT receives fully qualified generated test class", TestPitUsesFullyQualifiedTestClassAsync),
            ("focal coverage and B2 evidence remain target-scoped", TestFocalCoverageAndB2EvidenceAsync),
            ("checkpoint resumes candidate after generation", TestGenerationCheckpointResumeAsync),
            ("checkpoint resumes the persisted next stage", TestCheckpointResumeAsync),
            ("checkpoint resumes candidate with repaired source", TestRepairCheckpointResumeAsync),
            ("legacy checkpoint is rejected safely", TestLegacyCheckpointRejectionAsync),
            ("input-hash changes reject resume", TestInputHashRejectionAsync),
            ("RQ2 environment scope restores on all exits", TestScopedToolchainEnvironmentAsync),
            ("WPF run catalog reads saved campaign evidence", TestWpfRunCatalogAsync),
            ("credential gate leaves no campaign output", TestCampaignCredentialGateAsync)
        };

        var failed = 0;
        Console.WriteLine("=== FullChain offline regression tests ===");
        foreach (var test in tests)
        {
            try
            {
                await test.Execute();
                Console.WriteLine($"[PASS] {test.Name}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine($"[FAIL] {test.Name}: {ex.Message}");
            }
        }

        Console.WriteLine($"Offline regressions: {tests.Length - failed}/{tests.Length} passed.");
        return failed == 0 ? 0 : 1;
    }

    private static Task TestConfigurationSecretRejectionAsync()
    {
        const string json = """
        {
          "schemaVersion": "full-chain-config-v1",
          "experimentId": "offline-security-test",
          "mode": "Pilot",
          "cohortManifestPath": "missing-manifest.json",
          "datasetProjectsRoot": "missing-dataset",
          "outputRoot": "runs",
          "toolLockPath": "missing-tool-lock.json",
          "repetitions": [42],
          "budget": {
            "hardWallClockSeconds": 30,
            "fullChain": {
              "generationTimeoutSeconds": 1,
              "repairTotalTimeoutSeconds": 1,
              "evaluationTotalTimeoutSeconds": 1,
              "verificationTotalTimeoutSeconds": 1,
              "orchestrationTimeoutSeconds": 1,
              "maxRepairRounds": 2,
              "maxVerificationPasses": 3,
              "maxProviderRequests": 1,
              "maxTransientRetriesPerRequest": 0,
              "maxCumulativeTokens": 1
            },
            "evoSuite": {
              "searchTimeoutSeconds": 1,
              "evaluationTotalTimeoutSeconds": 1,
              "verificationTotalTimeoutSeconds": 1,
              "orchestrationTimeoutSeconds": 1
            }
          },
          "llm": {
            "provider": "openai",
            "endpoint": "https://example.invalid/v1",
            "modelId": "offline-test",
            "apiKey": "sk-not-allowed",
            "temperature": 0,
            "timeoutSeconds": 30
          },
          "tools": {
            "javaHome": "",
            "mavenHome": "",
            "evosuiteJar": "",
            "evosuiteRuntimeJar": "",
            "jacocoVersion": "0.8.15",
            "pitVersion": "1.25.3"
          }
        }
        """;
        var result = ConfigurationValidator.ValidateJson(json);
        Require(!result.IsValid && result.Errors.Any(error => error.Contains("Security violation", StringComparison.Ordinal)),
            "The validator accepted a raw API key.");
        return Task.CompletedTask;
    }

    private static async Task TestApiKeyResolverAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "full-chain-key-regression-" + Guid.NewGuid().ToString("N"));
        var keyFile = Path.Combine(directory, "key.txt");
        const string variableName = "FULL_CHAIN_OFFLINE_TEST_KEY";
        var originalValue = Environment.GetEnvironmentVariable(variableName);
        Directory.CreateDirectory(directory);

        try
        {
            await File.WriteAllTextAsync(keyFile, "file-key\n");
            Environment.SetEnvironmentVariable(variableName, "environment-key");
            var configuration = new LlmConfiguration("openai", "https://example.invalid/v1", "test", 0, 30, variableName, "key.txt");
            var resolver = new ApiKeyResolver(directory);

            Require(resolver.Resolve(configuration, " memory-key ") == "memory-key", "In-memory credential must take precedence.");
            Require(resolver.Resolve(configuration) == "environment-key", "Environment credential must take precedence over a file.");

            Environment.SetEnvironmentVariable(variableName, null);
            Require(resolver.Resolve(configuration) == "file-key", "Configured credential file was not used as final fallback.");

            var openRouterConfiguration = configuration with { Provider = "openrouter" };
            bool rejectedInvalidOpenRouterKey = false;
            try
            {
                ProviderCredentialValidator.EnsureUsable(openRouterConfiguration, "sk-generic-provider-key");
            }
            catch (ProviderFailureException)
            {
                rejectedInvalidOpenRouterKey = true;
            }
            Require(rejectedInvalidOpenRouterKey, "An OpenRouter credential with the wrong provider format was accepted.");
            ProviderCredentialValidator.EnsureUsable(openRouterConfiguration, "sk-or-v1-offline-test-key");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, originalValue);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static async Task TestRq3ContractAsync()
    {
        var target = CreateTarget();
        var request = new VerificationRequest(
            Target: target,
            Evaluation: CreateEnvelope(target),
            TestClassFqn: "sample.CalculatorTest",
            ArtifactRelativePath: "src/test/java/sample/CalculatorTest.java",
            CurrentVerificationPass: 1,
            RemainingRepairRounds: 2,
            ScenarioToMethodMap: new Dictionary<string, string> { ["S-01"] = "calculatesPositive" });
        var budget = new GlobalBudgetGovernor(CreateConfiguration().Budget);

        var clearClient = new SequenceLlmClient(
            "{\"status\":\"CLEAR\",\"cause\":\"NONE\",\"rationale\":\"evidence is consistent\"}",
            "{\"status\":\"CLEAR\",\"cause\":\"NONE\",\"rationale\":\"runtime is clean\"}",
            "{\"status\":\"CLEAR\",\"cause\":\"NONE\",\"rationale\":\"target is covered\"}");
        var adapter = new Rq3VerifierAdapter(clearClient);
        var verified = await adapter.VerifyBlindedEnvelopeAsync(request, budget);

        Require(verified.AggregateVerdict == VerificationVerdicts.Verified, "Three CLEAR RQ3 findings must yield VERIFIED.");
        Require(clearClient.UserPrompts.All(prompt => prompt.Contains("sample.Calculator", StringComparison.Ordinal)
            && prompt.Contains("calculate(int)", StringComparison.Ordinal)
            && prompt.Contains("src/test/java/sample/CalculatorTest.java", StringComparison.Ordinal)
            && prompt.Contains("\"current_verification_pass\": 1", StringComparison.Ordinal)
            && prompt.Contains("\"remaining_repair_rounds\": 2", StringComparison.Ordinal)),
            "RQ3 envelope did not retain real target identity, test path, and loop state.");

        var malformedClient = new SequenceLlmClient(
            "{\"status\":\"CLEAR\",\"cause\":\"WEAK_ORACLE\",\"rationale\":\"invalid combination\"}",
            "{\"status\":\"CLEAR\",\"cause\":\"NONE\",\"rationale\":\"runtime is clean\"}",
            "{\"status\":\"CLEAR\",\"cause\":\"NONE\",\"rationale\":\"target is covered\"}");
        var abstained = await new Rq3VerifierAdapter(malformedClient).VerifyBlindedEnvelopeAsync(request, new GlobalBudgetGovernor(CreateConfiguration().Budget));
        Require(abstained.AggregateVerdict == VerificationVerdicts.Abstain, "Malformed specialist output must abstain, not verify.");

        var detectedClient = new SequenceLlmClient(
            "{\"status\":\"CLEAR\",\"cause\":\"NONE\",\"rationale\":\"ok\"}",
            "{\"status\":\"CLEAR\",\"cause\":\"NONE\",\"rationale\":\"ok\"}",
            "{\"status\":\"DETECTED\",\"cause\":\"WEAK_ORACLE\",\"rationale\":\"assertion is weak\"}");
        var materialized = VerificationEnvelopeFactory.CreateBlindedEnvelope(request);
        var bounded = await new Rq3VerifierAdapter(detectedClient).VerifyEnvelopeAsync(
            materialized,
            currentVerificationPass: 3,
            remainingRepairRounds: 1,
            new GlobalBudgetGovernor(CreateConfiguration().Budget));
        Require(bounded.Decision == VerificationVerdicts.RepairRequired
            && bounded.Route == VerificationRoutes.LocalTestRepair,
            "A WEAK_ORACLE detection did not map to the actionable local-repair route.");
        Require(!bounded.CanLoopToRepair, "RQ3 must not request another repair after the maximum verification pass.");
    }

    private static async Task TestRq1GeneratorPlanInjectionAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "full-chain-rq1-plan-injection-" + Guid.NewGuid().ToString("N"));
        var contextDirectory = Path.Combine(root, "experiments", "rq1", "context");
        var targetDirectory = Path.Combine(root, "experiments", "rq1", "targets");
        var promptsDirectory = Path.Combine(root, "experiments", "rq1", "prompts");
        Directory.CreateDirectory(contextDirectory);
        Directory.CreateDirectory(targetDirectory);
        Directory.CreateDirectory(promptsDirectory);

        const string subjectId = "OFFLINE-RQ1";
        const string planJson = """
        {"subjectId":"OFFLINE-RQ1","scenarios":[{"scenarioId":"S01","technique":"BRANCH_OUTCOME","preconditions":["construct calculator"],"concreteInput":"value=1","expectedObservation":"returns true","oracleProvenance":{"type":"IMPLEMENTATION_DERIVED","sourceLines":[12]},"targetIds":["OFFLINE-001"]}]}
        """;
        const string generatorJson = """
        {"testClassName":"sample.CalculatorGeneratedTest","sourceCode":"package sample; public class CalculatorGeneratedTest { @org.junit.Test public void test_S01_positive() {} }","declaredTestMethods":["test_S01_positive"],"mappings":[{"scenarioId":"S01","testClass":"sample.CalculatorGeneratedTest","testMethod":"test_S01_positive"}]}
        """;

        try
        {
            await File.WriteAllTextAsync(Path.Combine(contextDirectory, "contexts-official.jsonl"),
                "{\"subjectId\":\"OFFLINE-RQ1\",\"packageName\":\"sample\"}\n");
            await File.WriteAllTextAsync(Path.Combine(targetDirectory, "targets-official.jsonl"),
                "{\"subjectId\":\"OFFLINE-RQ1\",\"targets\":[]}\n");
            await File.WriteAllTextAsync(Path.Combine(promptsDirectory, "planner-system.md"), "planner");
            await File.WriteAllTextAsync(Path.Combine(promptsDirectory, "planner-user.md"), "{{COMMON_CONTEXT_JSON}}");
            await File.WriteAllTextAsync(Path.Combine(promptsDirectory, "generator-system.md"), "generator");
            // Exercise the historical spelling as well: it must not silently drop the plan.
            await File.WriteAllTextAsync(Path.Combine(promptsDirectory, "generator-user.md"),
                "context={{COMMON_CONTEXT_JSON}} plan={{SCENARIO_PLAN_JSON}} class={{REQUIRED_TEST_CLASS_FQN}}");

            var client = new SequenceLlmClient(planJson, generatorJson);
            var adapter = new Rq1PlannedGenerationAdapter(new Rq1ContextAdapter(root), client);
            var target = CreateTarget() with { SubjectId = subjectId };
            var artifact = await adapter.GenerateTestsAsync(target, "", new GlobalBudgetGovernor(CreateConfiguration().Budget));

            Require(client.UserPrompts.Count == 2, "RQ1 adapter did not issue planner and generator calls.");
            Require(client.UserPrompts[1].Contains(planJson, StringComparison.Ordinal)
                && !client.UserPrompts[1].Contains("{{", StringComparison.Ordinal),
                "RQ1 generator prompt omitted the finalized plan or retained an unresolved token.");
            Require(artifact.ScenarioToMethodMap.TryGetValue("S01", out var method)
                && method == "test_S01_positive",
                "RQ1 adapter did not preserve the validated scenario-to-test mapping.");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task TestB2RepairContextAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "full-chain-b2-repair-context-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        const string planJson = """
        {"subjectId":"OFFLINE-001","scenarios":[{"scenarioId":"S01","technique":"BRANCH_OUTCOME","preconditions":[],"concreteInput":"value=1","expectedObservation":"returns true","oracleProvenance":{"type":"IMPLEMENTATION_DERIVED","sourceLines":[12]},"targetIds":["OFFLINE-001"]},{"scenarioId":"S02","technique":"BRANCH_OUTCOME","preconditions":[],"concreteInput":"value=0","expectedObservation":"returns false","oracleProvenance":{"type":"IMPLEMENTATION_DERIVED","sourceLines":[13]},"targetIds":["OFFLINE-001"]}]}
        """;

        try
        {
            var target = CreateTarget();
            var repair = new FakeRepairExecutor();
            var generator = new FakeGenerator(planJson, new Dictionary<string, string>
            {
                ["S01"] = "test_S01_positive",
                ["S02"] = "test_S02_zero"
            });
            var verifier = new SequenceVerifier(
                new VerificationDecision(
                    VerificationVerdicts.RepairRequired,
                    VerificationRoutes.LocalTestRepair,
                    [new FullChain.Contracts.SpecialistFinding("CONFORMANCE", "FAIL", 1, "Scenario S02 does not preserve the finalized input.")],
                    "offline-repair-required", 0, DateTime.UtcNow.ToString("o")),
                new VerificationDecision(
                    VerificationVerdicts.Verified,
                    VerificationRoutes.None,
                    Array.Empty<FullChain.Contracts.SpecialistFinding>(),
                    "offline-verified", 0, DateTime.UtcNow.ToString("o")));
            var orchestrator = CreateOrchestrator(
                root,
                new FakeCandidateRunner(
                    new CandidateExecutionResult(true, true, "ok", null),
                    new CandidateExecutionResult(true, true, "ok", null)),
                repair,
                new CheckpointStore(Path.Combine(root, "checkpoints")),
                new ArtifactIndexWriter(root),
                verifier,
                generator);

            var result = await orchestrator.RunAsync(
                new OrchestratorRunRequest(CreateConfiguration(), target, 42, CreateToolLock(), "offline-b2-repair-context"),
                new GlobalBudgetGovernor(CreateConfiguration().Budget));

            Require(result.TerminalStatus == TerminalStatus.Succeeded && repair.Calls == 1,
                "B2 repair-context regression did not complete the expected single repair loop.");
            Require(repair.LastFinalizedPlanJson == planJson
                && repair.LastFailure?.AssociatedScenarioId == "S02"
                && repair.LastFailure.FailingTestMethodName == "test_S02_zero",
                "B2 repair did not receive the finalized plan and scenario-specific mapping.");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task TestPitUsesFullyQualifiedTestClassAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "full-chain-pit-test-selector-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "pom.xml"),
                "<project xmlns=\"http://maven.apache.org/POM/4.0.0\"><modelVersion>4.0.0</modelVersion><groupId>sample</groupId><artifactId>sample</artifactId><version>1</version></project>");
            var runner = new CapturingEvaluationProcessRunner();
            var evaluator = new GeneratedSuiteEvaluator(
                runner,
                new ResolvedToolchain("/offline/jdk", "/offline/jdk/bin/java", "/offline/maven/bin/mvn", "evosuite.jar", "runtime.jar"));
            var target = CreateTarget();

            await evaluator.EvaluateSuiteAsync(
                target,
                root,
                "sample.CalculatorGeneratedTest",
                "package sample; public class CalculatorGeneratedTest { @org.junit.Test public void generated() {} }",
                new GlobalBudgetGovernor(CreateConfiguration().Budget));

            Require(runner.Arguments.Count == 2
                && runner.Arguments[1].Contains("-DtargetTests=sample.CalculatorGeneratedTest", StringComparison.Ordinal),
                "PIT invocation used a simple test name and would execute zero package-scoped tests.");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static Task TestFocalCoverageAndB2EvidenceAsync()
    {
        var target = CreateTarget() with
        {
            FocalClass = "sample.Calculator",
            StartLine = 10,
            EndLine = 18,
            Obligations = new[]
            {
                new BranchObligation("OFFLINE-BRANCH", "BRANCH_SITE", "OFFICIAL", "ALL_JACOCO_BRANCHES_AT_SOURCE_LINE", 10),
                new BranchObligation("OFFLINE-RETURN", "RETURN_PATH", "DIAGNOSTIC", "SOURCE_MAPPED_PATH_OBLIGATION", 18)
            }
        };
        const string jacocoXml = """
        <report>
          <package name="">
            <sourcefile name="Calculator.java">
              <line nr="10" mi="0" ci="3" mb="1" cb="1"/>
              <line nr="18" mi="0" ci="1" mb="0" cb="0"/>
            </sourcefile>
            <sourcefile name="Unrelated.java">
              <line nr="10" mi="0" ci="100" mb="0" cb="100"/>
              <line nr="18" mi="0" ci="100" mb="0" cb="100"/>
            </sourcefile>
          </package>
        </report>
        """;
        var jacoco = JacocoParser.ParseFocalCoverage(jacocoXml, target.FocalClass, target.StartLine, target.EndLine);
        Require(jacoco.CoveredBranches == 1 && jacoco.MissedBranches == 1 && jacoco.BranchCoverage == 50.0,
            "Focal JaCoCo parsing included a sibling source file instead of the exact focal source.");

        var structural = StructuralTargetEvaluator.Evaluate(
            target.Obligations,
            jacoco.LineCoveredBranches,
            jacoco.LineCoveredInstructions);
        Require(structural.SatisfactionPercentage == 100.0,
            "Return-path satisfaction must use covered instructions, not branch counters.");

        const string pitXml = """
        <mutations>
          <mutation detected="true" status="KILLED"><mutatedClass>sample.Calculator</mutatedClass><lineNumber>10</lineNumber><mutator>NEGATE_CONDITIONALS</mutator><description>negated conditional</description><killingTest>CalculatorTest.test_S01()</killingTest></mutation>
          <mutation detected="false" status="SURVIVED"><mutatedClass>sample.Calculator</mutatedClass><lineNumber>18</lineNumber><mutator>EMPTY_RETURNS</mutator><description>replaced return value</description></mutation>
          <mutation detected="false" status="SURVIVED"><mutatedClass>sample.Unrelated</mutatedClass><lineNumber>10</lineNumber><mutator>EMPTY_RETURNS</mutator><description>must be excluded</description></mutation>
        </mutations>
        """;
        var pit = PitParser.ParseFocalMutations(pitXml, target.FocalClass, target.StartLine, target.EndLine);
        Require(pit.TotalMutants == 2 && pit.KilledMutants == 1 && pit.SurvivedMutants == 1 && pit.FocalMutations.Count == 2,
            "PIT focal mutation evidence was not filtered to the focal class and line range.");

        var obligationEvidence = target.Obligations.Select(obligation => new ObligationCoverageEvidence(
            obligation.TargetId,
            obligation.Type,
            obligation.Line,
            jacoco.LineCoveredBranches.TryGetValue(obligation.Line, out var coveredBranches) ? coveredBranches : 0,
            jacoco.LineMissedBranches.TryGetValue(obligation.Line, out var missedBranches) ? missedBranches : 0,
            jacoco.LineCoveredInstructions.TryGetValue(obligation.Line, out var coveredInstructions) ? coveredInstructions : 0,
            jacoco.LineMissedInstructions.TryGetValue(obligation.Line, out var missedInstructions) ? missedInstructions : 0,
            structural.ObligationStatus[obligation.TargetId])).ToArray();
        var envelope = CreateEnvelope(target) with
        {
            BranchCoverage = jacoco.BranchCoverage,
            MutationScore = pit.MutationScore,
            StructuralTargetSatisfaction = structural.SatisfactionPercentage,
            JaCoCoReportXml = jacocoXml,
            PitReportXml = pitXml,
            ObligationCoverage = obligationEvidence,
            FocalMutations = pit.FocalMutations,
            MutationEvidenceAvailable = true
        };
        var planJson = """
        {"subjectId":"OFFLINE-001","scenarios":[{"scenarioId":"S01","technique":"BRANCH_OUTCOME","preconditions":[],"concreteInput":"x=1","expectedObservation":"returns one","oracleProvenance":{"type":"IMPLEMENTATION_DERIVED","sourceLines":[10]},"targetIds":["OFFLINE-BRANCH"]}]}
        """;
        var request = new VerificationRequest(
            target,
            envelope,
            "sample.CalculatorTest",
            "src/test/java/sample/CalculatorTest.java",
            1,
            2,
            new Dictionary<string, string> { ["S01"] = "test_S01" },
            planJson);
        var materialized = VerificationEnvelopeFactory.CreateBlindedEnvelope(request);

        Require(materialized.EnvelopeJson.Contains("PLANNED_TARGET_IDS_ONLY", StringComparison.Ordinal)
            && materialized.EnvelopeJson.Contains("OFFLINE-BRANCH", StringComparison.Ordinal)
            && materialized.EnvelopeJson.Contains("EMPTY_RETURNS", StringComparison.Ordinal),
            "B2 envelope omitted the planned target scope or focal PIT mutation evidence.");
        return Task.CompletedTask;
    }

    private static async Task TestCheckpointResumeAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "full-chain-resume-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var target = CreateTarget();
            var configuration = CreateConfiguration();
            var toolLock = CreateToolLock();
            const string campaignId = "offline-resume";
            const int repetition = 42;
            var checkpoints = new CheckpointStore(Path.Combine(root, "checkpoints"));
            var artifactIndex = new ArtifactIndexWriter(root);
            var initialRepair = new FakeRepairExecutor();
            var firstRun = CreateOrchestrator(
                root,
                new FakeCandidateRunner(new CandidateExecutionResult(
                    CompileSuccess: false,
                    ExecutionSuccess: false,
                    BuildLogSnippet: "CalculatorTest.calculatesPositive failed",
                    Failure: new FailureEvidence("COMPILE", "CalculatorTest.calculatesPositive failed", "calculatesPositive", "S-01", "", "", 1))),
                initialRepair,
                checkpoints,
                artifactIndex);

            using var cancellation = new CancellationTokenSource();
            firstRun.StageChanged += (stage, _) =>
            {
                if (stage == FullChainStages.Repair)
                {
                    cancellation.Cancel();
                }
            };

            try
            {
                await firstRun.RunAsync(
                    new OrchestratorRunRequest(configuration, target, repetition, toolLock, campaignId),
                    new GlobalBudgetGovernor(configuration.Budget),
                    cancellation.Token);
                throw new InvalidOperationException("The controlled cancellation did not occur at REPAIR.");
            }
            catch (OperationCanceledException)
            {
                // Expected: the persisted checkpoint must already name REPAIR as its next stage.
            }

            var persisted = await checkpoints.LoadCheckpointAsync(campaignId, target.SubjectId, repetition, "FullChain");
            Require(persisted?.CurrentStage == FullChainStages.Repair, "Candidate failure checkpoint did not persist REPAIR as the next stage.");
            Require(persisted!.FullChainStateJson.Contains("latestFailure", StringComparison.Ordinal), "Repair checkpoint omitted failure evidence.");
            Require(initialRepair.Calls == 0, "Repair ran before the controlled resume point.");

            var resumedRepair = new FakeRepairExecutor();
            var resumedVerifier = new FakeVerifier(VerificationVerdicts.Verified);
            var resumed = CreateOrchestrator(
                root,
                new FakeCandidateRunner(new CandidateExecutionResult(true, true, "ok", null)),
                resumedRepair,
                checkpoints,
                artifactIndex,
                resumedVerifier);
            var resumedResult = await resumed.RunAsync(
                new OrchestratorRunRequest(configuration, target, repetition, toolLock, campaignId),
                new GlobalBudgetGovernor(configuration.Budget));

            Require(resumedResult.ResumedFromCheckpoint, "Second run did not report a checkpoint resume.");
            Require(resumedRepair.Calls == 1, "Resume skipped the persisted repair stage.");
            Require(resumedResult.TerminalStatus == TerminalStatus.Succeeded, "Resumed run failed to reach the verified terminal state.");
            Require(resumedVerifier.Calls == 1, "Verifier should run exactly once before delivery.");

            var deliveredResume = CreateOrchestrator(
                root,
                new FakeCandidateRunner(new CandidateExecutionResult(true, true, "should not execute", null)),
                new FakeRepairExecutor(),
                checkpoints,
                artifactIndex,
                resumedVerifier);
            var deliveredResult = await deliveredResume.RunAsync(
                new OrchestratorRunRequest(configuration, target, repetition, toolLock, campaignId),
                new GlobalBudgetGovernor(configuration.Budget));

            Require(deliveredResult.ResumedFromCheckpoint, "Delivered checkpoint was not recognized for resume.");
            Require(resumedVerifier.Calls == 1, "Delivered checkpoint incorrectly repeated B2 verification.");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task TestGenerationCheckpointResumeAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "full-chain-generation-checkpoint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var configuration = CreateConfiguration();
            var target = CreateTarget();
            var toolLock = CreateToolLock();
            const string campaignId = "generation-checkpoint";
            var checkpoints = new CheckpointStore(Path.Combine(root, "checkpoints"));
            var artifactIndex = new ArtifactIndexWriter(root);
            var initialGenerator = new FakeGenerator();
            var firstRun = CreateOrchestrator(
                root,
                new FakeCandidateRunner(new CandidateExecutionResult(true, true, "unused", null)),
                new FakeRepairExecutor(),
                checkpoints,
                artifactIndex,
                generator: initialGenerator);
            using var cancellation = new CancellationTokenSource();
            firstRun.StageChanged += (stage, _) =>
            {
                if (stage == FullChainStages.CandidateCompileAndExecute)
                {
                    cancellation.Cancel();
                }
            };

            await ExpectCancellationAsync(() => firstRun.RunAsync(
                new OrchestratorRunRequest(configuration, target, 42, toolLock, campaignId),
                new GlobalBudgetGovernor(configuration.Budget),
                cancellation.Token));

            var checkpoint = await checkpoints.LoadCheckpointAsync(campaignId, target.SubjectId, 42, "FullChain");
            Require(checkpoint?.CurrentStage == FullChainStages.CandidateCompileAndExecute,
                "Generation checkpoint did not name candidate compilation as the next stage.");
            Require(initialGenerator.Calls == 1, "Initial generation did not execute exactly once.");

            var resumedGenerator = new FakeGenerator();
            var resumedCandidate = new FakeCandidateRunner(new CandidateExecutionResult(true, true, "ok", null));
            var resumed = CreateOrchestrator(root, resumedCandidate, new FakeRepairExecutor(), checkpoints, artifactIndex, generator: resumedGenerator);
            var result = await resumed.RunAsync(
                new OrchestratorRunRequest(configuration, target, 42, toolLock, campaignId),
                new GlobalBudgetGovernor(configuration.Budget));

            Require(result.ResumedFromCheckpoint && result.TerminalStatus == TerminalStatus.Succeeded,
                "Generation checkpoint did not resume to a verified terminal result.");
            Require(resumedCandidate.Calls == 1 && resumedGenerator.Calls == 0,
                "Resume re-ran generation or skipped candidate compilation.");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task TestRepairCheckpointResumeAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "full-chain-repair-checkpoint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var configuration = CreateConfiguration();
            var target = CreateTarget();
            var toolLock = CreateToolLock();
            const string campaignId = "repair-checkpoint";
            var checkpoints = new CheckpointStore(Path.Combine(root, "checkpoints"));
            var artifactIndex = new ArtifactIndexWriter(root);
            var firstCandidate = new FakeCandidateRunner(new CandidateExecutionResult(
                false,
                false,
                "CalculatorTest.calculatesPositive failed",
                new FailureEvidence("COMPILE", "CalculatorTest.calculatesPositive failed", "calculatesPositive", "S-01", "", "", 1)));
            var completedRepair = new FakeRepairExecutor("package sample; class CalculatorTest { void repairedScenario() {} }");
            var firstRun = CreateOrchestrator(root, firstCandidate, completedRepair, checkpoints, artifactIndex);
            var candidateStageVisits = 0;
            using var cancellation = new CancellationTokenSource();
            firstRun.StageChanged += (stage, _) =>
            {
                if (stage == FullChainStages.CandidateCompileAndExecute && ++candidateStageVisits == 2)
                {
                    cancellation.Cancel();
                }
            };

            await ExpectCancellationAsync(() => firstRun.RunAsync(
                new OrchestratorRunRequest(configuration, target, 42, toolLock, campaignId),
                new GlobalBudgetGovernor(configuration.Budget),
                cancellation.Token));

            var checkpoint = await checkpoints.LoadCheckpointAsync(campaignId, target.SubjectId, 42, "FullChain");
            Require(checkpoint?.CurrentStage == FullChainStages.CandidateCompileAndExecute,
                "Repair checkpoint did not name candidate compilation as the next stage.");
            Require(completedRepair.Calls == 1, "Initial repair did not execute exactly once.");

            var resumedCandidate = new FakeCandidateRunner(new CandidateExecutionResult(true, true, "ok", null));
            var resumedRepair = new FakeRepairExecutor();
            var result = await CreateOrchestrator(root, resumedCandidate, resumedRepair, checkpoints, artifactIndex).RunAsync(
                new OrchestratorRunRequest(configuration, target, 42, toolLock, campaignId),
                new GlobalBudgetGovernor(configuration.Budget));

            Require(result.ResumedFromCheckpoint && result.TerminalStatus == TerminalStatus.Succeeded,
                "Repair checkpoint did not resume to a verified terminal result.");
            Require(resumedCandidate.LastTestSource.Contains("repairedScenario", StringComparison.Ordinal),
                "Resume did not compile the repaired source persisted in the checkpoint.");
            Require(resumedRepair.Calls == 0, "Resume repeated a repair that had already completed.");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task TestLegacyCheckpointRejectionAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "full-chain-legacy-checkpoint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var configuration = CreateConfiguration();
            var target = CreateTarget();
            var toolLock = CreateToolLock();
            var checkpoints = new CheckpointStore(Path.Combine(root, "checkpoints"));
            const string campaignId = "legacy-checkpoint";
            var legacyPayload = new OrchestratorCheckpointPayload(
                FullChainStateMachine.CreateInitialState(),
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                new List<string>(),
                SchemaVersion: "full-chain-orchestrator-checkpoint-v1");
            var legacyCheckpoint = new CampaignCheckpoint(
                "campaign-checkpoint-v1",
                campaignId,
                target.SubjectId,
                42,
                "FullChain",
                FullChainStages.PlannedGeneration,
                JsonSerializer.Serialize(legacyPayload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
                FullChainOrchestrator.ComputeInputHash(configuration, target, toolLock),
                DateTime.UtcNow.ToString("o"));
            await checkpoints.SaveCheckpointAsync(legacyCheckpoint);

            var orchestrator = CreateOrchestrator(
                root,
                new FakeCandidateRunner(new CandidateExecutionResult(true, true, "unused", null)),
                new FakeRepairExecutor(),
                checkpoints,
                new ArtifactIndexWriter(root));
            var rejected = false;
            try
            {
                await orchestrator.RunAsync(
                    new OrchestratorRunRequest(configuration, target, 42, toolLock, campaignId),
                    new GlobalBudgetGovernor(configuration.Budget));
            }
            catch (InvalidOperationException exception) when (exception.Message.Contains("cannot be resumed safely", StringComparison.Ordinal))
            {
                rejected = true;
            }

            Require(rejected, "A legacy checkpoint was not rejected with an explicit migration error.");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task TestInputHashRejectionAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "full-chain-input-hash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var configuration = CreateConfiguration();
            var target = CreateTarget();
            var toolLock = CreateToolLock();
            const string campaignId = "input-hash-checkpoint";
            var checkpoints = new CheckpointStore(Path.Combine(root, "checkpoints"));
            var payload = new OrchestratorCheckpointPayload(
                FullChainStateMachine.CreateInitialState(), null, null, null, null, null, null, null, null, new List<string>(),
                SchemaVersion: "full-chain-orchestrator-checkpoint-v2");
            await checkpoints.SaveCheckpointAsync(new CampaignCheckpoint(
                "campaign-checkpoint-v1", campaignId, target.SubjectId, 42, "FullChain", FullChainStages.PlannedGeneration,
                JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
                FullChainOrchestrator.ComputeInputHash(configuration, target, toolLock), DateTime.UtcNow.ToString("o")));

            var changedConfiguration = configuration with { Llm = configuration.Llm with { ModelId = "different-model" } };
            var orchestrator = CreateOrchestrator(
                root,
                new FakeCandidateRunner(new CandidateExecutionResult(true, true, "unused", null)),
                new FakeRepairExecutor(),
                checkpoints,
                new ArtifactIndexWriter(root));
            var rejected = false;
            try
            {
                await orchestrator.RunAsync(
                    new OrchestratorRunRequest(changedConfiguration, target, 42, toolLock, campaignId),
                    new GlobalBudgetGovernor(changedConfiguration.Budget));
            }
            catch (InvalidOperationException exception) when (exception.Message.Contains("input hash mismatch", StringComparison.OrdinalIgnoreCase))
            {
                rejected = true;
            }

            Require(rejected, "Resume accepted a checkpoint produced by a different input configuration.");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static Task TestScopedToolchainEnvironmentAsync()
    {
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var originalJavaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        var toolchain = new ResolvedToolchain(
            "/offline/jdk", "/offline/jdk/bin/java", "/offline/maven/bin/mvn", "evosuite.jar", "runtime.jar");

        try
        {
            Environment.SetEnvironmentVariable("PATH", "offline-original-path");
            Environment.SetEnvironmentVariable("JAVA_HOME", "offline-original-java-home");

            using (ScopedToolchainEnvironment.Apply(toolchain))
            {
                Require(Environment.GetEnvironmentVariable("JAVA_HOME") == toolchain.JavaHome, "Scoped environment did not apply JAVA_HOME.");
            }
            Require(Environment.GetEnvironmentVariable("PATH") == "offline-original-path"
                && Environment.GetEnvironmentVariable("JAVA_HOME") == "offline-original-java-home",
                "Scoped environment did not restore after normal completion.");

            try
            {
                using var scope = ScopedToolchainEnvironment.Apply(toolchain);
                throw new InvalidOperationException("controlled scope failure");
            }
            catch (InvalidOperationException exception) when (exception.Message == "controlled scope failure")
            {
                // Expected; disposal still happens before this handler observes the failure.
            }
            Require(Environment.GetEnvironmentVariable("PATH") == "offline-original-path"
                && Environment.GetEnvironmentVariable("JAVA_HOME") == "offline-original-java-home",
                "Scoped environment did not restore after failure.");

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try
            {
                using var scope = ScopedToolchainEnvironment.Apply(toolchain);
                cancellation.Token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                // Expected; disposal still happens before this handler observes cancellation.
            }
            Require(Environment.GetEnvironmentVariable("PATH") == "offline-original-path"
                && Environment.GetEnvironmentVariable("JAVA_HOME") == "offline-original-java-home",
                "Scoped environment did not restore after cancellation.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Environment.SetEnvironmentVariable("JAVA_HOME", originalJavaHome);
        }

        return Task.CompletedTask;
    }

    private static async Task TestWpfRunCatalogAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "full-chain-wpf-run-catalog-" + Guid.NewGuid().ToString("N"));
        var campaignDirectory = Path.Combine(root, "experiments", "full-chain", "runs", "catalog-fixture");
        Directory.CreateDirectory(campaignDirectory);

        try
        {
            var diagnostic = new CandidateDiagnostic(true, true, 50.0, 40.0, 100.0, false);
            var outcome = new DeliveredSystemOutcome(1.0, 1.0, 1.0, 50.0, 40.0, 100.0);
            var records = new[]
            {
                new FullChainRecord("full-chain-record-v1", "fullchain-fixture", "PT-O-001", 42, "FullChain", "Succeeded", "VerifiedByB2", diagnostic, outcome, 0, 1, 1, 10, 1.5, campaignDirectory, "2026-01-01T00:00:00Z"),
                new FullChainRecord("full-chain-record-v1", "evosuite-fixture", "PT-O-001", 42, "EvoSuite", "Succeeded", "VerifiedByB2", diagnostic, outcome, 0, 1, 0, 0, 1.0, campaignDirectory, "2026-01-01T00:00:01Z")
            };
            var rawRecords = string.Join(Environment.NewLine, records.Select(record => JsonSerializer.Serialize(record))) + Environment.NewLine;
            await File.WriteAllTextAsync(Path.Combine(campaignDirectory, "raw-records.jsonl"), rawRecords);
            await File.WriteAllTextAsync(Path.Combine(campaignDirectory, "availability-ledger.json"), """
                {
                  "schemaVersion": "full-chain-availability-ledger-v1",
                  "campaignId": "catalog-fixture",
                  "timestamp": "2026-01-01T00:00:02Z",
                  "totalExpectedPairs": 1,
                  "completedPairs": 1,
                  "missingEvidenceCount": 0,
                  "isHeadlineAnalysisEligible": true
                }
                """);

            var viewModel = new MainWindowViewModel(root);
            Require(viewModel.Runs.Count == 1, "WPF catalog did not discover the campaign directory.");
            var selectedRun = viewModel.SelectedRun ?? throw new InvalidOperationException("WPF catalog did not select the discovered run.");
            Require(selectedRun.CampaignId == "catalog-fixture", "WPF catalog selected the wrong run.");
            Require(selectedRun.FullChainB2 == "1/1" && selectedRun.EvoSuiteB2 == "1/1",
                $"WPF catalog did not summarize B2 verdicts from raw records (FullChain={selectedRun.FullChainB2}, EvoSuite={selectedRun.EvoSuiteB2}, rows={viewModel.StageRows.Count}).");
            Require(selectedRun.Readiness == "Ready for headline analysis", "WPF catalog did not read headline eligibility from the ledger.");
            Require(viewModel.StageRows.Count == 2, "Selecting a catalog run did not load its stage records.");

            var originalId = viewModel.CampaignId;
            viewModel.CreateNewCampaignId();
            Require(viewModel.CampaignId.StartsWith("wpf-run-", StringComparison.Ordinal) && viewModel.CampaignId != "catalog-fixture",
                "WPF did not prepare a fresh campaign ID for a new run.");
            Require(!string.IsNullOrWhiteSpace(originalId), "WPF did not initialize a campaign ID.");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task TestCampaignCredentialGateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "full-chain-campaign-credential-gate-" + Guid.NewGuid().ToString("N"));
        const string environmentVariable = "FULL_CHAIN_OFFLINE_MISSING_KEY";
        var originalEnvironmentValue = Environment.GetEnvironmentVariable(environmentVariable);
        Directory.CreateDirectory(root);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "manifest.json"), "{}");
            await File.WriteAllTextAsync(Path.Combine(root, "tool-lock.json"), "{}");
            Directory.CreateDirectory(Path.Combine(root, "datasets"));
            Environment.SetEnvironmentVariable(environmentVariable, null);

            var configuration = CreateConfiguration() with
            {
                CohortManifestPath = "manifest.json",
                DatasetProjectsRoot = "datasets",
                ToolLockPath = "tool-lock.json",
                Llm = new LlmConfiguration(
                    "openrouter",
                    "https://openrouter.ai/api/v1/chat/completions",
                    "offline-test",
                    0,
                    30,
                    environmentVariable,
                    "missing-key.txt")
            };
            var outputDirectory = Path.Combine(root, "campaign-output");
            var service = new FullChainCampaignService(root);
            var rejected = false;
            try
            {
                await service.RunAsync(new FullChainCampaignRequest(
                    configuration,
                    CreateToolLock(),
                    new[] { CreateTarget() },
                    new[] { 42 },
                    "credential-gate",
                    outputDirectory));
            }
            catch (ProviderFailureException)
            {
                rejected = true;
            }

            Require(rejected, "Campaign service accepted a missing OpenRouter credential.");
            Require(!Directory.Exists(outputDirectory), "Credential rejection created a partial campaign output directory.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentVariable, originalEnvironmentValue);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static FullChainOrchestrator CreateOrchestrator(
        string root,
        ICandidateTestRunner candidateRunner,
        ITestRepairExecutor repairExecutor,
        ICheckpointStore checkpoints,
        IArtifactIndexWriter artifactIndex,
        IVerifierAdapter? verifier = null,
        ITestGenerator? generator = null)
    {
        return new FullChainOrchestrator(
            new FullChainStateMachine(),
            generator ?? new FakeGenerator(),
            candidateRunner,
            repairExecutor,
            new FakeEvaluator(),
            verifier ?? new FakeVerifier(VerificationVerdicts.Verified),
            checkpoints,
            artifactIndex,
            new FakeWorkspaceManager(root));
    }

    private static TargetDescriptor CreateTarget() => new(
        "OFFLINE-001", "sample-project", "revision-1", "sample-project", ".", "src/main/java/sample/Calculator.java",
        "sample.Calculator", "calculate(int)", 10, 20, 11, 2, "LOW", "public", false, "source-sha", "calculator behavior",
        new[] { new BranchObligation("OFFLINE-001", "BRANCH", "TARGET", "x > 0", 12) });

    private static CommonEnvelope CreateEnvelope(TargetDescriptor target) => new(
        "common-envelope-v1", target.SubjectId, target.ProjectId, target.SourceSha256, "test-source-sha",
        "package sample; class CalculatorTest { void calculatesPositive() {} }", target.Obligations, true, true,
        "build passed", 1.0, 1.0, 1.0, "<report/>", "<report/>");

    private static FullChainConfiguration CreateConfiguration() => new(
        "full-chain-config-v1", "offline-regression", "Pilot", "manifest.json", "datasets", "runs", "tool-lock.json", new[] { 42 },
        new BudgetPolicy(300, new FullChainBudgetPolicy(30, 30, 30, 30, 30, 2, 3, 20, 0, 10000), new EvoSuiteBudgetPolicy(30, 30, 30, 30)),
        new LlmConfiguration("openai", "https://example.invalid/v1", "offline-test", 0, 30),
        new ToolConfiguration("", "", "", "", "0.8.15", "1.25.3"));

    private static ToolLock CreateToolLock() => new(
        "tool-lock-v1", "now", new ToolLockEnvironment(
            new ToolLockJava("17", "test", new ToolPathEntry("java", "hash"), new ToolPathEntry("java", "hash")),
            new ToolLockMaven("3", "maven", "mvn", "hash"),
            new ToolLockEvoSuite("1", new ToolPathEntry("evosuite.jar", "hash"), new ToolPathEntry("runtime.jar", "hash")),
            new ToolLockJacoco("org.jacoco", "jacoco", "0.8.15", "hash"),
            new ToolLockPit("org.pitest", "pitest", "1.25.3", "plugin", "hash")));

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static async Task ExpectCancellationAsync(Func<Task> operation)
    {
        try
        {
            await operation();
            throw new InvalidOperationException("The controlled cancellation did not occur.");
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }
    }

    private sealed class SequenceLlmClient : ILlmClient
    {
        private readonly Queue<string> _responses;
        public List<string> UserPrompts { get; } = new();

        public SequenceLlmClient(params string[] responses) => _responses = new Queue<string>(responses);

        public Task<LlmResponse> SendPromptAsync(string systemPrompt, string userPrompt, IGlobalBudgetGovernor budget, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No scripted RQ3 response remains.");
            }

            UserPrompts.Add(userPrompt);
            budget.RecordUsage("offline-rq3", 1, 1, 0);
            return Task.FromResult(new LlmResponse(_responses.Dequeue(), 1, 1, 0, "offline"));
        }
    }

    private sealed class FakeGenerator : ITestGenerator
    {
        private readonly string _planJson;
        private readonly IReadOnlyDictionary<string, string> _mappings;
        public int Calls { get; private set; }

        public FakeGenerator(
            string planJson = "{}",
            IReadOnlyDictionary<string, string>? mappings = null)
        {
            _planJson = planJson;
            _mappings = mappings ?? new Dictionary<string, string> { ["S-01"] = "calculatesPositive" };
        }

        public Task<GeneratedTestArtifact> GenerateTestsAsync(TargetDescriptor target, string contextJson, IGlobalBudgetGovernor budget, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(new GeneratedTestArtifact(
                "CalculatorTest", "sample", "package sample; class CalculatorTest { void calculatesPositive() {} }", "test-source-sha", _planJson, "plan-hash",
                _mappings, 0, 0, 0));
        }
    }

    private sealed class FakeCandidateRunner : ICandidateTestRunner
    {
        private readonly Queue<CandidateExecutionResult> _results;
        public int Calls { get; private set; }
        public string LastTestSource { get; private set; } = string.Empty;

        public FakeCandidateRunner(params CandidateExecutionResult[] results) => _results = new Queue<CandidateExecutionResult>(results);

        public Task<CandidateExecutionResult> CompileAndExecuteCandidateAsync(TargetDescriptor target, string workspacePath, string testClassFqn, string testSourceCode, IGlobalBudgetGovernor budget, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            LastTestSource = testSourceCode;
            if (_results.Count == 0)
            {
                throw new InvalidOperationException("Candidate runner was invoked unexpectedly.");
            }

            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class FakeRepairExecutor : ITestRepairExecutor
    {
        private readonly string? _repairedSource;
        public int Calls { get; private set; }
        public string? LastFinalizedPlanJson { get; private set; }
        public FailureEvidence? LastFailure { get; private set; }

        public FakeRepairExecutor(string? repairedSource = null) => _repairedSource = repairedSource;

        public Task<RepairArtifact?> AttemptRepairAsync(TargetDescriptor target, string workspacePath, string currentTestSource, FailureEvidence failure, string? finalizedPlanJson, int roundNumber, IGlobalBudgetGovernor budget, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            LastFinalizedPlanJson = finalizedPlanJson;
            LastFailure = failure;
            var repairedSource = _repairedSource ?? currentTestSource;
            return Task.FromResult<RepairArtifact?>(new RepairArtifact(roundNumber, "TGSLR", "calculatesPositive", currentTestSource, repairedSource, repairedSource, "repaired-source-sha", 0, 0, true, "offline"));
        }
    }

    private sealed class FakeEvaluator : ICommonEvaluator
    {
        public Task<CommonEnvelope> EvaluateSuiteAsync(TargetDescriptor target, string workspacePath, string testClassFqn, string testSourceCode, IGlobalBudgetGovernor budget, CancellationToken ct = default)
        {
            return Task.FromResult(CreateEnvelope(target) with { TestSourceCode = testSourceCode });
        }
    }

    private sealed class FakeVerifier : IVerifierAdapter
    {
        private readonly string _verdict;
        public int Calls { get; private set; }

        public FakeVerifier(string verdict) => _verdict = verdict;

        public Task<VerificationDecision> VerifyBlindedEnvelopeAsync(VerificationRequest request, IGlobalBudgetGovernor budget, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(new VerificationDecision(_verdict, VerificationRoutes.None, Array.Empty<FullChain.Contracts.SpecialistFinding>(), "offline-hash", 0, DateTime.UtcNow.ToString("o")));
        }
    }

    private sealed class SequenceVerifier : IVerifierAdapter
    {
        private readonly Queue<VerificationDecision> _decisions;

        public SequenceVerifier(params VerificationDecision[] decisions) => _decisions = new Queue<VerificationDecision>(decisions);

        public Task<VerificationDecision> VerifyBlindedEnvelopeAsync(VerificationRequest request, IGlobalBudgetGovernor budget, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (_decisions.Count == 0)
            {
                throw new InvalidOperationException("No scripted B2 decision remains.");
            }

            return Task.FromResult(_decisions.Dequeue());
        }
    }

    private sealed class CapturingEvaluationProcessRunner : IProcessRunner
    {
        public List<string> Arguments { get; } = new();

        public Task<ProcessResult> RunAsync(
            string executable,
            string arguments,
            string workingDirectory,
            IReadOnlyDictionary<string, string>? environment = null,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Arguments.Add(arguments);

            if (Arguments.Count == 1)
            {
                var jacocoDirectory = Path.Combine(workingDirectory, "target", "site", "jacoco");
                Directory.CreateDirectory(jacocoDirectory);
                File.WriteAllText(Path.Combine(jacocoDirectory, "jacoco.xml"),
                    "<report><package name=\"sample\"><sourcefile name=\"Calculator.java\"><line nr=\"12\" mi=\"0\" ci=\"1\" mb=\"0\" cb=\"1\"/></sourcefile></package></report>");
            }
            else
            {
                var pitDirectory = Path.Combine(workingDirectory, "target", "pit-reports");
                Directory.CreateDirectory(pitDirectory);
                File.WriteAllText(Path.Combine(pitDirectory, "mutations.xml"),
                    "<mutations><mutation detected=\"true\" status=\"KILLED\"><mutatedClass>sample.Calculator</mutatedClass><lineNumber>12</lineNumber><mutator>NEGATE_CONDITIONALS</mutator><description>negated conditional</description></mutation></mutations>");
            }

            return Task.FromResult(new ProcessResult(
                executable,
                arguments,
                workingDirectory,
                0,
                "T E S T S\nResults:",
                string.Empty,
                TimeSpan.Zero,
                false,
                false));
        }
    }

    private sealed class FakeWorkspaceManager : IWorkspaceManager
    {
        private readonly string _root;
        public FakeWorkspaceManager(string root) => _root = root;

        public Task<string> PrepareIsolatedWorkspaceAsync(string projectPath, string campaignId, string targetId, int repetitionId, string treatment, string stage, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var workspace = Path.Combine(_root, "workspace");
            Directory.CreateDirectory(workspace);
            return Task.FromResult(workspace);
        }

        public Task CleanupWorkspaceAsync(string workspacePath, CancellationToken ct = default) => Task.CompletedTask;
    }
}
