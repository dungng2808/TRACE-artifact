using Rq1Experiment.Contracts.Config;
using Rq1Experiment.Contracts.Experiments;
using Rq1Experiment.Contracts.Generation;
using Rq1Experiment.Contracts.Prompts;
using Rq1Experiment.Contracts.Subjects;
using Rq1Experiment.Contracts.Targets;
using Rq1Experiment.Core.Abstractions;
using Rq1Experiment.Core.Config;
using Rq1Experiment.Core.Llm;
using Rq1Experiment.Infrastructure.Llm.Common;
using Rq1Experiment.Infrastructure.Llm.Codex;
using Rq1Experiment.Infrastructure.Java;
using Rq1Experiment.Infrastructure.Generation;
using Rq1Experiment.Infrastructure.Orchestration;
using Rq1Experiment.Infrastructure.Persistence;
using Rq1Experiment.Infrastructure.Prompts;

if (args.Length >= 2 && args[0].Equals("--preflight", StringComparison.OrdinalIgnoreCase))
    return await RunRepositoryPreflightAsync(args);
if (args.Length >= 1 && args[0].Equals("--select-subjects", StringComparison.OrdinalIgnoreCase))
    return await RunSubjectSelectionAsync(args);
if (args.Length >= 1 && args[0].Equals("--extract-targets", StringComparison.OrdinalIgnoreCase))
    return await RunStructuralTargetsAsync(args);
if (args.Length >= 1 && args[0].Equals("--build-context-prompts", StringComparison.OrdinalIgnoreCase))
    return await RunRepositoryContextsAndPromptsAsync(args);
if (args.Length >= 1 && args[0].Equals("--run-direct", StringComparison.OrdinalIgnoreCase))
    return await RunDirectBaselineAsync(args);
if (args.Length >= 1 && args[0].Equals("--run-planned", StringComparison.OrdinalIgnoreCase))
    return await RunPlannedPipelineAsync(args);
if (args.Length >= 1 && args[0].Equals("--rehydrate-v4-generator-requests", StringComparison.OrdinalIgnoreCase))
    return await RehydrateV4GeneratorRequestsAsync(args);
if (args.Length >= 1 && args[0].Equals("--run-tools", StringComparison.OrdinalIgnoreCase))
    return await RunToolExecutionAsync(args);
if (args.Length >= 1 && args[0].Equals("--rebuild-ledger", StringComparison.OrdinalIgnoreCase))
    return await RebuildMetricsLedgerAsync(args);

var testRoot = Path.Combine(Path.GetTempPath(), "rq1-experiment-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);

try
{
    var layout = new WorkspaceLayout(testRoot);
    var relocatedDataset = Path.Combine(testRoot, "datasets", "rq1", "projecttest", "java-ready", "projects", "Train");
    Assert(WorkspaceLayout.ResolveRepositoryPath(testRoot, "datasets/projecttest/java-ready/projects/Train") == relocatedDataset
           && WorkspaceLayout.ResolveRepositoryPath(testRoot, @"Y:\old\paper-2\datasets\projecttest\java-ready\projects\Train") == relocatedDataset
           && WorkspaceLayout.ResolveRepositoryPath(testRoot, "datasets/rq1/projecttest/java-ready/projects/Train") == relocatedDataset,
        "Historical and current dataset paths resolve inside the current RQ1 checkout");
    Assert(WorkspaceLayout.ResolveRepositoryPath(testRoot, testRoot) == testRoot,
        "Explicit repositories outside the dataset layout retain their location");
    var keyStore = new FileApiKeyStore(layout);

    var configStore = new JsonExperimentConfigStore(layout);
    var storedConfig = new ExperimentConfig { ExperimentId = "round-trip", RepositoryRoot = testRoot };
    storedConfig.Llm.Provider = ProviderIds.OpenRouter;
    storedConfig.Llm.ModelId = "provider/model";
    await configStore.SaveAsync(storedConfig);
    var loadedConfig = await configStore.LoadAsync();
    Assert(loadedConfig.ExperimentId == "round-trip" && loadedConfig.Llm.ModelId == "provider/model",
        "Experiment config round-trips through JSON persistence");
    storedConfig.ExperimentId = "second-experiment";
    await configStore.SaveAsync(storedConfig);
    var knownExperiments = await configStore.ListExperimentIdsAsync();
    var reopenedFirst = await configStore.LoadExperimentAsync("round-trip");
    Assert(knownExperiments.Contains("round-trip") && knownExperiments.Contains("second-experiment")
           && reopenedFirst?.Llm.ModelId == "provider/model",
        "Experiment config history can reopen an older experiment after the active config changes");

    var parsed = ApiKeyParser.Parse(" API1\r\n\nAPI2\nAPI1\n API3 ");
    Assert(parsed.SequenceEqual(["API1", "API2", "API3"]), "API key parser preserves order and removes blanks/duplicates");

    await keyStore.SaveAsync(ProviderIds.Gemini, parsed);
    var reloaded = await keyStore.LoadAsync(ProviderIds.Gemini);
    Assert(reloaded.SequenceEqual(parsed), "API key plaintext persistence round-trips");
    await keyStore.SaveAsync(ProviderIds.OpenRouter, ["OR1"]);
    await keyStore.SaveAsync(ProviderIds.JustWoker, ["JW1"]);
    Assert((await keyStore.LoadAsync(ProviderIds.Gemini)).Count == 3
           && (await keyStore.LoadAsync(ProviderIds.OpenRouter)).Single() == "OR1"
           && (await keyStore.LoadAsync(ProviderIds.JustWoker)).Single() == "JW1",
        "Provider API-key files remain isolated");

    Directory.CreateDirectory(Path.GetDirectoryName(layout.ModelCatalogPath)!);
    await File.WriteAllTextAsync(layout.ModelCatalogPath,
        "{\"gemini\":[\"g-model\"],\"deepseek\":[\"d-model\"],\"openrouter\":[\"o/model\"],\"justwoker\":[\"claude-opus-5-thinking\"]}");
    var modelCatalog = new JsonModelCatalogService(layout);
    Assert((await modelCatalog.GetModelsAsync(ProviderIds.DeepSeek)).Single() == "d-model",
        "Model catalog loads provider-specific exact model IDs");
    Assert((await modelCatalog.GetModelsAsync(ProviderIds.JustWoker)).Single() == "claude-opus-5-thinking",
        "JustWoker model catalog loads the configured experiment model");

    var remoteCatalog = new JsonModelCatalogService(layout, new HttpClient(new ModelListHandler()));
    var geminiModels = await remoteCatalog.RefreshAsync(ProviderIds.Gemini, null, "GKEY");
    var deepSeekModels = await remoteCatalog.RefreshAsync(ProviderIds.DeepSeek, null, "DKEY");
    var openRouterModels = await remoteCatalog.RefreshAsync(ProviderIds.OpenRouter, null, "OKEY");
    var justWokerModels = await remoteCatalog.RefreshAsync(ProviderIds.JustWoker, null, "JWKEY");
    Assert(geminiModels.IsSuccess && geminiModels.Models.SequenceEqual(["gemini-generate"]),
        "Gemini Reload Models parses generateContent models");
    Assert(deepSeekModels.IsSuccess && deepSeekModels.Models.SequenceEqual(["deepseek-chat"]),
        "DeepSeek Reload Models parses /models response");
    Assert(openRouterModels.IsSuccess && openRouterModels.Models.SequenceEqual(["provider/model"]),
        "OpenRouter Reload Models parses /models/user response");
    Assert(justWokerModels.IsSuccess && justWokerModels.Models.SequenceEqual(["claude-opus-5-thinking"]),
        "JustWoker Reload Models parses the OpenAI-compatible /models response");

    var pool = new SequentialApiKeyPool(keyStore, layout);
    var first = await pool.AcquireAsync(ProviderIds.Gemini);
    Assert(first?.SlotId == "KEY_001", "Sequential pool starts at KEY_001");
    await pool.MarkQuotaLimitedAsync(first!);
    var second = await pool.AcquireAsync(ProviderIds.Gemini);
    Assert(second?.SlotId == "KEY_002", "Quota failover advances to KEY_002");

    var rotationRoot = Path.Combine(testRoot, "round-robin-keys");
    var rotationLayout = new WorkspaceLayout(rotationRoot);
    var rotationStore = new FileApiKeyStore(rotationLayout);
    await rotationStore.SaveAsync(ProviderIds.Gemini, ["R1", "R2", "R3"]);
    var rotationPool = new SequentialApiKeyPool(rotationStore, rotationLayout);
    var rotation = new List<string?>();
    for (var index = 0; index < 4; index++)
        rotation.Add((await rotationPool.AcquireAsync(ProviderIds.Gemini))?.SlotId);
    Assert(rotation.SequenceEqual(["KEY_001", "KEY_002", "KEY_003", "KEY_001"]),
        "API-key pool rotates every call and wraps to KEY_001");
    await rotationPool.MarkQuotaLimitedAsync(new ApiKeyLease(ProviderIds.Gemini, "KEY_001", "R1"));
    var afterQuota = await rotationPool.AcquireAsync(ProviderIds.Gemini);
    Assert(afterQuota?.SlotId == "KEY_002",
        "Quota-exhausted key is excluded while healthy round-robin keys remain");

    var phaseStore = new JsonPhaseProgressStore(layout);
    var phases = await phaseStore.LoadAsync("rq1-test");
    Assert(phases.Phases.Count == 13, "Phase progress initializes all 13 phases");
    phases.Phases[1].IsCompleted = true;
    await phaseStore.SaveAsync(phases);
    var phasesReloaded = await phaseStore.LoadAsync("rq1-test");
    Assert(phasesReloaded.Phases[1].IsCompleted, "Phase checkbox state persists");
    await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => phaseStore.SaveAsync(phasesReloaded)));
    var concurrentPhaseJson = await File.ReadAllTextAsync(layout.PhaseProgressPath);
    Assert(System.Text.Json.JsonSerializer.Deserialize<Rq1Experiment.Contracts.Progress.PhaseProgressDocument>(
               concurrentPhaseJson,
               new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) is not null
           && !Directory.EnumerateFiles(Path.GetDirectoryName(layout.PhaseProgressPath)!, "*.tmp").Any(),
        "Concurrent phase saves remain atomic and leave no temporary-file race");

    var config = new ExperimentConfig
    {
        ExperimentId = "rq1-test",
        RepositoryRoot = testRoot,
        OutputRoot = Path.Combine(testRoot, "experiments", "rq1")
    };
    config.Llm.Provider = ProviderIds.Gemini;
    config.Llm.ModelId = "test-model";
    var missingSmokeFocal = ExperimentConfigValidator.Validate(config, 3);
    Assert(!missingSmokeFocal.IsValid && missingSmokeFocal.Errors.Any(error => error.Contains("Smoke focal method")),
        "Smoke configuration cannot run before an analyzed focal method is selected");
    config.Subjects.SmokeCandidateKey = "fixture|Example|method()|1";
    var validation = ExperimentConfigValidator.Validate(config, 3);
    Assert(validation.IsValid
           && config.Generation.MaxOutputTokensPerCall == 65_536
           && config.Generation.TotalTokenCeilingPerRun == 65_536
           && config.Llm.TimeoutSeconds == 180
           && config.Llm.DeepSeekThinkingMode == "enabled",
        "Experiment config defaults to the Gemini 3.5 maximum output and a matched per-run ceiling");

    var factory = new LlmClientFactory();
    Assert(factory.Create(ProviderIds.Gemini).ProviderId == ProviderIds.Gemini, "Gemini adapter contract resolves");
    Assert(factory.Create(ProviderIds.DeepSeek).ProviderId == ProviderIds.DeepSeek, "DeepSeek adapter contract resolves");
    Assert(factory.Create(ProviderIds.OpenRouter).ProviderId == ProviderIds.OpenRouter, "OpenRouter adapter contract resolves");
    Assert(factory.Create(ProviderIds.JustWoker).ProviderId == ProviderIds.JustWoker, "JustWoker adapter contract resolves");
    foreach (var provider in ProviderIds.All)
    {
        var client = factory.Create(provider);
        foreach (var role in new[] { "DIRECT_GENERATOR", "STRUCTURAL_PLANNER", "PLANNED_GENERATOR" })
        {
            var request = client.CreateRequest("frozen-model", role, "system", "user");
            Assert(request.ProviderId == provider && request.ModelId == "frozen-model",
                $"{provider} preserves selected provider/model for {role}");
        }
    }
    var geminiStructuredRequest = LlmRequestFactory.Configure(
        factory.Create(ProviderIds.Gemini).CreateRequest(
            "gemini-3.5-flash", "STRUCTURAL_PLANNER", "system", "user"),
        string.Empty, 0, 16384, transportRetryCount: 2);
    Assert(geminiStructuredRequest.ThinkingLevel == "minimal"
           && !string.IsNullOrWhiteSpace(geminiStructuredRequest.ResponseJsonSchema)
           && geminiStructuredRequest.TransportRetryCount == 2,
        "Gemini 3 generation uses minimal thinking, role JSON Schema and bounded transport retries");
    var deepSeekHandler = new CompletionHandler();
    var deepSeekTransport = new HttpLlmTransport(new HttpClient(deepSeekHandler));
    var deepSeekNonThinkingRequest = LlmRequestFactory.Configure(
        factory.Create(ProviderIds.DeepSeek).CreateRequest(
            "deepseek-v4-flash", "STRUCTURAL_PLANNER", "strict json", "generate json"),
        string.Empty, 0, 1024, deepSeekThinkingMode: "disabled");
    var deepSeekResult = await deepSeekTransport.SendAsync(
        deepSeekNonThinkingRequest, new ApiKeyLease(ProviderIds.DeepSeek, "KEY_001", "SECRET"));
    using (var deepSeekBody = System.Text.Json.JsonDocument.Parse(deepSeekHandler.LastRequestBody))
    {
        Assert(deepSeekResult.Outcome == LlmTransportOutcome.Success
               && deepSeekNonThinkingRequest.DeepSeekThinkingMode == "disabled"
               && deepSeekBody.RootElement.GetProperty("thinking").GetProperty("type").GetString() == "disabled",
            "DeepSeek request explicitly transmits the frozen thinking-mode treatment");
    }
    var completionHandler = new CompletionHandler();
    var httpTransport = new HttpLlmTransport(new HttpClient(completionHandler));
    foreach (var provider in ProviderIds.All.Where(provider => provider != ProviderIds.CodexCli))
    {
        var configured = LlmRequestFactory.Configure(
            factory.Create(provider).CreateRequest("model-id", "DIRECT_GENERATOR", "strict json", "generate json"),
            string.Empty, 0, 256);
        var transportResult = await httpTransport.SendAsync(configured, new ApiKeyLease(provider, "KEY_001", "SECRET"));
        Assert(transportResult.Outcome == LlmTransportOutcome.Success
               && transportResult.ResponseText == "{\"ok\":true}"
               && transportResult.TotalTokens == 30,
            $"Phase 6 {provider} transport parses content/model/token usage from the official response shape");
    }
    using (var justWokerBody = System.Text.Json.JsonDocument.Parse(completionHandler.LastRequestBody))
    {
        Assert(justWokerBody.RootElement.GetProperty("stream").GetBoolean()
               && justWokerBody.RootElement.GetProperty("stream_options")
                   .GetProperty("include_usage").GetBoolean(),
            "JustWoker request enables SSE streaming and requests usage in the final chunk");
    }
    var justWokerStreamingTransport = new HttpLlmTransport(new HttpClient(new StreamingCompletionHandler()));
    var justWokerStreamingRequest = LlmRequestFactory.Configure(
        factory.Create(ProviderIds.JustWoker).CreateRequest(
            "claude-opus-5-thinking", "DIRECT_GENERATOR", "strict json", "generate json"),
        string.Empty, 0, 256);
    var justWokerStreamingResult = await justWokerStreamingTransport.SendAsync(
        justWokerStreamingRequest, new ApiKeyLease(ProviderIds.JustWoker, "KEY_001", "SECRET"));
    Assert(justWokerStreamingResult.Outcome == LlmTransportOutcome.Success
           && justWokerStreamingResult.ResponseText == "{\"ok\":true}"
           && justWokerStreamingResult.ResponseModelId == "claude-opus-5-thinking"
           && justWokerStreamingResult.TotalTokens == 30,
        "JustWoker SSE chunks are combined and preserve model, finish reason, and token usage");
    var rpmTransport = new HttpLlmTransport(new HttpClient(new ErrorResponseHandler(
        System.Net.HttpStatusCode.TooManyRequests,
        "{\"error\":{\"message\":\"Quota exceeded for GenerateRequestsPerMinutePerProjectPerModel\"}}")));
    var dailyTransport = new HttpLlmTransport(new HttpClient(new ErrorResponseHandler(
        System.Net.HttpStatusCode.TooManyRequests,
        "{\"error\":{\"message\":\"Quota exceeded for GenerateRequestsPerDayPerProjectPerModel-FreeTier\"}}")));
    var quotaClassifierRequest = factory.Create(ProviderIds.Gemini)
        .CreateRequest("model-id", "DIRECT_GENERATOR", "system", "user");
    var rpmResult = await rpmTransport.SendAsync(
        quotaClassifierRequest, new ApiKeyLease(ProviderIds.Gemini, "KEY_001", "SECRET"));
    var dailyResult = await dailyTransport.SendAsync(
        quotaClassifierRequest, new ApiKeyLease(ProviderIds.Gemini, "KEY_001", "SECRET"));
    Assert(rpmResult.Outcome == LlmTransportOutcome.RateLimited,
        "Gemini per-minute 429 remains transient and does not exhaust the key");
    Assert(dailyResult.Outcome == LlmTransportOutcome.QuotaLimited,
        "Gemini explicit per-day 429 is classified as quota exhaustion");

    var officialConfig = new ExperimentConfig
    {
        ExperimentId = "rq1-official-lock-test",
        Mode = "Official",
        RepositoryRoot = testRoot,
        OutputRoot = Path.Combine(testRoot, "official-output")
    };
    officialConfig.Llm.Provider = ProviderIds.Gemini;
    officialConfig.Llm.ModelId = "frozen-model";
    var lockService = new OfficialConfigLockService(layout);
    var createdLock = await lockService.ValidateOrCreateAsync(officialConfig);
    var matchingLock = await lockService.ValidateOrCreateAsync(officialConfig);
    officialConfig.Llm.ModelId = "changed-model";
    var rejectedLock = await lockService.ValidateOrCreateAsync(officialConfig);
    var secondOfficialConfig = new ExperimentConfig
    {
        ExperimentId = "rq1-official-lock-test-2",
        Mode = "Official",
        RepositoryRoot = testRoot,
        OutputRoot = Path.Combine(testRoot, "official-output")
    };
    secondOfficialConfig.Llm.Provider = ProviderIds.Gemini;
    secondOfficialConfig.Llm.ModelId = "changed-model";
    var secondExperimentLock = await lockService.ValidateOrCreateAsync(secondOfficialConfig);
    Assert(createdLock.IsAccepted && createdLock.WasCreated && matchingLock.IsAccepted
           && !rejectedLock.IsAccepted && secondExperimentLock.IsAccepted && secondExperimentLock.WasCreated,
        "Official lock is scoped per experiment, accepts frozen config, and rejects in-place changes");

    var failoverRoot = Path.Combine(testRoot, "request-preserving-failover");
    Directory.CreateDirectory(failoverRoot);
    var failoverLayout = new WorkspaceLayout(failoverRoot);
    var failoverKeyStore = new FileApiKeyStore(failoverLayout);
    await failoverKeyStore.SaveAsync(ProviderIds.Gemini, ["F1", "F2"]);
    var failoverPool = new SequentialApiKeyPool(failoverKeyStore, failoverLayout);
    var transport = new SequenceTransport(
        LlmTransportOutcome.QuotaLimited,
        LlmTransportOutcome.Success);
    var failoverClient = new FailoverLlmClient(failoverPool, transport);
    var originalRequest = factory.Create(ProviderIds.Gemini)
        .CreateRequest("frozen-model", "DIRECT_GENERATOR", "same-system", "same-user");
    var failoverResult = await failoverClient.SendAsync(originalRequest);
    Assert(failoverResult.IsSuccess
           && failoverResult.SemanticLlmCalls == 1
           && failoverResult.TransportAttempts == 2
           && failoverResult.KeySlots.SequenceEqual(["KEY_001", "KEY_002"])
           && transport.RequestHashes.Distinct(StringComparer.Ordinal).Single() == originalRequest.RequestHash,
        "Quota failover preserves request hash, increments transport attempts, and keeps one semantic call");

    var transientRoot = Path.Combine(testRoot, "transient-retry");
    var transientLayout = new WorkspaceLayout(transientRoot);
    var transientStore = new FileApiKeyStore(transientLayout);
    await transientStore.SaveAsync(ProviderIds.Gemini, ["T1", "T2"]);
    var transientTransport = new SequenceTransport(
        LlmTransportOutcome.TransientFailure,
        LlmTransportOutcome.Success);
    var transientResult = await new FailoverLlmClient(
        new SequentialApiKeyPool(transientStore, transientLayout), transientTransport)
        .SendAsync(geminiStructuredRequest with { TransportRetryCount = 1 });
    Assert(transientResult.IsSuccess && transientResult.TransportAttempts == 2
           && transientResult.KeySlots.SequenceEqual(["KEY_001", "KEY_002"]),
        "Transient 503-style failure retries once with the next round-robin key");

    var orchestrator = new ScaffoldExperimentOrchestrator(layout);
    var evidence = new List<PipelineProgress>();
    var run = await orchestrator.RunAsync(config, new Progress<PipelineProgress>(item => evidence.Add(item)), CancellationToken.None);
    Assert(run.IsCompleted, "Scaffold pipeline completes");
    Assert(File.Exists(Path.Combine(config.OutputRoot, "runs", config.ExperimentId, "scaffold-smoke", "run.json")),
        "Scaffold pipeline creates immutable run metadata");
    var artifactRows = await new RunArtifactReader().ReadAsync(config.OutputRoot);
    Assert(artifactRows.Count == 1
           && artifactRows[0].ExperimentId == config.ExperimentId
           && artifactRows[0].ProviderId == ProviderIds.Gemini,
        "Results artifact reader rebuilds a dummy result from immutable run.json");

    var runArtifacts = Directory.EnumerateFiles(
            Path.Combine(config.OutputRoot, "runs", config.ExperimentId),
            "*",
            SearchOption.AllDirectories)
        .Where(path => !path.Contains(Path.Combine("data", "api-keys"), StringComparison.Ordinal))
        .Select(File.ReadAllText);
    Assert(runArtifacts.All(text => !text.Contains("API1", StringComparison.Ordinal)),
        "Raw API keys do not leak into run artifacts");

    config.ExperimentId = "rq1-subject-failure";
    var subjectFailureStates = new List<PipelineProgress>();
    var subjectFailureOrchestrator = new ScaffoldExperimentOrchestrator(
        layout,
        subjectSelectionService: new ThrowingSubjectSelectionService());
    var subjectFailure = await subjectFailureOrchestrator.RunAsync(
        config,
        new InlineProgress<PipelineProgress>(subjectFailureStates.Add),
        CancellationToken.None);
    Assert(!subjectFailure.IsCompleted
           && subjectFailure.Message.StartsWith("PHASE_3_FAILED", StringComparison.Ordinal)
           && subjectFailureStates.Any(item => item.StageId == "subjects" && item.State == PipelineStageState.Failed),
        "Subject-selection infrastructure errors transition the stage from RUNNING to FAILED");
    Assert(new ScaffoldExperimentOrchestrator(layout).CanResume,
        "A fresh WPF process detects the persisted resumable checkpoint");

    config.ExperimentId = "rq1-target-failure";
    var targetFailureStates = new List<PipelineProgress>();
    var targetFailure = await new ScaffoldExperimentOrchestrator(
            layout,
            structuralTargetService: new ThrowingStructuralTargetService())
        .RunAsync(config, new InlineProgress<PipelineProgress>(targetFailureStates.Add), CancellationToken.None);
    Assert(!targetFailure.IsCompleted
           && targetFailure.Message.StartsWith("PHASE_4_FAILED", StringComparison.Ordinal)
           && targetFailureStates.Any(item => item.StageId == "targets" && item.State == PipelineStageState.Failed),
        "Structural-target errors transition the real Phase 4 stage to FAILED");

    var pausable = new ScaffoldExperimentOrchestrator(layout);
    config.ExperimentId = "rq1-pause-resume";
    var pausingRun = pausable.RunAsync(config, new InlineProgress<PipelineProgress>(_ => { }), CancellationToken.None);
    await Task.Delay(100);
    pausable.PauseAfterCurrentStage();
    var paused = await pausingRun;
    Assert(!paused.IsCompleted && !paused.IsCancelled && pausable.CanResume,
        "Pause waits for the current stage and leaves a resumable checkpoint");
    var resumed = await pausable.ResumeAsync(config, new InlineProgress<PipelineProgress>(_ => { }), CancellationToken.None);
    Assert(resumed.IsCompleted, "Paused scaffold pipeline resumes to completion");

    var cancellable = new ScaffoldExperimentOrchestrator(layout);
    config.ExperimentId = "rq1-cancel-resume";
    using var cancellation = new CancellationTokenSource(100);
    var cancelled = await cancellable.RunAsync(
        config,
        new InlineProgress<PipelineProgress>(_ => { }),
        cancellation.Token);
    Assert(cancelled.IsCancelled && cancellable.CanResume,
        "Cancellation saves a resumable checkpoint even during the first stage");
    var resumedAfterCancel = await cancellable.ResumeAsync(
        config,
        new InlineProgress<PipelineProgress>(_ => { }),
        CancellationToken.None);
    Assert(resumedAfterCancel.IsCompleted, "Cancelled scaffold pipeline resumes to completion");

    await keyStore.SaveAsync(ProviderIds.DeepSeek, ["D1", "D2"]);
    var deepSeekFirst = await pool.AcquireAsync(ProviderIds.DeepSeek);
    await pool.MarkQuotaLimitedAsync(deepSeekFirst!);
    var deepSeekSecond = await pool.AcquireAsync(ProviderIds.DeepSeek);
    await pool.MarkQuotaLimitedAsync(deepSeekSecond!);
    config.ExperimentId = "rq1-waiting-for-key";
    config.Llm.Provider = ProviderIds.DeepSeek;
    var waitingOrchestrator = new ScaffoldExperimentOrchestrator(layout, pool);
    var waitingStates = new List<PipelineProgress>();
    var waiting = await waitingOrchestrator.RunAsync(
        config,
        new InlineProgress<PipelineProgress>(waitingStates.Add),
        CancellationToken.None);
    Assert(!waiting.IsCompleted
           && waitingOrchestrator.CanResume
           && waitingStates.Any(item => item.State == PipelineStageState.WaitingForApiKey),
        "Exhausted provider pool moves pipeline to WAITING_FOR_API_KEY");
    await keyStore.SaveAsync(ProviderIds.DeepSeek, ["D1", "D2", "D3"]);
    var resumedWithNewKey = await waitingOrchestrator.ResumeAsync(
        config,
        new InlineProgress<PipelineProgress>(_ => { }),
        CancellationToken.None);
    Assert(resumedWithNewKey.IsCompleted, "Adding a provider key resumes the waiting request");

    var fakeRepository = Path.Combine(testRoot, "fake-maven-project");
    Directory.CreateDirectory(Path.Combine(fakeRepository, "src", "main", "java", "example"));
    await File.WriteAllTextAsync(Path.Combine(fakeRepository, "pom.xml"), "<project />");
    await File.WriteAllTextAsync(
        Path.Combine(fakeRepository, "src", "main", "java", "example", "Example.java"),
        "package example; public final class Example { public int value() { return 1; } }");
    var preflightOutput = Path.Combine(testRoot, "preflight-output");
    var fakeRunner = new FakePreflightProcessRunner();
    var preflightService = new RepositoryPreflightService(fakeRunner);
    var preflight = await preflightService.RunAsync(new PreflightRequest(
        "FAKE-01",
        fakeRepository,
        preflightOutput,
        30,
        30,
        30,
        30,
        "0.8.15",
        "1.25.3"));
    Assert(preflight.Eligible
           && preflight.CleanCompile == "PASS"
           && preflight.ExistingTests.SequenceEqual(["PASS", "PASS", "PASS"])
           && preflight.JacocoSmoke == "PASS"
           && preflight.PitSmoke == "PASS",
        "Phase 2 preflight accepts only after compile, three test runs, JaCoCo, and PIT pass");
    Assert(File.Exists(Path.Combine(preflightOutput, "subjects", "preflight-report.json"))
           && File.Exists(Path.Combine(preflightOutput, "subjects", "exclusions.csv")),
        "Phase 2 preflight persists report and exclusion ledger");
    Assert(Directory.EnumerateFiles(preflight.EvidenceDirectory, "jacoco.xml", SearchOption.AllDirectories).Any()
           && Directory.EnumerateFiles(preflight.EvidenceDirectory, "mutations.xml", SearchOption.AllDirectories).Any(),
        "Phase 2 retains JaCoCo/PIT XML after deleting the isolated workspace");
    Assert(!Directory.Exists(Path.Combine(fakeRepository, "target")),
        "Phase 2 tool execution is isolated from the input repository");

    var excludedOutput = Path.Combine(testRoot, "excluded-output");
    var excluded = await new RepositoryPreflightService(new FakePreflightProcessRunner(failCompile: true))
        .RunAsync(new PreflightRequest(
            "FAKE-BROKEN",
            fakeRepository,
            excludedOutput,
            30,
            30,
            30,
            30,
            "0.8.15",
            "1.25.3"));
    Assert(!excluded.Eligible
           && excluded.CleanCompile == "FAIL"
           && excluded.ExclusionReason?.Contains("Clean compile", StringComparison.Ordinal) == true,
        "Phase 2 excludes a repository before generation when clean compile fails");
    Assert((await File.ReadAllTextAsync(Path.Combine(excludedOutput, "subjects", "exclusions.csv")))
            .Contains("FAKE-BROKEN", StringComparison.Ordinal),
        "Phase 2 records the pre-generation exclusion reason");

    var fakeGradleRepository = Path.Combine(testRoot, "fake-gradle-project");
    Directory.CreateDirectory(Path.Combine(fakeGradleRepository, "src", "main", "java", "example"));
    await File.WriteAllTextAsync(Path.Combine(fakeGradleRepository, "build.gradle"), "plugins { id 'java' }");
    await File.WriteAllTextAsync(
        Path.Combine(fakeGradleRepository, "src", "main", "java", "example", "GradleExample.java"),
        "package example; public final class GradleExample { }");
    var gradlePreflight = await new RepositoryPreflightService(new FakePreflightProcessRunner())
        .RunAsync(new PreflightRequest(
            "FAKE-GRADLE",
            fakeGradleRepository,
            Path.Combine(testRoot, "gradle-output"),
            30,
            30,
            30,
            30,
            "0.8.15",
            "1.25.3"));
    Assert(gradlePreflight.Eligible && gradlePreflight.BuildTool == "gradle",
        "Phase 2 recognizes and completes the Gradle preflight contract");

    await File.WriteAllTextAsync(Path.Combine(fakeRepository, "REVISION"), "dataset-revision-123");
    var noGitPreflight = await new RepositoryPreflightService(
            new FakePreflightProcessRunner(gitUnavailable: true))
        .RunAsync(new PreflightRequest(
            "FAKE-NO-GIT",
            fakeRepository,
            Path.Combine(testRoot, "no-git-output"),
            30,
            30,
            30,
            30,
            "0.8.15",
            "1.25.3"));
    Assert(noGitPreflight.Eligible && noGitPreflight.Revision == "dataset-revision-123",
        "Phase 2 resolves a pinned dataset revision when Git is not installed");

    var processSmoke = await new ProcessRunner().RunAsync(
        "dotnet", ["--version"], testRoot, TimeSpan.FromSeconds(30));
    Assert(processSmoke.IsSuccess && !string.IsNullOrWhiteSpace(processSmoke.StandardOutput),
        "Phase 2 process runner captures a real tool invocation");
    var workspaceForTools = WorkspaceLayout.FindWorkspaceRoot(Environment.CurrentDirectory);
    Assert(File.Exists(BundledBuildToolResolver.ResolveMaven(workspaceForTools)),
        "Bundled Maven is discovered without relying on system PATH");
    Assert(File.Exists(BundledBuildToolResolver.ResolveGradle(workspaceForTools)),
        "Bundled Gradle is discovered without relying on system PATH");

    var workspaceRoot = workspaceForTools;
    var subjectRoot = Path.Combine(workspaceRoot, "experiments", "rq1", "subjects");
    var subjectReport = System.Text.Json.JsonSerializer.Deserialize<SubjectSelectionReport>(
        await File.ReadAllTextAsync(Path.Combine(subjectRoot, "subject-selection-report.json")),
        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    var pilotSubjects = await ReadJsonLinesAsync<FocalMethodSubject>(
        Path.Combine(subjectRoot, "subjects-pilot.jsonl"));
    var officialSubjects = await ReadJsonLinesAsync<FocalMethodSubject>(
        Path.Combine(subjectRoot, "subjects-official.jsonl"));
    var allSubjects = pilotSubjects.Concat(officialSubjects).ToList();
    Assert(subjectReport.AutomatedSelectionComplete
           && subjectReport.ManualReviewComplete
           && subjectReport.ParseErrorCount == 0,
        "Phase 3 automated and manual selection gates pass");
    Assert(pilotSubjects.Count == 5
           && pilotSubjects.Select(item => item.ComplexityStratum).ToHashSet(StringComparer.Ordinal)
               .SetEquals(["LOW", "MEDIUM", "HIGH"])
           && !pilotSubjects.Select(item => item.CandidateKey)
               .Intersect(officialSubjects.Select(item => item.CandidateKey), StringComparer.Ordinal).Any()
           && officialSubjects.GroupBy(item => item.ProjectId).All(group => group.Count() <= 3),
        "Phase 3 pilot strata, disjointness, and per-project cap hold");
    Assert(allSubjects.All(subject =>
        {
            var repositoryPath = WorkspaceLayout.ResolveRepositoryPath(workspaceRoot, subject.RepositoryPath);
            var sourcePath = Path.Combine(
                repositoryPath, subject.SourceFile.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(sourcePath)
                   && Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(sourcePath)))
                       .Equals(subject.SourceSha256, StringComparison.OrdinalIgnoreCase);
        }),
        "Phase 3 all focal source files resolve and match frozen SHA-256 hashes");
    var manualReviewLines = await File.ReadAllLinesAsync(Path.Combine(subjectRoot, "manual-review.csv"));
    Assert(manualReviewLines.Length == allSubjects.Count + 1
           && manualReviewLines.Skip(1).All(line => line.Contains(",APPROVED,", StringComparison.Ordinal)),
        "Phase 3 manual review ledger approves every selected subject");

    var targetsRoot = Path.Combine(workspaceRoot, "experiments", "rq1", "targets");
    var targetReport = System.Text.Json.JsonSerializer.Deserialize<StructuralTargetReport>(
        await File.ReadAllTextAsync(Path.Combine(targetsRoot, "target-extraction-report.json")),
        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    var pilotTargets = await ReadJsonLinesAsync<StructuralTargetInventory>(
        Path.Combine(targetsRoot, "targets-pilot.jsonl"));
    var targetInventories = pilotTargets;
    Assert(targetReport.Complete
           && targetReport.DeterministicThreeRuns
           && targetReport.TargetIdsStable
           && targetReport.AllTargetsSourceMapped
           && targetReport.SourceHashesVerified
           && targetReport.PlannerIndependent
           && targetReport.OutcomeSemanticsFrozen,
        "Phase 4 deterministic extraction and frozen measurement gates pass");
    Assert(targetReport.SubjectCount == targetReport.PilotSubjectCount
                                      + targetReport.OfficialSubjectCount
                                      + targetReport.SmokeSubjectCount
           && pilotTargets.Count == pilotSubjects.Count
           && targetInventories.SelectMany(item => item.Targets).All(target =>
               target.Type != "BRANCH_OUTCOME"
               && (target.MeasurementRole != "OFFICIAL"
                   || target.Type == "BRANCH_SITE"
                   && target.MeasurementSemantics == "ALL_JACOCO_BRANCHES_AT_SOURCE_LINE")),
        "Phase 4 never infers semantic true/false outcomes from JaCoCo branch evidence");
    Assert(targetInventories.SelectMany(item => item.Targets).Select(item => item.TargetId)
               .Distinct(StringComparer.Ordinal).Count()
           == targetInventories.SelectMany(item => item.Targets).Count()
           && targetInventories.All(inventory => inventory.Targets.All(target =>
               target.Line >= pilotSubjects.Single(subject => subject.SubjectId == inventory.SubjectId).StartLine
               && target.Line <= pilotSubjects.Single(subject => subject.SubjectId == inventory.SubjectId).EndLine)),
        "Phase 4 target IDs are globally unique and every target is mapped inside its focal method");
    var independentlyMissingOfficialTargets = targetInventories
        .Where(inventory => inventory.Targets.All(target => target.MeasurementRole != "OFFICIAL"))
        .Select(inventory => inventory.SubjectId).OrderBy(value => value, StringComparer.Ordinal).ToList();
    Assert(targetReport.SubjectWithoutOfficialTargetsCount == targetReport.SubjectsWithoutOfficialTargets.Count
           && independentlyMissingOfficialTargets.All(id => targetInventories.Single(item => item.SubjectId == id)
               .Targets.All(target => target.MeasurementRole != "OFFICIAL")),
        "Phase 4 explicitly reports subjects with no JaCoCo branch-site denominator");

    var experimentRoot = Path.Combine(workspaceRoot, "experiments", "rq1");
    var phase5Report = System.Text.Json.JsonSerializer.Deserialize<RepositoryContextPromptReport>(
        await File.ReadAllTextAsync(Path.Combine(experimentRoot, "phase5-context-prompt-report.json")),
        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    var contexts = await ReadJsonLinesAsync<RepositoryContext>(
        Path.Combine(experimentRoot, "context", "contexts-pilot.jsonl"));
    Assert(phase5Report.Complete && phase5Report.SubjectCount > 0
           && phase5Report.DeterministicTwoRuns && phase5Report.ErrorCount == 0
           && phase5Report.StrictSchemasValid && phase5Report.PromptContractsValid,
        "Phase 5 deterministic context extraction and strict prompt-contract gates pass");
    Assert(phase5Report.SubjectCount == phase5Report.PilotSubjectCount
                                      + phase5Report.OfficialSubjectCount
                                      + phase5Report.SmokeSubjectCount
           && contexts.Count == pilotSubjects.Count
           && contexts.All(context => !string.IsNullOrWhiteSpace(context.ContextHash)
                                      && context.FocalMethodOverloadSignatures.Count >= 1
                                      && context.Build.RequiredTestApi == "JUNIT_JUPITER"
                                      && context.Build.TestAnnotation == "org.junit.jupiter.api.Test"
                                      && context.Build.ExecutionProvider == "JUnitPlatformProvider"
                                      && context.SourceFile.Replace('\\', '/').StartsWith("src/main/java/", StringComparison.Ordinal)
                                      && context.DirectlyCalledProjectMethods.All(method =>
                                          method.SourceFile.Replace('\\', '/').Contains("src/main/java/", StringComparison.Ordinal))),
        "Phase 5 contexts cover every subject and exclude existing test bodies");
    Assert(phase5Report.CommonContextSharedExactly && phase5Report.DirectPromptHasNoTargetLeakage
           && File.Exists(Path.Combine(experimentRoot, "state", "phase5-prompt-context-lock.json")),
        "Phase 5 freezes identical common-context hashes while isolating treatment-specific evidence");
    var directSystemPrompt = await File.ReadAllTextAsync(
        Path.Combine(experimentRoot, "prompts", "direct-system.md"));
    var plannerSystemPrompt = await File.ReadAllTextAsync(
        Path.Combine(experimentRoot, "prompts", "planner-system.md"));
    var generatorSystemPrompt = await File.ReadAllTextAsync(
        Path.Combine(experimentRoot, "prompts", "generator-system.md"));
    Assert(directSystemPrompt.Contains("TEST-CODE SAFETY CONTRACT", StringComparison.Ordinal)
           && generatorSystemPrompt.Contains("TEST-CODE SAFETY CONTRACT", StringComparison.Ordinal)
           && directSystemPrompt.Contains("private/inaccessible", StringComparison.Ordinal)
           && generatorSystemPrompt.Contains("private/inaccessible", StringComparison.Ordinal)
           && directSystemPrompt.Contains("runtime prerequisites", StringComparison.Ordinal)
           && generatorSystemPrompt.Contains("runtime prerequisites", StringComparison.Ordinal)
           && directSystemPrompt.Contains("JUNIT_JUPITER", StringComparison.Ordinal)
           && generatorSystemPrompt.Contains("JUNIT_JUPITER", StringComparison.Ordinal)
           && directSystemPrompt.Contains("focalMethodOverloadSignatures", StringComparison.Ordinal)
           && generatorSystemPrompt.Contains("focalMethodOverloadSignatures", StringComparison.Ordinal),
        "Phase 5 applies the same accessibility, runtime, JUnit and overload policy to DIRECT and PLANNED generation");
    Assert(plannerSystemPrompt.Contains("PLANNING CONTRACT", StringComparison.Ordinal)
           && plannerSystemPrompt.Contains("implementation-derived", StringComparison.Ordinal)
           && plannerSystemPrompt.Contains("infeasible", StringComparison.Ordinal)
           && generatorSystemPrompt.Contains(
               "Every `mappings[].testClass` must exactly equal `testClassName`", StringComparison.Ordinal),
        "Phase 5 rejects infeasible plans and freezes exact planned class/method mapping identity");

    var validDirectJson = """
        {"testClassName":"FixtureGeneratedTest","sourceCode":"```java\nimport org.junit.jupiter.api.Test;\nclass FixtureGeneratedTest {\n @Test\n void testOne() {} }\n```","declaredTestMethods":["testOne"]}
        """;
    var parsedDirect = StructuredLlmOutputParser.ParseDirect("```json\n" + validDirectJson + "\n```", 2);
    Assert(parsedDirect.IsValid && parsedDirect.Output?.DeclaredTestMethods.SequenceEqual(["testOne"]) == true
           && !parsedDirect.Output.SourceCode.Contains("```", StringComparison.Ordinal),
        "Phase 6 deterministically parses strict DIRECT JSON and extracts a Java code fence");
    var junit4Direct = StructuredLlmOutputParser.ParseDirect(
        validDirectJson.Replace("org.junit.jupiter.api.Test", "org.junit.Test", StringComparison.Ordinal),
        2, "JUNIT_JUPITER");
    var overloadedNullDirect = StructuredLlmOutputParser.ParseDirect(
        validDirectJson.Replace("void testOne() {}", "void testOne() { target.convert(null); }", StringComparison.Ordinal),
        2, "JUNIT_JUPITER", "convert(EventWrapper<AccessEvent>)",
        ["convert(EventWrapper<AccessEvent>)", "convert(EventIdentifier)"]);
    var typedNullDirect = StructuredLlmOutputParser.ParseDirect(
        validDirectJson.Replace("void testOne() {}",
            "void testOne() { target.convert((EventWrapper<AccessEvent>) null); }", StringComparison.Ordinal),
        2, "JUNIT_JUPITER", "convert(EventWrapper<AccessEvent>)",
        ["convert(EventWrapper<AccessEvent>)", "convert(EventIdentifier)"]);
    Assert(!junit4Direct.IsValid && junit4Direct.FailureCode == "JUNIT4_FORBIDDEN"
           && !overloadedNullDirect.IsValid && overloadedNullDirect.FailureCode == "AMBIGUOUS_BARE_NULL_OVERLOAD"
           && typedNullDirect.IsValid,
        "Phase 6 rejects JUnit 4 discovery mismatches and ambiguous bare-null overload calls before Maven");
    var tooMany = StructuredLlmOutputParser.ParseDirect(
        "{\"testClassName\":\"TTest\",\"sourceCode\":\"class TTest { @Test\\n void a(){} @Test\\n void b(){} }\",\"declaredTestMethods\":[\"a\",\"b\"]}", 1);
    Assert(!tooMany.IsValid && tooMany.FailureCode == "MAX_TEST_METHODS_EXCEEDED",
        "Phase 6 enforces the frozen maximum test-method count without coverage-based selection");
    var focalCollision = StructuredLlmOutputParser.ParseDirect(
        validDirectJson.Replace("FixtureGeneratedTest", "Fixture", StringComparison.Ordinal),
        2, focalClass: "Fixture", expectedTestClassName: "FixtureGeneratedTest");
    var unexpectedIdentity = StructuredLlmOutputParser.ParseDirect(
        validDirectJson.Replace("FixtureGeneratedTest", "AnotherGeneratedTest", StringComparison.Ordinal),
        2, focalClass: "Fixture", expectedTestClassName: "FixtureGeneratedTest");
    Assert(!focalCollision.IsValid
           && focalCollision.FailureCode == "GENERATED_TEST_FQN_COLLIDES_WITH_FOCAL_CLASS"
           && !unexpectedIdentity.IsValid
           && unexpectedIdentity.FailureCode == "UNEXPECTED_TEST_CLASS_NAME",
        "Phase 6 rejects focal-class shadowing and enforces the deterministic generated-test identity");
    var productionBudget = ConservativeTokenBudget.Allocate(65_536, new string('x', 19_491), 65_536);
    Assert(productionBudget.EstimatedInputTokens == 6_497
           && productionBudget.SafetyReserveTokens > 0
           && productionBudget.MaxOutputTokens + productionBudget.EstimatedInputTokens
              + productionBudget.SafetyReserveTokens <= 65_536,
        "Phase 6 uses a conservative auditable input estimate and reserves token headroom");

    var directFixtureRoot = Path.Combine(testRoot, "direct-fixture");
    Directory.CreateDirectory(Path.Combine(directFixtureRoot, "context"));
    Directory.CreateDirectory(Path.Combine(directFixtureRoot, "prompts"));
    await File.WriteAllTextAsync(Path.Combine(directFixtureRoot, "prompts", "direct-system.md"),
        "Return strict JSON only.");
    await File.WriteAllTextAsync(Path.Combine(directFixtureRoot, "prompts", "direct-user.md"),
        "COMMON_CONTEXT_JSON:\n{{COMMON_CONTEXT_JSON}}");
    var fixtureContext = new RepositoryContext
    {
        SubjectId = "FIX-M01", Split = "SMOKE", ProjectId = "fixture",
        SourceFile = "src/main/java/Fixture.java", FocalClass = "Fixture",
        FocalMethodSignature = "value(int)", SourceSha256 = new string('a', 64),
        FocalMethodSource = "int value(int x) { return x; }", ContextHash = new string('b', 64)
    };
    await File.WriteAllTextAsync(Path.Combine(directFixtureRoot, "context", "contexts-smoke.jsonl"),
        System.Text.Json.JsonSerializer.Serialize(fixtureContext,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }) + "\n");
    var validFixtureClient = new StubFailoverLlmClient(validDirectJson, totalTokens: 120);
    var fixtureService = new DirectBaselineService(new LlmClientFactory(), validFixtureClient);
    var fixtureReport = await fixtureService.RunAsync(new DirectBaselineRequest(
        "fixture-valid", "Smoke", directFixtureRoot, ProviderIds.Gemini, string.Empty, "fixture-model",
        0, 30, 2, 1000, 3, MaxOutputTokensPerCall: 512));
    Assert(fixtureReport.Complete && fixtureReport.ValidOutputCount == 1
           && fixtureReport.SemanticLlmCallCount == 1 && validFixtureClient.CallCount == 1
           && validFixtureClient.LastMaxOutputTokens == 512
           && fixtureReport.PlannerCallCount == 0 && fixtureReport.RepairCallCount == 0,
        "Phase 6 applies the configured per-call output cap and executes one DIRECT call without Planner/Repair");
    var invalidFixtureClient = new StubFailoverLlmClient("not-json", totalTokens: 20);
    var invalidReport = await new DirectBaselineService(new LlmClientFactory(), invalidFixtureClient).RunAsync(
        new DirectBaselineRequest("fixture-invalid", "Smoke", directFixtureRoot, ProviderIds.Gemini,
            string.Empty, "fixture-model", 0, 30, 2, 4000, 3));
    var invalidRaw = Path.Combine(directFixtureRoot, "runs", "fixture-invalid", "FIX-M01", "DIRECT", "R01",
        "generation", "assistant-content.raw.txt");
    Assert(invalidReport.Complete && invalidReport.InvalidOutputCount == 1
           && invalidFixtureClient.CallCount == 1 && File.ReadAllText(invalidRaw) == "not-json",
        "Phase 6 retains invalid raw output as a terminal failure without hidden retry");
    var ceilingClient = new StubFailoverLlmClient(validDirectJson, totalTokens: 1001);
    var ceilingReport = await new DirectBaselineService(new LlmClientFactory(), ceilingClient).RunAsync(
        new DirectBaselineRequest("fixture-ceiling", "Smoke", directFixtureRoot, ProviderIds.Gemini,
            string.Empty, "fixture-model", 0, 30, 2, 1000, 3));
    Assert(ceilingReport.InvalidOutputCount == 1 && ceilingReport.TokenCeilingViolationCount == 1
           && ceilingClient.CallCount == 1,
        "Phase 6 records authoritative provider token-ceiling violations without a hidden retry");
    var legacyDirectRoot = Path.Combine(directFixtureRoot, "runs", "fixture-legacy-contract",
        "FIX-M01", "DIRECT", "R01");
    Directory.CreateDirectory(legacyDirectRoot);
    await File.WriteAllTextAsync(Path.Combine(legacyDirectRoot, "run.json"),
        System.Text.Json.JsonSerializer.Serialize(new DirectRunRecord
        {
            RunId = "FIX-M01-DIRECT-R01", SubjectId = "FIX-M01", Status = "VALID",
            CompletedAt = DateTimeOffset.UtcNow
        }, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
    var legacyRejected = false;
    try
    {
        await new DirectBaselineService(new LlmClientFactory(), new StubFailoverLlmClient(validDirectJson, 10))
            .RunAsync(new DirectBaselineRequest("fixture-legacy-contract", "Smoke", directFixtureRoot,
                ProviderIds.Gemini, string.Empty, "fixture-model", 0, 30, 2, 1000, 3));
    }
    catch (InvalidOperationException exception)
    {
        legacyRejected = exception.Message.Contains("PIPELINE_CONTRACT_MISMATCH", StringComparison.Ordinal);
    }
    Assert(legacyRejected,
        "Phase 6 refuses to overwrite or reuse an artifact created by an older pipeline contract");
    var parallelContexts = Enumerable.Range(1, 4).Select(index =>
    {
        var copy = new RepositoryContext
        {
            SubjectId = $"FIX-P{index:00}", Split = "PILOT", ProjectId = "fixture",
            SourceFile = "src/main/java/Fixture.java", FocalClass = "Fixture",
            FocalMethodSignature = "value(int)", SourceSha256 = new string('a', 64),
            FocalMethodSource = "int value(int x) { return x; }", ContextHash = new string((char)('b' + index), 64)
        };
        return System.Text.Json.JsonSerializer.Serialize(copy,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
    });
    await File.WriteAllTextAsync(Path.Combine(directFixtureRoot, "context", "contexts-pilot.jsonl"),
        string.Join(Environment.NewLine, parallelContexts) + Environment.NewLine);
    var parallelClient = new TrackingFailoverLlmClient(validDirectJson, 120, 60);
    var parallelReport = await new DirectBaselineService(new LlmClientFactory(), parallelClient).RunAsync(
        new DirectBaselineRequest("fixture-parallel", "Pilot", directFixtureRoot, ProviderIds.Gemini,
            string.Empty, "fixture-model", 0, 30, 2, 1000, 1,
            MaxOutputTokensPerCall: 512, MaxParallelLlmCalls: 3));
    var parallelLedgerSubjects = (await File.ReadAllLinesAsync(
            Path.Combine(directFixtureRoot, "direct-run-ledger.jsonl")))
        .Where(line => !string.IsNullOrWhiteSpace(line))
        .Select(line => System.Text.Json.JsonDocument.Parse(line).RootElement.GetProperty("subjectId").GetString())
        .ToList();
    Assert(parallelReport.ValidOutputCount == 4 && parallelClient.MaxObservedConcurrency >= 2
           && parallelLedgerSubjects.SequenceEqual(parallelLedgerSubjects.OrderBy(value => value, StringComparer.Ordinal)),
        "Phase 6 executes independent focal-method LLM runs concurrently while preserving deterministic ledger order");

    var planJson = """
        {"subjectId":"FIX-M01","scenarios":[{"scenarioId":"S01","technique":"BRANCH_OUTCOME","preconditions":[],"concreteInput":"x = 1","expectedObservation":"returns 1","oracleProvenance":{"type":"IMPLEMENTATION_DERIVED","sourceLines":[1]},"targetIds":["FIX-M01-T001"],"cfgPathNodeIds":["ENTRY","N001","EXIT"]}]}
        """;
    var fixtureCfg = new MethodControlFlowGraph
    {
        EntryNodeId = "ENTRY", ExitNodeIds = ["EXIT"], Semantics = "SOURCE_LEVEL_INTRAPROCEDURAL_CFG_V1",
        Nodes =
        [
            new ControlFlowNode { Id = "ENTRY", Kind = "ENTRY", Line = -1, SourceRange = "UNKNOWN" },
            new ControlFlowNode { Id = "N001", Kind = "RETURN", Line = 1, SourceRange = "1:1-1:1" },
            new ControlFlowNode { Id = "EXIT", Kind = "EXIT", Line = -1, SourceRange = "UNKNOWN" }
        ],
        Edges =
        [
            new ControlFlowEdge { From = "ENTRY", To = "N001", Kind = "NORMAL", Label = "entry" },
            new ControlFlowEdge { From = "N001", To = "EXIT", Kind = "RETURN", Label = "return" }
        ]
    };
    var parsedPlan = StructuredLlmOutputParser.ParseScenarioPlan(
        planJson, "FIX-M01", new HashSet<string> { "FIX-M01-T001" }, fixtureCfg, 2);
    Assert(parsedPlan.IsValid && parsedPlan.Plan?.Scenarios.Single().ScenarioId == "S01",
        "Phase 7 validates strict scenario schema, stable IDs, technique and provenance");
    var unknownTargetPlan = StructuredLlmOutputParser.ParseScenarioPlan(
        planJson.Replace("FIX-M01-T001", "UNKNOWN", StringComparison.Ordinal),
        "FIX-M01", new HashSet<string> { "FIX-M01-T001" }, fixtureCfg, 2);
    var unstablePlan = StructuredLlmOutputParser.ParseScenarioPlan(
        planJson.Replace("S01", "S02", StringComparison.Ordinal),
        "FIX-M01", new HashSet<string> { "FIX-M01-T001" }, fixtureCfg, 2);
    var cappedPlan = StructuredLlmOutputParser.ParseScenarioPlan(
        planJson, "FIX-M01", new HashSet<string> { "FIX-M01-T001" }, fixtureCfg, 0);
    var disconnectedCfgPlan = StructuredLlmOutputParser.ParseScenarioPlan(
        planJson.Replace("N001", "EXIT", StringComparison.Ordinal),
        "FIX-M01", new HashSet<string> { "FIX-M01-T001" }, fixtureCfg, 2);
    Assert(!unknownTargetPlan.IsValid && unknownTargetPlan.FailureCode == "UNKNOWN_OR_INVALID_TARGET_ID"
           && !unstablePlan.IsValid && unstablePlan.FailureCode == "UNSTABLE_SCENARIO_ID"
           && !cappedPlan.IsValid && cappedPlan.FailureCode == "MAX_SCENARIOS_EXCEEDED"
           && !disconnectedCfgPlan.IsValid && disconnectedCfgPlan.FailureCode == "INVALID_CFG_PATH",
        "Phase 7 rejects unknown targets, unstable IDs, disconnected CFG paths and scenario-count cap violations");
    var plannedOutputJson = """
        {"testClassName":"FixtureGeneratedTest","sourceCode":"import org.junit.jupiter.api.Test;\nclass FixtureGeneratedTest {\n @Test\n void test_S01_value() {}\n}","declaredTestMethods":["test_S01_value"],"mappings":[{"scenarioId":"S01","testClass":"FixtureGeneratedTest","testMethod":"test_S01_value"}]}
        """;
    var parsedPlannedOutput = StructuredLlmOutputParser.ParsePlannedOutput(
        plannedOutputJson, parsedPlan.Plan!, 2);
    var parameterizedOutput = StructuredLlmOutputParser.ParsePlannedOutput(
        plannedOutputJson.Replace("@Test", "@ParameterizedTest", StringComparison.Ordinal), parsedPlan.Plan!, 2);
    var brokenMappingOutput = StructuredLlmOutputParser.ParsePlannedOutput(
        plannedOutputJson.Replace("test_S01_value\"}]", "unowned_test\"}]", StringComparison.Ordinal),
        parsedPlan.Plan!, 2);
    Assert(parsedPlannedOutput.IsValid && !parameterizedOutput.IsValid
           && parameterizedOutput.FailureCode == "PARAMETERIZED_TEST_FORBIDDEN"
           && !brokenMappingOutput.IsValid && brokenMappingOutput.FailureCode == "BIJECTION_VIOLATION",
        "Phase 7 enforces scenario-test bijection and rejects parameterized tests");
    var junit4Planned = StructuredLlmOutputParser.ParsePlannedOutput(
        plannedOutputJson.Replace("org.junit.jupiter.api.Test", "org.junit.Test", StringComparison.Ordinal),
        parsedPlan.Plan!, 2, "JUNIT_JUPITER");
    Assert(!junit4Planned.IsValid && junit4Planned.FailureCode == "JUNIT4_FORBIDDEN",
        "Phase 7 enforces the frozen JUnit Jupiter execution contract for PLANNED output");

    await File.WriteAllTextAsync(Path.Combine(directFixtureRoot, "prompts", "planner-system.md"), "Return plan JSON.");
    await File.WriteAllTextAsync(Path.Combine(directFixtureRoot, "prompts", "planner-user.md"),
        "{{COMMON_CONTEXT_JSON}}\n{{STRUCTURAL_TARGET_INVENTORY_JSON}}");
    await File.WriteAllTextAsync(Path.Combine(directFixtureRoot, "prompts", "generator-system.md"), "Return tests JSON.");
    await File.WriteAllTextAsync(Path.Combine(directFixtureRoot, "prompts", "generator-user.md"),
        "{{COMMON_CONTEXT_JSON}}\n{{SCENARIO_PLAN_JSON}}");
    Directory.CreateDirectory(Path.Combine(directFixtureRoot, "targets"));
    var fixtureInventory = new StructuralTargetInventory
    {
        SubjectId = "FIX-M01", Split = "SMOKE", ProjectId = "fixture", FocalClass = "Fixture",
        FocalMethodSignature = "value(int)", SourceFile = "src/main/java/Fixture.java",
        SourceSha256 = new string('a', 64), AnalyzerVersion = "fixture",
        ControlFlowGraph = fixtureCfg,
        Targets = [new StructuralTarget { TargetId = "FIX-M01-T001", Type = "BRANCH_SITE",
            MeasurementRole = "OFFICIAL", MeasurementSemantics = "ALL_JACOCO_BRANCHES_AT_SOURCE_LINE", Line = 1 }]
    };
    await File.WriteAllTextAsync(Path.Combine(directFixtureRoot, "targets", "targets-smoke.jsonl"),
        System.Text.Json.JsonSerializer.Serialize(fixtureInventory,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }) + "\n");
    var plannedClient = new QueueFailoverLlmClient((planJson, 100), (plannedOutputJson, 130));
    var plannedFixtureReport = await new PlannedPipelineService(new LlmClientFactory(), plannedClient).RunAsync(
        new PlannedPipelineRequest("fixture-planned-valid", "Smoke", directFixtureRoot, ProviderIds.Gemini,
            string.Empty, "fixture-model", 0, 30, 2, 4000, 3));
    Assert(plannedFixtureReport.Complete && plannedFixtureReport.ValidOutputCount == 1
           && plannedFixtureReport.PlannerSemanticCallCount == 1
           && plannedFixtureReport.GeneratorSemanticCallCount == 1
           && plannedFixtureReport.RepairCallCount == 0 && plannedFixtureReport.ReviewerCallCount == 0
           && plannedClient.CallCount == 2,
        "Phase 7 fixture produces plan, test class and bijective mapping in exactly two semantic calls");
    var resumeRoot = Path.Combine(directFixtureRoot, "runs", "fixture-planned-resume",
        "FIX-M01", "PLANNED", "R01");
    Directory.CreateDirectory(Path.Combine(resumeRoot, "planner"));
    await File.WriteAllTextAsync(Path.Combine(resumeRoot, "planner", "scenario-plan.json"), planJson);
    await File.WriteAllTextAsync(Path.Combine(resumeRoot, "run.json"),
        System.Text.Json.JsonSerializer.Serialize(new PlannedRunRecord
        {
            PipelineContractVersion = PipelineContractVersions.Current,
            RunId = "FIX-M01-PLANNED-R01", SubjectId = "FIX-M01", Split = "SMOKE",
            Status = string.Empty, ContextHash = fixtureContext.ContextHash,
            TargetInventoryHash = new string('f', 64), PlannerSemanticCalls = 1,
            PlannerTotalTokens = 100, TotalTokens = 100, ScenarioCount = 1,
            StartedAt = DateTimeOffset.UtcNow
        }, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
    var resumeClient = new QueueFailoverLlmClient((plannedOutputJson, 130));
    var resumeReport = await new PlannedPipelineService(new LlmClientFactory(), resumeClient).RunAsync(
        new PlannedPipelineRequest("fixture-planned-resume", "Smoke", directFixtureRoot,
            ProviderIds.Gemini, string.Empty, "fixture-model", 0, 30, 2, 4000, 3));
    Assert(resumeReport.ValidOutputCount == 1 && resumeClient.CallCount == 1
           && resumeReport.PlannerSemanticCallCount == 1
           && resumeReport.GeneratorSemanticCallCount == 1,
        "Phase 7 resumes a validated Planner checkpoint at Generator without silently reusing a blank run");
    var badPlannerClient = new QueueFailoverLlmClient(("not-json", 20));
    var badPlannerReport = await new PlannedPipelineService(new LlmClientFactory(), badPlannerClient).RunAsync(
        new PlannedPipelineRequest("fixture-planned-invalid", "Smoke", directFixtureRoot, ProviderIds.Gemini,
            string.Empty, "fixture-model", 0, 30, 2, 4000, 3));
    Assert(badPlannerReport.Complete && badPlannerReport.InvalidPlannerCount == 1
           && badPlannerReport.ContractViolationCount == 0 && badPlannerClient.CallCount == 1,
        "Phase 7 retains invalid Planner output and never calls Generator/Repair after terminal plan failure");
    var unknownPlannerClient = new QueueFailoverLlmClient(
        (planJson.Replace("FIX-M01-T001", "UNKNOWN", StringComparison.Ordinal), 20));
    var unknownPlannerReport = await new PlannedPipelineService(new LlmClientFactory(), unknownPlannerClient).RunAsync(
        new PlannedPipelineRequest("fixture-planned-contract", "Smoke", directFixtureRoot, ProviderIds.Gemini,
            string.Empty, "fixture-model", 0, 30, 2, 4000, 3));
    var contractRecord = System.Text.Json.JsonSerializer.Deserialize<PlannedRunRecord>(
        await File.ReadAllTextAsync(Path.Combine(directFixtureRoot, "runs", "fixture-planned-contract",
            "FIX-M01", "PLANNED", "R01", "run.json")),
        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    Assert(unknownPlannerReport.ContractViolationCount == 1
           && contractRecord?.FailureCode == "UNKNOWN_OR_INVALID_TARGET_ID"
           && unknownPlannerClient.CallCount == 1,
        "Phase 7 records contract violations in the result ledger without Generator/Repair calls");

    var timeoutCommand = OperatingSystem.IsWindows()
        ? ("cmd.exe", new[] { "/d", "/c", "ping", "127.0.0.1", "-n", "3" })
        : ("/bin/sh", new[] { "-c", "sleep 2" });
    var timeoutEvidence = await new ProcessRunner().RunAsync(
        timeoutCommand.Item1, timeoutCommand.Item2, testRoot, TimeSpan.FromMilliseconds(50));
    Assert(timeoutEvidence.TimedOut && timeoutEvidence.ExitCode is null,
        "Phase 8 process runner enforces timeout and returns captured terminal evidence");

    var toolOutputRoot = Path.Combine(testRoot, "tool-fixture-output");
    var toolRepository = Path.Combine(testRoot, "tool-fixture-repository");
    Directory.CreateDirectory(Path.Combine(toolRepository, "src", "main", "java"));
    Directory.CreateDirectory(Path.Combine(toolRepository, "src", "test", "java"));
    await File.WriteAllTextAsync(Path.Combine(toolRepository, "pom.xml"),
        "<project xmlns=\"http://maven.apache.org/POM/4.0.0\"><modelVersion>4.0.0</modelVersion><groupId>fixture</groupId><artifactId>fixture</artifactId><version>1</version><build><plugins><plugin><groupId>org.jacoco</groupId><artifactId>jacoco-maven-plugin</artifactId><version>0.8.10</version></plugin></plugins></build></project>");
    await File.WriteAllTextAsync(Path.Combine(toolRepository, "src", "main", "java", "Fixture.java"),
        "class Fixture { int value(int x) { return x > 0 ? 1 : 0; } int unrelated() { return 9; } }");
    await File.WriteAllTextAsync(Path.Combine(toolRepository, "src", "test", "java", "OldTest.java"),
        "class OldTest {}");
    Directory.CreateDirectory(Path.Combine(toolOutputRoot, "subjects"));
    var toolSubject = new FocalMethodSubject
    {
        SubjectId = "FIX-M01", Split = "PILOT", ProjectId = "fixture", RepositoryPath = toolRepository,
        ModulePath = ".", SourceFile = "src/main/java/Fixture.java", FocalClass = "Fixture",
        FocalMethodSignature = "value(int)", StartLine = 1, EndLine = 5, SourceSha256 = new string('c', 64),
        Eligible = true, ManualReviewStatus = "APPROVED"
    };
    await File.WriteAllTextAsync(Path.Combine(toolOutputRoot, "subjects", "subjects-pilot.jsonl"),
        System.Text.Json.JsonSerializer.Serialize(toolSubject,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }) + "\n");
    Directory.CreateDirectory(Path.Combine(toolOutputRoot, "targets"));
    var toolInventory = new StructuralTargetInventory
    {
        SubjectId = "FIX-M01", Split = "PILOT", ProjectId = "fixture", FocalClass = "Fixture",
        FocalMethodSignature = "value(int)", SourceFile = "src/main/java/Fixture.java",
        SourceSha256 = new string('c', 64), AnalyzerVersion = "fixture",
        OfficialTargetSemantics = "FULL_BRANCH_SITE_COVERAGE",
        Targets = [new StructuralTarget { TargetId = "FIX-M01-T001", Type = "BRANCH_SITE",
            MeasurementRole = "OFFICIAL", MeasurementSemantics = "ALL_JACOCO_BRANCHES_AT_SOURCE_LINE", Line = 1 }]
    };
    await File.WriteAllTextAsync(Path.Combine(toolOutputRoot, "targets", "targets-pilot.jsonl"),
        System.Text.Json.JsonSerializer.Serialize(toolInventory,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }) + "\n");
    var generatedJava = """
        import org.junit.jupiter.api.Test;
        class FixtureGeneratedTest { @Test void test_value() { } }
        """;

    static async Task CreateToolArtifactAsync(string output, string experiment, string java)
    {
        var run = Path.Combine(output, "runs", experiment, "FIX-M01", "DIRECT", "R01");
        Directory.CreateDirectory(Path.Combine(run, "generation"));
        await File.WriteAllTextAsync(Path.Combine(run, "generation", "FixtureGeneratedTest.java"), java);
        await File.WriteAllTextAsync(Path.Combine(run, "generation", "assistant-content.raw.txt"), "fixture response");
        await File.WriteAllTextAsync(Path.Combine(run, "generation", "provider-response.raw.json"), "{\"fixture\":true}");
        var now = DateTimeOffset.UtcNow;
        await File.WriteAllTextAsync(Path.Combine(run, "run.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            subjectId = "FIX-M01", approach = "DIRECT", repetition = 1, status = "VALID",
            contextHash = new string('d', 64), requestHash = new string('e', 64), providerId = "fixture",
            requestedModelId = "fixture-model", responseModelId = "fixture-model", semanticLlmCalls = 1,
            transportAttempts = 1, inputTokens = 10, outputTokens = 5, totalTokens = 15,
            declaredTestMethodCount = 1, startedAt = now, completedAt = now.AddSeconds(1)
        }, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
    }

    static async Task CreatePlannerFailureArtifactAsync(string output, string experiment)
    {
        var run = Path.Combine(output, "runs", experiment, "FIX-M01", "PLANNED", "R01");
        Directory.CreateDirectory(Path.Combine(run, "planner"));
        await File.WriteAllTextAsync(Path.Combine(run, "planner", "assistant-content.raw.txt"), "{ invalid plan");
        await File.WriteAllTextAsync(Path.Combine(run, "planner", "provider-response.raw.json"), "{\"fixture\":true}");
        var now = DateTimeOffset.UtcNow;
        await File.WriteAllTextAsync(Path.Combine(run, "run.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            subjectId = "FIX-M01", approach = "PLANNED", repetition = 1, status = "INVALID",
            failureStage = "PLANNER", failureCode = "INVALID_JSON",
            contextHash = new string('d', 64), targetInventoryHash = new string('f', 64),
            plannerRequestHash = new string('e', 64), generatorRequestHash = "",
            providerId = "fixture", requestedModelId = "fixture-model",
            plannerResponseModelId = "fixture-model", plannerSemanticCalls = 1,
            generatorSemanticCalls = 0, transportAttempts = 1,
            plannerInputTokens = 10, plannerOutputTokens = 5, plannerTotalTokens = 15, totalTokens = 15,
            scenarioCount = 0, testMethodCount = 0, bijectionValid = false,
            startedAt = now, completedAt = now.AddSeconds(1)
        }, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
    }

    ToolExecutionRequest ToolRequest(string experiment) => new(
        experiment, "Smoke", toolOutputRoot, testRoot, Path.Combine(testRoot, "datasets"), string.Empty,
        2, 2, 2, 2, "0.8.15", "1.25.3");

    await CreateToolArtifactAsync(toolOutputRoot, "tool-compile-fail", generatedJava);
    var compileFailRunner = new FakeGatedToolProcessRunner(failCompile: true);
    var compileFailReport = await new GatedToolExecutionService(compileFailRunner)
        .RunCompileAndTestAsync(ToolRequest("tool-compile-fail"));
    var compileFailRecord = System.Text.Json.JsonSerializer.Deserialize<ToolRunRecord>(
        await File.ReadAllTextAsync(Path.Combine(toolOutputRoot, "runs", "tool-compile-fail", "FIX-M01", "DIRECT", "R01", "tools", "tool-run.json")),
        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    Assert(compileFailReport.Complete && compileFailRecord?.Status == ToolRunStatuses.CompileFail
           && compileFailRunner.Gates.SequenceEqual(["COMPILE"]),
        "Phase 8 compile failure is terminal and does not execute JUnit, JaCoCo or PIT");

    await CreateToolArtifactAsync(toolOutputRoot, "tool-success", generatedJava);
    var gatedRunner = new FakeGatedToolProcessRunner();
    var toolService = new GatedToolExecutionService(gatedRunner);
    var compileTestReport = await toolService.RunCompileAndTestAsync(ToolRequest("tool-success"));
    var adequacyReport = await toolService.RunAdequacyAsync(ToolRequest("tool-success"));
    var successRecord = System.Text.Json.JsonSerializer.Deserialize<ToolRunRecord>(
        await File.ReadAllTextAsync(Path.Combine(toolOutputRoot, "runs", "tool-success", "FIX-M01", "DIRECT", "R01", "tools", "tool-run.json")),
        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    Assert(compileTestReport.ExecutionPassCount == 1 && adequacyReport.Complete
           && adequacyReport.GeneratedTestsOnly && adequacyReport.GateOrderingValid
           && gatedRunner.Gates.SequenceEqual(["COMPILE", "JUNIT", "JACOCO", "PIT"]),
        "Phase 8 executes gates in order and scopes JUnit/JaCoCo/PIT to the generated test");
    Assert(successRecord?.Mutation is { FocalMutants: 3, Killed: 1, Survived: 1, NoCoverage: 1 }
           && successRecord.Mutation.Denominator == 3
           && successRecord.Coverage is { MethodFound: true, BranchCovered: 1, BranchMissed: 1 },
        "Phase 8 filters JaCoCo/PIT evidence to the focal method and applies the frozen PIT denominator");
    Assert(successRecord?.RemovedExistingTestFileCount == 1
           && successRecord.WorkspaceRetained == false
           && File.Exists(Path.Combine(successRecord.GenerationRunDirectory, "tools", "instrumentation", "manifest.json"))
           && File.ReadAllText(Path.Combine(successRecord.GenerationRunDirectory, "tools", "instrumentation", "pom.instrumented.xml"))
               .Contains("prepare-agent", StringComparison.Ordinal)
           && Directory.EnumerateFiles(Path.Combine(successRecord.GenerationRunDirectory, "tools", "junit", "reports"), "TEST-*.xml").Any(),
        "Phase 8 retains evidence, adds prepare-agent to an existing JaCoCo plugin, and cleans the workspace");

    await CreateToolArtifactAsync(toolOutputRoot, "tool-no-discovery", generatedJava);
    var noDiscoveryRunner = new FakeGatedToolProcessRunner(zeroDiscoveredTests: true);
    var noDiscoveryService = new GatedToolExecutionService(noDiscoveryRunner);
    var noDiscoveryCompile = await noDiscoveryService.RunCompileAndTestAsync(ToolRequest("tool-no-discovery"));
    var noDiscoveryAdequacy = await noDiscoveryService.RunAdequacyAsync(ToolRequest("tool-no-discovery"));
    Assert(noDiscoveryCompile.JunitProcessSuccessCount == 1
           && noDiscoveryCompile.ExecutionPassCount == 0
           && noDiscoveryCompile.TestDiscoveryFailCount == 1
           && noDiscoveryAdequacy.MutationPassCount == 0
           && noDiscoveryRunner.Gates.SequenceEqual(["COMPILE", "JUNIT"]),
        "Phase 8 does not count a zero-test JUnit process as an execution pass or send it to JaCoCo/PIT");

    var fixtureConfig = Path.Combine(testRoot, "tool-fixture-config.json");
    await File.WriteAllTextAsync(fixtureConfig, "{\"experimentId\":\"fixture\",\"frozen\":true}");
    var ledgerService = new MetricsLedgerService();
    MetricsLedgerRequest LedgerRequest(string experiment) => new(
        experiment, "Smoke", toolOutputRoot, testRoot, fixtureConfig);
    var successLedger = await ledgerService.RebuildAsync(LedgerRequest("tool-success"));
    var successLedgerAgain = await ledgerService.RebuildAsync(LedgerRequest("tool-success"));
    var successMetric = System.Text.Json.JsonSerializer.Deserialize<RunMetricsRecord>(
        await File.ReadAllTextAsync(Path.Combine(toolOutputRoot, "runs", "tool-success", "FIX-M01", "DIRECT", "R01", "metrics.json")),
        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    Assert(successLedger.Complete && successLedger.RunCount == 1 && successLedger.UniqueKeys
           && successLedger.NoMissingRequiredHashes && successLedger.TreatmentContractsValid
           && successLedger.LedgerSha256 == successLedgerAgain.LedgerSha256
           && File.Exists(successLedger.RunsCsvPath),
        "Phase 9 deterministically rebuilds runs.csv from immutable artifacts without WPF/UI state");
    Assert(successLedger.Approaches.Single() is
           { TotalInputTokens: 10, TotalOutputTokens: 5, TotalReportedTokens: 15, TokenSampleCount: 1 },
        "Phase 9 aggregates authoritative input, output and total token usage by approach");
    Assert(successMetric is { Status: "COMPLETED", CompileSuccess: true, ExecutionSuccess: true,
               EndToEndTargetSatisfaction: 1, EndToEndBranchCoverage: 0.5, PassRate: 1,
               PlanFidelity: null, PlanObligationCoverage: null, ScenarioValidity: null }
           && successMetric.Mutation.Score == 1d / 3d
           && successMetric.PromptHash.Length == 64 && successMetric.ConfigHash.Length == 64,
        "Phase 9 derives target, focal branch, common-mutant, cost and provenance metrics");

    var failedLedger = await ledgerService.RebuildAsync(LedgerRequest("tool-compile-fail"));
    var failedMetric = System.Text.Json.JsonSerializer.Deserialize<RunMetricsRecord>(
        await File.ReadAllTextAsync(Path.Combine(toolOutputRoot, "runs", "tool-compile-fail", "FIX-M01", "DIRECT", "R01", "metrics.json")),
        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    Assert(failedLedger.Complete && failedLedger.FailedRunRetainedCount == 1
           && failedMetric is { Status: ToolRunStatuses.CompileFail, CompileSuccess: false, ExecutionSuccess: false,
               EndToEndTargetSatisfaction: 0, EndToEndBranchCoverage: 0, EndToEndMutationScore: 0,
               PassRate: 0 }
           && failedMetric.ConditionalBranchCoverage is null && failedMetric.ConditionalMutationScore is null,
        "Phase 9 retains compile failures and assigns end-to-end zero without polluting conditional metrics");

    await CreatePlannerFailureArtifactAsync(toolOutputRoot, "planner-generation-fail");
    var plannerFailureLedger = await ledgerService.RebuildAsync(LedgerRequest("planner-generation-fail"));
    var plannerFailureMetric = System.Text.Json.JsonSerializer.Deserialize<RunMetricsRecord>(
        await File.ReadAllTextAsync(Path.Combine(toolOutputRoot, "runs", "planner-generation-fail",
            "FIX-M01", "PLANNED", "R01", "metrics.json")),
        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    Assert(plannerFailureLedger.Complete && plannerFailureLedger.InvalidRowCount == 0
           && plannerFailureMetric is { Status: ToolRunStatuses.GenerationInvalid,
               EndToEndTargetSatisfaction: 0, EndToEndBranchCoverage: 0, EndToEndMutationScore: 0,
               PlanFidelity: 0, PlanObligationCoverage: 0, ScenarioValidity: 0, PassRate: 0 }
           && plannerFailureMetric.PromptHash == new string('e', 64),
        "Phase 9 accepts a retained Planner-stage generation failure without requiring Generator artifacts");

    var rawResponse = Path.Combine(toolOutputRoot, "runs", "tool-success", "FIX-M01", "DIRECT", "R01", "generation", "assistant-content.raw.txt");
    File.Delete(rawResponse);
    var rejectedLedger = await ledgerService.RebuildAsync(LedgerRequest("tool-success"));
    Assert(!rejectedLedger.Complete && rejectedLedger.InvalidRowCount == 1
           && rejectedLedger.Errors.Any(error => error.Contains("MISSING_RAW_RESPONSE", StringComparison.Ordinal)),
        "Phase 9 rejects invalid rows with an actionable missing-evidence message");

    // Test RunManagementService and Selective Rerun
    var runMgmtRoot = Path.Combine(testRoot, "run-management-test");
    var validRunDir = Path.Combine(runMgmtRoot, "runs", "test-exp", "PT-01", "DIRECT", "R01");
    var tokenFailRunDir = Path.Combine(runMgmtRoot, "runs", "test-exp", "PT-02", "PLANNED", "R01");
    var jsonFailRunDir = Path.Combine(runMgmtRoot, "runs", "test-exp", "PT-03", "DIRECT", "R01");
    Directory.CreateDirectory(validRunDir);
    Directory.CreateDirectory(tokenFailRunDir);
    Directory.CreateDirectory(jsonFailRunDir);
    await File.WriteAllTextAsync(Path.Combine(validRunDir, "run.json"), "{\"subjectId\":\"PT-01\",\"approach\":\"DIRECT\",\"repetition\":1,\"status\":\"VALID\"}");
    await File.WriteAllTextAsync(Path.Combine(tokenFailRunDir, "run.json"), "{\"subjectId\":\"PT-02\",\"approach\":\"PLANNED\",\"repetition\":1,\"status\":\"INVALID\",\"failureCode\":\"TOKEN_CEILING_EXCEEDED\"}");
    await File.WriteAllTextAsync(Path.Combine(jsonFailRunDir, "run.json"), "{\"subjectId\":\"PT-03\",\"approach\":\"DIRECT\",\"repetition\":1,\"status\":\"INVALID\",\"failureCode\":\"INVALID_JSON\"}");

    var runManager = new RunManagementService();
    var failedList = await runManager.GetFailedRunsAsync(runMgmtRoot, "test-exp");
    Assert(failedList.Count == 2 && failedList.Any(r => r.SubjectId == "PT-02") && failedList.Any(r => r.SubjectId == "PT-03"),
        "RunManagementService discovers all failed/invalid runs");

    var tokenResetReport = await runManager.ResetFailedRunsAsync(
        new ResetRunsRequest(runMgmtRoot, "test-exp", OnlyTokenFailures: true));
    Assert(tokenResetReport.ResetRunCount == 1 && !File.Exists(Path.Combine(tokenFailRunDir, "run.json"))
           && File.Exists(Path.Combine(validRunDir, "run.json"))
           && File.Exists(Path.Combine(jsonFailRunDir, "run.json")),
        "RunManagementService resets only token failure runs when OnlyTokenFailures is true");

    var allResetReport = await runManager.ResetFailedRunsAsync(
        new ResetRunsRequest(runMgmtRoot, "test-exp", OnlyTokenFailures: false));
    Assert(allResetReport.ResetRunCount == 1 && !File.Exists(Path.Combine(jsonFailRunDir, "run.json"))
           && File.Exists(Path.Combine(validRunDir, "run.json")),
        "RunManagementService resets remaining failed runs and preserves VALID runs");

    // Test ConservativeTokenBudget with larger 128k/200k budgets
    var largeBudget128k = ConservativeTokenBudget.Allocate(131_072, new string('x', 30_000), 65_536);
    Assert(largeBudget128k.MaxOutputTokens == 65_536 && largeBudget128k.EstimatedInputTokens == 10_000,
        "ConservativeTokenBudget supports 131,072 token budget with sufficient output allocation");

    var largeBudget200k = ConservativeTokenBudget.Allocate(200_000, new string('x', 150_000), 100_000);
    Assert(largeBudget200k.MaxOutputTokens > 10_000 && largeBudget200k.EstimatedInputTokens == 50_000,
        "ConservativeTokenBudget supports 200,000 token budget for large context focal methods");

    Console.WriteLine("All RQ1 Phase 0-9 implementation checks passed.");
    return 0;
}
finally
{
    if (Directory.Exists(testRoot))
        Directory.Delete(testRoot, recursive: true);
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException("FAILED: " + message);
    Console.WriteLine("PASS: " + message);
}

static async Task<List<T>> ReadJsonLinesAsync<T>(string filePath)
{
    var result = new List<T>();
    foreach (var line in await File.ReadAllLinesAsync(filePath))
    {
        if (string.IsNullOrWhiteSpace(line)) continue;
        result.Add(System.Text.Json.JsonSerializer.Deserialize<T>(line,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!);
    }
    return result;
}

static async Task<int> RunRepositoryPreflightAsync(string[] arguments)
{
    var repository = Path.GetFullPath(arguments[1]);
    var outputRoot = arguments.Length >= 3
        ? Path.GetFullPath(arguments[2])
        : Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "experiments", "rq1"));
    var modulePath = arguments.Length >= 4 ? arguments[3] : ".";
    var javaHome = arguments.Length >= 5 ? arguments[4] : string.Empty;
    var projectId = Path.GetFileName(Path.TrimEndingDirectorySeparator(repository));
    var request = new PreflightRequest(
        projectId,
        repository,
        outputRoot,
        900,
        900,
        900,
        1800,
        "0.8.15",
        "1.25.3",
        javaHome,
        modulePath);
    var service = new RepositoryPreflightService(new ProcessRunner());
    var report = await service.RunAsync(
        request,
        new InlineProgress<PreflightProgress>(item =>
            Console.WriteLine($"[{item.CompletedChecks}/{item.TotalChecks}] {item.Message}")));
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(
        report,
        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    return report.Eligible ? 0 : 2;
}

static async Task<int> RunSubjectSelectionAsync(string[] arguments)
{
    var workspace = WorkspaceLayout.FindWorkspaceRoot(Environment.CurrentDirectory);
    var layout = new WorkspaceLayout(workspace);
    var config = await new JsonExperimentConfigStore(layout).LoadAsync();
    var projectsRoot = arguments.Length >= 2
        ? Path.GetFullPath(arguments[1])
        : layout.ResolveOutputRoot(config.Subjects.DatasetProjectsRoot);
    var outputRoot = Path.Combine(layout.ResolveOutputRoot(config.OutputRoot), "subjects");
    var javaHome = arguments.Length >= 3 ? Path.GetFullPath(arguments[2]) : config.Tools.JavaHome;
    var report = await new SubjectSelectionService(new ProcessRunner()).RunAsync(
        new SubjectSelectionRequest(
            projectsRoot,
            outputRoot,
            workspace,
            config.Subjects.BenchmarkRevision,
            javaHome,
            config.Subjects.PilotCount,
            config.Subjects.OfficialCount,
            config.Subjects.MaxPerProject,
            config.Subjects.SelectionSeed),
        new InlineProgress<string>(Console.WriteLine));
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(
        report,
        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    return report.AutomatedSelectionComplete ? 0 : 2;
}

static async Task<int> RunStructuralTargetsAsync(string[] arguments)
{
    var workspace = WorkspaceLayout.FindWorkspaceRoot(Environment.CurrentDirectory);
    var layout = new WorkspaceLayout(workspace);
    var config = await new JsonExperimentConfigStore(layout).LoadAsync();
    var javaHome = arguments.Length >= 2 ? Path.GetFullPath(arguments[1]) : config.Tools.JavaHome;
    var outputBase = layout.ResolveOutputRoot(config.OutputRoot);
    var report = await new StructuralTargetService(new ProcessRunner()).RunAsync(
        new Rq1Experiment.Contracts.Targets.StructuralTargetRequest(
            workspace,
            layout.ResolveOutputRoot(config.Subjects.DatasetProjectsRoot),
            Path.Combine(outputBase, "subjects"),
            Path.Combine(outputBase, "targets"),
            javaHome),
        new InlineProgress<string>(Console.WriteLine));
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(
        report,
        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    return report.Complete ? 0 : 2;
}

static async Task<int> RunRepositoryContextsAndPromptsAsync(string[] arguments)
{
    var workspace = WorkspaceLayout.FindWorkspaceRoot(Environment.CurrentDirectory);
    var layout = new WorkspaceLayout(workspace);
    var config = await new JsonExperimentConfigStore(layout).LoadAsync();
    var javaHome = arguments.Length >= 2 ? Path.GetFullPath(arguments[1]) : config.Tools.JavaHome;
    var output = layout.ResolveOutputRoot(config.OutputRoot);
    var report = await new RepositoryContextPromptService(new ProcessRunner()).RunAsync(
        new Rq1Experiment.Contracts.Prompts.RepositoryContextPromptRequest(
            workspace,
            layout.ResolveOutputRoot(config.Subjects.DatasetProjectsRoot),
            Path.Combine(output, "subjects"),
            Path.Combine(output, "targets"),
            output,
            javaHome,
            config.Llm.Provider,
            config.Llm.BaseUrl,
            config.Llm.ModelId,
            config.Mode,
            config.ExperimentId),
        new InlineProgress<string>(Console.WriteLine));
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(
        report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    return report.Complete ? 0 : 2;
}

static async Task<int> RunDirectBaselineAsync(string[] arguments)
{
    var workspace = WorkspaceLayout.FindWorkspaceRoot(Environment.CurrentDirectory);
    var layout = new WorkspaceLayout(workspace);
    var config = await LoadCommandConfigAsync(layout, arguments, experimentIdIndex: 2);
    var mode = arguments.Length >= 2 ? arguments[1] : config.Mode;
    await PrepareFrozenRunAsync(layout, config, mode);
    var service = new DirectBaselineService(
        new LlmClientFactory(), CreateExecutionClient(config, layout));
    var report = await service.RunAsync(new DirectBaselineRequest(
        config.ExperimentId, mode, layout.ResolveOutputRoot(config.OutputRoot),
        config.Llm.Provider, config.Llm.BaseUrl, config.Llm.ModelId, config.Llm.Temperature,
        config.Llm.TimeoutSeconds, config.Generation.MaxTestMethods,
        config.Generation.TotalTokenCeilingPerRun, config.Subjects.Repetitions,
        config.Llm.TransportRetryCount, config.Generation.MaxOutputTokensPerCall,
        config.Llm.DeepSeekThinkingMode, config.Generation.MaxParallelLlmCalls),
        new InlineProgress<string>(Console.WriteLine));
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(
        report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    return report.Complete ? 0 : 2;
}

static async Task<int> RunPlannedPipelineAsync(string[] arguments)
{
    var workspace = WorkspaceLayout.FindWorkspaceRoot(Environment.CurrentDirectory);
    var layout = new WorkspaceLayout(workspace);
    var config = await LoadCommandConfigAsync(layout, arguments, experimentIdIndex: 2);
    var mode = arguments.Length >= 2 ? arguments[1] : config.Mode;
    await PrepareFrozenRunAsync(layout, config, mode);
    var service = new PlannedPipelineService(new LlmClientFactory(),
        CreateExecutionClient(config, layout));
    var report = await service.RunAsync(new PlannedPipelineRequest(
        config.ExperimentId, mode, layout.ResolveOutputRoot(config.OutputRoot),
        config.Llm.Provider, config.Llm.BaseUrl, config.Llm.ModelId, config.Llm.Temperature,
        config.Llm.TimeoutSeconds, config.Generation.MaxTestMethods,
        config.Generation.TotalTokenCeilingPerRun, config.Subjects.Repetitions,
        config.Llm.TransportRetryCount, config.Generation.MaxOutputTokensPerCall,
        config.Llm.DeepSeekThinkingMode, config.Generation.MaxParallelLlmCalls),
        new InlineProgress<string>(Console.WriteLine));
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(
        report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    return report.Complete ? 0 : 2;
}

static async Task<int> RehydrateV4GeneratorRequestsAsync(string[] arguments)
{
    if (arguments.Length < 3)
        throw new ArgumentException(
            "Usage: --rehydrate-v4-generator-requests <experiment-id> <subject-id> [subject-id ...]");

    var workspace = WorkspaceLayout.FindWorkspaceRoot(Environment.CurrentDirectory);
    var layout = new WorkspaceLayout(workspace);
    var experimentId = arguments[1];
    var subjectIds = arguments.Skip(2).Distinct(StringComparer.Ordinal).ToArray();
    var outputRoot = layout.ResolveOutputRoot("experiments/rq1");
    var experimentRoot = Path.Combine(outputRoot, "runs", experimentId);
    var snapshotPath = Path.Combine(experimentRoot, "experiment-config.snapshot.json");
    if (!File.Exists(snapshotPath))
        throw new FileNotFoundException("The immutable experiment snapshot was not found.", snapshotPath);

    var jsonOptions = new System.Text.Json.JsonSerializerOptions
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
    var config = System.Text.Json.JsonSerializer.Deserialize<ExperimentConfig>(
                     await File.ReadAllTextAsync(snapshotPath), jsonOptions)
                 ?? throw new InvalidOperationException("The experiment snapshot is invalid.");
    if (!string.Equals(config.ExperimentId, experimentId, StringComparison.Ordinal))
        throw new InvalidOperationException("Experiment ID does not match the run snapshot.");
    if (!string.Equals(config.Generation.PipelineContractVersion, "rq1-pipeline-contract-v4", StringComparison.Ordinal))
        throw new InvalidOperationException("This recovery command accepts only pipeline contract v4.");

    // Prove that the current request factory still reproduces a frozen v4 request byte-for-byte
    // before using it to reconstruct evidence that was omitted from three completed runs.
    var referenceRequestPath = Directory.EnumerateFiles(
            experimentRoot, "request.json", SearchOption.AllDirectories)
        .FirstOrDefault(path => path.Contains($"{Path.DirectorySeparatorChar}generator{Path.DirectorySeparatorChar}",
            StringComparison.Ordinal));
    if (referenceRequestPath is null)
        throw new InvalidOperationException("No completed v4 generator request is available as a compatibility witness.");
    using (var referenceDocument = System.Text.Json.JsonDocument.Parse(
               await File.ReadAllTextAsync(referenceRequestPath)))
    {
        var reference = referenceDocument.RootElement;
        var referenceDirectory = Path.GetDirectoryName(referenceRequestPath)!;
        var witness = LlmRequestFactory.Configure(
            LlmRequestFactory.Create(
                reference.GetProperty("providerId").GetString()!,
                reference.GetProperty("modelId").GetString()!,
                reference.GetProperty("role").GetString()!,
                await File.ReadAllTextAsync(Path.Combine(referenceDirectory, "system-prompt.md")),
                await File.ReadAllTextAsync(Path.Combine(referenceDirectory, "user-prompt.md"))),
            reference.GetProperty("baseEndpoint").GetString() ?? string.Empty,
            reference.GetProperty("temperature").GetDouble(),
            reference.GetProperty("maxOutputTokens").GetInt32(),
            reference.GetProperty("requireJsonObject").GetBoolean(),
            reference.GetProperty("transportRetryCount").GetInt32(),
            reference.GetProperty("deepSeekThinkingMode").GetString() ?? "enabled");
        if (!string.Equals(witness.RequestHash, reference.GetProperty("requestHash").GetString(),
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The current request factory does not reproduce the frozen v4 request hash; recovery aborted.");
    }

    var frozenSystemPromptPath = Directory.EnumerateFiles(
            experimentRoot, "system-prompt.md", SearchOption.AllDirectories)
        .First(path => path.Contains($"{Path.DirectorySeparatorChar}generator{Path.DirectorySeparatorChar}",
            StringComparison.Ordinal));
    var frozenSystemPrompt = await File.ReadAllTextAsync(frozenSystemPromptPath);

    foreach (var subjectId in subjectIds)
    {
        var runRoot = Path.Combine(experimentRoot, subjectId, "PLANNED", "R01");
        var generatorRoot = Path.Combine(runRoot, "generator");
        var requestPath = Path.Combine(generatorRoot, "request.json");
        if (File.Exists(requestPath))
            throw new InvalidOperationException($"{subjectId}: generator/request.json already exists; refusing to overwrite it.");

        var protectedPaths = new[]
        {
            Path.Combine(generatorRoot, "assistant-content.raw.txt"),
            Path.Combine(generatorRoot, "provider-response.raw.json"),
            Path.Combine(generatorRoot, "planned-output.json"),
            Path.Combine(generatorRoot, "scenario-test-mapping.json")
        };
        if (protectedPaths.Any(path => !File.Exists(path)))
            throw new InvalidOperationException($"{subjectId}: a protected completed generator artifact is missing.");
        var protectedHashes = protectedPaths.ToDictionary(path => path, Sha256File, StringComparer.Ordinal);

        var contextPath = Path.Combine(runRoot, "input", "common-context.json");
        var planPath = Path.Combine(runRoot, "planner", "scenario-plan.json");
        var runPath = Path.Combine(runRoot, "run.json");
        var contextJson = await File.ReadAllTextAsync(contextPath);
        var planJson = await File.ReadAllTextAsync(planPath);
        var context = System.Text.Json.JsonSerializer.Deserialize<RepositoryContext>(contextJson, jsonOptions)
                      ?? throw new InvalidOperationException($"{subjectId}: invalid common context.");
        var plan = System.Text.Json.JsonSerializer.Deserialize<ScenarioPlan>(planJson, jsonOptions)
                   ?? throw new InvalidOperationException($"{subjectId}: invalid scenario plan.");
        var record = System.Text.Json.JsonSerializer.Deserialize<PlannedRunRecord>(
                         await File.ReadAllTextAsync(runPath), jsonOptions)
                     ?? throw new InvalidOperationException($"{subjectId}: invalid run record.");
        if (!string.Equals(context.SubjectId, subjectId, StringComparison.Ordinal)
            || !string.Equals(plan.SubjectId, subjectId, StringComparison.Ordinal)
            || !string.Equals(record.SubjectId, subjectId, StringComparison.Ordinal))
            throw new InvalidOperationException($"{subjectId}: subject identity mismatch.");
        if (plan.Scenarios.Count == 0 || plan.Scenarios.Count != record.ScenarioCount)
            throw new InvalidOperationException($"{subjectId}: scenario count does not match the completed run record.");

        var requiredTestClassName = GeneratedTestIdentity.For(context);
        var generatorUser = "Generate exactly one test method for every finalized scenario; do not merge or split scenarios.\n\n"
                            + "COMMON_CONTEXT_JSON:\n" + contextJson
                            + "\n\nFINALIZED_SCENARIO_PLAN_JSON:\n" + planJson
                            + "\n\nREQUIRED_TEST_CLASS_FQN:\n" + requiredTestClassName
                            + $"\n\nHARD_LIMIT: Generate exactly {plan.Scenarios.Count} non-parameterized @Test methods and a bijective mapping.\n";
        var remainingTokens = config.Generation.TotalTokenCeilingPerRun - (record.PlannerTotalTokens ?? 0);
        var allocation = ConservativeTokenBudget.Allocate(
            remainingTokens, frozenSystemPrompt + generatorUser,
            config.Generation.MaxOutputTokensPerCall);
        if (allocation.MaxOutputTokens < 256)
            throw new InvalidOperationException($"{subjectId}: reconstructed generator token budget is invalid.");

        var request = LlmRequestFactory.Configure(
            LlmRequestFactory.Create(config.Llm.Provider, config.Llm.ModelId, "PLANNED_GENERATOR",
                frozenSystemPrompt, generatorUser),
            config.Llm.BaseUrl, config.Llm.Temperature, allocation.MaxOutputTokens,
            transportRetryCount: config.Llm.TransportRetryCount,
            deepSeekThinkingMode: config.Llm.DeepSeekThinkingMode);

        Directory.CreateDirectory(generatorRoot);
        await File.WriteAllTextAsync(Path.Combine(generatorRoot, "system-prompt.md"), frozenSystemPrompt);
        await File.WriteAllTextAsync(Path.Combine(generatorRoot, "user-prompt.md"), generatorUser);
        await WriteJsonAtomicAsync(requestPath, new
        {
            pipelineContractVersion = config.Generation.PipelineContractVersion,
            request.ProviderId,
            request.ModelId,
            request.Role,
            request.BaseEndpoint,
            request.Temperature,
            request.MaxOutputTokens,
            request.RequireJsonObject,
            request.ThinkingLevel,
            request.DeepSeekThinkingMode,
            request.TransportRetryCount,
            requiredTestClassName,
            tokenBudgetEstimatorVersion = allocation.EstimatorVersion,
            estimatedInputTokens = allocation.EstimatedInputTokens,
            tokenSafetyReserveTokens = allocation.SafetyReserveTokens,
            responseJsonSchemaHash = Sha256Text(request.ResponseJsonSchema),
            request.RequestHash,
            contextHash = context.ContextHash,
            treatmentHash = Sha256Text(planJson)
        }, jsonOptions);

        record.GeneratorRequestHash = request.RequestHash;
        await WriteJsonAtomicAsync(runPath, record, jsonOptions);

        foreach (var protectedPath in protectedPaths)
            if (!string.Equals(protectedHashes[protectedPath], Sha256File(protectedPath), StringComparison.Ordinal))
                throw new InvalidOperationException($"{subjectId}: protected artifact changed unexpectedly: {protectedPath}");

        Console.WriteLine($"{subjectId}: restored generator request {request.RequestHash}; protected outputs unchanged.");
    }

    return 0;
}

static async Task WriteJsonAtomicAsync<T>(
    string path, T value, System.Text.Json.JsonSerializerOptions options)
{
    var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
    await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                     FileShare.None, 4096, FileOptions.Asynchronous))
        await System.Text.Json.JsonSerializer.SerializeAsync(stream, value, options);
    File.Move(temporaryPath, path, true);
}

static string Sha256Text(string value) => Convert.ToHexString(
    System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

static string Sha256File(string path) => Convert.ToHexString(
    System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

static async Task<int> RunToolExecutionAsync(string[] arguments)
{
    var workspace = WorkspaceLayout.FindWorkspaceRoot(Environment.CurrentDirectory);
    var layout = new WorkspaceLayout(workspace);
    var config = await LoadCommandConfigAsync(layout, arguments, experimentIdIndex: 1);
    var experimentId = config.ExperimentId;
    var javaHome = arguments.Length >= 3
        ? arguments[2]
        : Directory.Exists(config.Tools.JavaHome) ? config.Tools.JavaHome : string.Empty;
    var request = new ToolExecutionRequest(
        experimentId, config.Mode, layout.ResolveOutputRoot(config.OutputRoot), workspace,
        layout.ResolveOutputRoot(config.Subjects.DatasetProjectsRoot), javaHome,
        config.Tools.CompileTimeoutSeconds, config.Tools.TestTimeoutSeconds,
        config.Tools.JacocoTimeoutSeconds, config.Tools.PitTimeoutSeconds,
        config.Tools.JacocoVersion, config.Tools.PitVersion,
        config.Tools.MaxParallelToolRuns);
    var service = new GatedToolExecutionService(new ProcessRunner());
    var compile = await service.RunCompileAndTestAsync(request, new InlineProgress<string>(Console.WriteLine));
    var adequacy = await service.RunAdequacyAsync(request, new InlineProgress<string>(Console.WriteLine));
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { compile, adequacy },
        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    return compile.Complete && adequacy.Complete ? 0 : 2;
}

static async Task<int> RebuildMetricsLedgerAsync(string[] arguments)
{
    var workspace = WorkspaceLayout.FindWorkspaceRoot(Environment.CurrentDirectory);
    var layout = new WorkspaceLayout(workspace);
    var store = new JsonExperimentConfigStore(layout);
    var experimentId = arguments.Length >= 2 ? arguments[1] : (await store.LoadAsync()).ExperimentId;
    var config = await store.LoadExperimentAsync(experimentId)
                 ?? throw new InvalidOperationException($"Experiment config not found: {experimentId}");
    var report = await new MetricsLedgerService().RebuildAsync(new MetricsLedgerRequest(
            experimentId, config.Mode, layout.ResolveOutputRoot(config.OutputRoot), workspace,
            Path.Combine(layout.ExperimentConfigHistoryDirectory, experimentId + ".json")),
        new InlineProgress<string>(Console.WriteLine));
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report,
        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    return report.Complete ? 0 : 2;
}

static async Task<ExperimentConfig> LoadCommandConfigAsync(
    WorkspaceLayout layout,
    string[] arguments,
    int experimentIdIndex)
{
    var store = new JsonExperimentConfigStore(layout);
    if (arguments.Length <= experimentIdIndex)
        return await store.LoadAsync();

    var experimentId = arguments[experimentIdIndex];
    return await store.LoadExperimentAsync(experimentId)
           ?? throw new InvalidOperationException($"Experiment config not found: {experimentId}");
}

static IFailoverLlmClient CreateExecutionClient(ExperimentConfig config, WorkspaceLayout layout)
{
    if (config.Llm.Provider.Equals(ProviderIds.CodexCli, StringComparison.OrdinalIgnoreCase))
    {
        return new CodexExecLlmClient(new CodexExecLlmClientOptions
        {
            ExecutablePath = config.Llm.CodexExecutablePath,
            ReasoningEffort = config.Llm.CodexReasoningEffort,
            ServiceTier = config.Llm.CodexServiceTier
        });
    }

    var keyPool = new SequentialApiKeyPool(new FileApiKeyStore(layout), layout);
    return new FailoverLlmClient(keyPool,
        new HttpLlmTransport(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }));
}

static async Task PrepareFrozenRunAsync(
    WorkspaceLayout layout,
    ExperimentConfig config,
    string mode)
{
    if (!mode.Equals("Official", StringComparison.OrdinalIgnoreCase)) return;
    if (!config.Mode.Equals("Official", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("An Official command requires an Official experiment config.");
    if (!config.IsFrozen)
        throw new InvalidOperationException("Official experiment config must be frozen before generation.");

    if (config.Llm.Provider.Equals(ProviderIds.CodexCli, StringComparison.OrdinalIgnoreCase))
    {
        if (!config.PilotWaived)
            throw new InvalidOperationException("This Codex Official run requires an explicit recorded pilot waiver.");
        if (config.Subjects.Repetitions != 1 || config.Subjects.OfficialCount * 2 != 300)
            throw new InvalidOperationException("The authorized Codex schedule must contain exactly 300 run records.");
        var contextPath = Path.Combine(layout.ResolveOutputRoot(config.OutputRoot), "context", "contexts-official.jsonl");
        if (!File.Exists(contextPath) || File.ReadLines(contextPath).Count(line => !string.IsNullOrWhiteSpace(line)) != config.Subjects.OfficialCount)
            throw new InvalidOperationException("Frozen Official context count does not match the authorized schedule.");
    }

    var lockResult = await new OfficialConfigLockService(layout).ValidateOrCreateAsync(config);
    if (!lockResult.IsAccepted)
        throw new InvalidOperationException(lockResult.Message);

    var source = Path.Combine(layout.ExperimentConfigHistoryDirectory, config.ExperimentId + ".json");
    if (!File.Exists(source))
        throw new FileNotFoundException("Historical experiment config is required before an Official run.", source);
    var experimentRoot = Path.Combine(layout.ResolveOutputRoot(config.OutputRoot), "runs", config.ExperimentId);
    Directory.CreateDirectory(experimentRoot);
    var snapshot = Path.Combine(experimentRoot, "experiment-config.snapshot.json");
    if (!File.Exists(snapshot))
    {
        File.Copy(source, snapshot, false);
    }
    else if (!File.ReadAllBytes(source).SequenceEqual(File.ReadAllBytes(snapshot)))
    {
        throw new InvalidOperationException("Frozen experiment config differs from the existing run snapshot.");
    }
}

sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

sealed class ThrowingSubjectSelectionService : ISubjectSelectionService
{
    public Task<SubjectSelectionReport> RunAsync(
        SubjectSelectionRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Synthetic analyzer build failure");
}

sealed class ThrowingStructuralTargetService : IStructuralTargetService
{
    public Task<StructuralTargetReport> RunAsync(
        StructuralTargetRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Synthetic target extraction failure");
}

sealed class SequenceTransport(params LlmTransportOutcome[] outcomes) : ILlmTransport
{
    private readonly Queue<LlmTransportOutcome> _outcomes = new(outcomes);
    public List<string> RequestHashes { get; } = [];

    public Task<LlmTransportResult> SendAsync(
        LlmRequest request,
        ApiKeyLease key,
        CancellationToken cancellationToken = default)
    {
        RequestHashes.Add(request.RequestHash);
        var outcome = _outcomes.Dequeue();
        return Task.FromResult(new LlmTransportResult(
            outcome,
            outcome == LlmTransportOutcome.Success ? "ok" : null,
            outcome == LlmTransportOutcome.Success ? request.ModelId : null,
            outcome == LlmTransportOutcome.Success ? null : "QUOTA"));
    }
}

sealed class StubFailoverLlmClient(string response, int totalTokens) : IFailoverLlmClient
{
    public int CallCount { get; private set; }
    public int LastMaxOutputTokens { get; private set; }

    public Task<LlmCallResult> SendAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastMaxOutputTokens = request.MaxOutputTokens;
        return Task.FromResult(new LlmCallResult(
            true, false, request.RequestHash, response, request.ModelId, 1, 1, ["KEY_001"], null)
        {
            RawResponseText = "{\"fixture\":true}",
            InputTokens = Math.Max(1, totalTokens - 10),
            OutputTokens = 10,
            TotalTokens = totalTokens,
            FinishReason = "stop",
            HttpStatusCode = 200
        });
    }
}

sealed class TrackingFailoverLlmClient(
    string response,
    int totalTokens,
    int delayMilliseconds) : IFailoverLlmClient
{
    private int _active;
    private int _maxObserved;
    public int MaxObservedConcurrency => Volatile.Read(ref _maxObserved);

    public async Task<LlmCallResult> SendAsync(
        LlmRequest request,
        CancellationToken cancellationToken = default)
    {
        var active = Interlocked.Increment(ref _active);
        while (true)
        {
            var observed = Volatile.Read(ref _maxObserved);
            if (active <= observed || Interlocked.CompareExchange(ref _maxObserved, active, observed) == observed)
                break;
        }
        try
        {
            await Task.Delay(delayMilliseconds, cancellationToken);
            return new LlmCallResult(
                true, false, request.RequestHash, response, request.ModelId, 1, 1, ["KEY_001"], null)
            {
                RawResponseText = "{\"fixture\":true}",
                InputTokens = Math.Max(1, totalTokens - 10), OutputTokens = 10,
                TotalTokens = totalTokens, FinishReason = "stop", HttpStatusCode = 200
            };
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }
}

sealed class QueueFailoverLlmClient(params (string Response, int TotalTokens)[] responses) : IFailoverLlmClient
{
    private readonly Queue<(string Response, int TotalTokens)> _responses = new(responses);
    public int CallCount { get; private set; }

    public Task<LlmCallResult> SendAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        CallCount++;
        var next = _responses.Dequeue();
        return Task.FromResult(new LlmCallResult(
            true, false, request.RequestHash, next.Response, request.ModelId, 1, 1, ["KEY_001"], null)
        {
            RawResponseText = "{\"fixture\":true}",
            InputTokens = Math.Max(1, next.TotalTokens - 10), OutputTokens = 10,
            TotalTokens = next.TotalTokens, FinishReason = "stop", HttpStatusCode = 200
        });
    }
}

sealed class FakePreflightProcessRunner(
    bool failCompile = false,
    bool gitUnavailable = false) : IProcessRunner
{
    public Task<ProcessRunResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string?>? environment = null,
        CancellationToken cancellationToken = default)
    {
        var command = arguments.ToArray();
        if (executable == "git" && gitUnavailable)
            throw new System.ComponentModel.Win32Exception(2, "Synthetic missing Git");
        if (executable == "git")
            return Success("0123456789abcdef\n");
        if (command.Contains("--version", StringComparer.Ordinal))
            return Success("Apache Maven 3.9.9\nJava version: 17.0.12, vendor: Test\n");
        if (failCompile && command.Contains("test-compile", StringComparer.Ordinal)
                        && command.Contains("clean", StringComparer.Ordinal))
            return Failure("Synthetic compile failure");
        if (command.Any(value => value.Contains("jacoco-maven-plugin", StringComparison.Ordinal)))
        {
            var report = Path.Combine(workingDirectory, "target", "site", "jacoco", "jacoco.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, "<report />");
        }
        if (command.Contains("jacocoTestReport", StringComparer.Ordinal))
        {
            var report = Path.Combine(workingDirectory, "build", "reports", "jacoco", "test", "jacocoTestReport.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, "<report />");
        }
        if (command.Any(value => value.Contains("pitest-maven", StringComparison.Ordinal)))
        {
            var report = Path.Combine(workingDirectory, "target", "pit-reports", "mutations.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, "<mutations />");
        }
        if (command.Contains("pitest", StringComparer.Ordinal)
            && !command.Any(value => value.Contains("pitest-maven", StringComparison.Ordinal)))
        {
            var report = Path.Combine(workingDirectory, "build", "reports", "pitest", "mutations.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, "<mutations />");
        }
        return Success("BUILD SUCCESS\n");
    }

    private static Task<ProcessRunResult> Success(string output) =>
        Task.FromResult(new ProcessRunResult(0, false, TimeSpan.FromMilliseconds(5), output, string.Empty));

    private static Task<ProcessRunResult> Failure(string error) =>
        Task.FromResult(new ProcessRunResult(1, false, TimeSpan.FromMilliseconds(5), string.Empty, error));
}

sealed class FakeGatedToolProcessRunner(bool failCompile = false, bool zeroDiscoveredTests = false) : IProcessRunner
{
    public List<string> Gates { get; } = [];

    public Task<ProcessRunResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string?>? environment = null,
        CancellationToken cancellationToken = default)
    {
        var values = arguments.ToArray();
        var pit = values.Any(value => value.Contains("pitest", StringComparison.OrdinalIgnoreCase));
        var jacoco = values.Any(value => value.Contains("jacoco", StringComparison.OrdinalIgnoreCase));
        var compile = values.Contains("test-compile", StringComparer.Ordinal) && !pit;
        var gate = pit ? "PIT" : jacoco ? "JACOCO" : compile ? "COMPILE" : "JUNIT";
        Gates.Add(gate);
        if (failCompile && gate == "COMPILE")
            return Task.FromResult(new ProcessRunResult(1, false, TimeSpan.FromMilliseconds(2), "", "compile error"));
        if (gate == "JUNIT")
        {
            var report = Path.Combine(workingDirectory, "target", "surefire-reports", "TEST-FixtureGeneratedTest.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, zeroDiscoveredTests
                ? "<testsuite tests=\"0\" failures=\"0\" errors=\"0\" skipped=\"0\" />"
                : "<testsuite tests=\"1\" failures=\"0\" errors=\"0\" skipped=\"0\" />");
        }
        if (gate == "JACOCO")
        {
            var report = Path.Combine(workingDirectory, "target", "site", "jacoco", "jacoco.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, """
                <report><package name=""><class name="Fixture">
                <method name="value" line="1"><counter type="LINE" missed="0" covered="3"/><counter type="BRANCH" missed="1" covered="1"/><counter type="INSTRUCTION" missed="2" covered="8"/></method>
                <method name="unrelated" line="1"><counter type="BRANCH" missed="0" covered="4"/></method>
                </class><sourcefile name="Fixture.java"><line nr="1" mi="0" ci="8" mb="0" cb="2"/></sourcefile></package></report>
                """);
        }
        if (gate == "PIT")
        {
            var report = Path.Combine(workingDirectory, "target", "pit-reports", "mutations.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, """
                <mutations>
                <mutation status="KILLED"><mutatedClass>Fixture</mutatedClass><mutatedMethod>value</mutatedMethod><lineNumber>1</lineNumber></mutation>
                <mutation status="SURVIVED"><mutatedClass>Fixture</mutatedClass><mutatedMethod>value</mutatedMethod><lineNumber>2</lineNumber></mutation>
                <mutation status="NO_COVERAGE"><mutatedClass>Fixture</mutatedClass><mutatedMethod>value</mutatedMethod><lineNumber>3</lineNumber></mutation>
                <mutation status="KILLED"><mutatedClass>Fixture</mutatedClass><mutatedMethod>value</mutatedMethod><lineNumber>20</lineNumber></mutation>
                <mutation status="KILLED"><mutatedClass>Fixture</mutatedClass><mutatedMethod>unrelated</mutatedMethod><lineNumber>2</lineNumber></mutation>
                <mutation status="KILLED"><mutatedClass>Other</mutatedClass><mutatedMethod>value</mutatedMethod><lineNumber>2</lineNumber></mutation>
                </mutations>
                """);
        }
        return Task.FromResult(new ProcessRunResult(0, false, TimeSpan.FromMilliseconds(2), "ok", ""));
    }
}

sealed class ModelListHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var host = request.RequestUri!.Host;
        var json = host switch
        {
            "generativelanguage.googleapis.com" =>
                "{\"models\":[{\"baseModelId\":\"gemini-generate\",\"supportedGenerationMethods\":[\"generateContent\"]},{\"baseModelId\":\"gemini-embed\",\"supportedGenerationMethods\":[\"embedContent\"]}]}",
            "api.deepseek.com" =>
                "{\"object\":\"list\",\"data\":[{\"id\":\"deepseek-chat\",\"object\":\"model\"}]}",
            "openrouter.ai" =>
                "{\"data\":[{\"id\":\"provider/model\",\"name\":\"Model\"}]}",
            "api.justwoker.icu" =>
                "{\"object\":\"list\",\"data\":[{\"id\":\"claude-opus-5-thinking\",\"object\":\"model\"}]}",
            _ => throw new InvalidOperationException("Unexpected model-list endpoint: " + request.RequestUri)
        };
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        });
    }
}

sealed class CompletionHandler : HttpMessageHandler
{
    public string LastRequestBody { get; private set; } = string.Empty;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        LastRequestBody = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        var gemini = request.RequestUri!.Host.Contains("googleapis", StringComparison.Ordinal);
        var json = gemini
            ? "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"{\\\"ok\\\":true}\"}]},\"finishReason\":\"STOP\"}],\"modelVersion\":\"model-id\",\"usageMetadata\":{\"promptTokenCount\":20,\"candidatesTokenCount\":10,\"totalTokenCount\":30}}"
            : "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"{\\\"ok\\\":true}\"},\"finish_reason\":\"stop\"}],\"model\":\"model-id\",\"usage\":{\"prompt_tokens\":20,\"completion_tokens\":10,\"total_tokens\":30}}";
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
    }
}

sealed class StreamingCompletionHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        const string sse = """
            data: {"model":"claude-opus-5","choices":[{"delta":{"role":"assistant","content":"{\"ok\":"},"finish_reason":null}]}

            data: {"model":"claude-opus-5","choices":[{"delta":{"content":"true}"},"finish_reason":"stop"}]}

            data: {"model":"claude-opus-5-thinking","choices":[],"usage":{"prompt_tokens":20,"completion_tokens":10,"total_tokens":30}}

            data: [DONE]

            """;
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(sse, System.Text.Encoding.UTF8, "text/event-stream")
        });
    }
}

sealed class ErrorResponseHandler(System.Net.HttpStatusCode statusCode, string body) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        });
}
