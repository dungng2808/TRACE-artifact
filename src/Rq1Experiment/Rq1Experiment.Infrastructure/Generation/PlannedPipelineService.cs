using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rq1Experiment.Contracts.Config;
using Rq1Experiment.Contracts.Generation;
using Rq1Experiment.Contracts.Prompts;
using Rq1Experiment.Contracts.Targets;
using Rq1Experiment.Core.Abstractions;
using Rq1Experiment.Core.Llm;
using Rq1Experiment.Infrastructure.Persistence;

namespace Rq1Experiment.Infrastructure.Generation;

public sealed class PlannedPipelineService(
    ILlmClientFactory clientFactory,
    IFailoverLlmClient failoverClient) : IPlannedPipelineService
{
    private static readonly JsonSerializerOptions CompactJson = new(JsonSupport.Options) { WriteIndented = false };

    public async Task<PlannedPipelineReport> RunAsync(
        PlannedPipelineRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(request.ExperimentOutputRoot);
        var splitName = request.Mode.Equals("Official", StringComparison.OrdinalIgnoreCase) ? "official"
            : request.Mode.Equals("Smoke", StringComparison.OrdinalIgnoreCase) ? "smoke" : "pilot";
        var contexts = await ReadLinesAsync<RepositoryContext>(
            Path.Combine(root, "context", $"contexts-{splitName}.jsonl"), cancellationToken);
        var inventories = await ReadLinesAsync<StructuralTargetInventory>(
            Path.Combine(root, "targets", $"targets-{splitName}.jsonl"), cancellationToken);
        contexts = contexts.OrderBy(item => item.SubjectId, StringComparer.Ordinal).ToList();
        var inventoryBySubject = inventories.ToDictionary(item => item.SubjectId, StringComparer.Ordinal);
        if (contexts.Any(context => !inventoryBySubject.ContainsKey(context.SubjectId)))
            throw new InvalidOperationException("Every PLANNED subject requires a frozen Phase 4 target inventory.");
        var repetitions = request.Mode.Equals("Smoke", StringComparison.OrdinalIgnoreCase) ? 1 : request.Repetitions;
        var templates = await LoadTemplatesAsync(root, cancellationToken);
        var records = new ConcurrentBag<PlannedRunRecord>();
        var expected = contexts.Count * repetitions;
        var sequence = 0;

        await Parallel.ForEachAsync(contexts, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(request.MaxParallelLlmCalls, 1, 32),
            CancellationToken = cancellationToken
        }, async (context, _) =>
        {
        for (var repetition = 1; repetition <= repetitions; repetition++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentSequence = Interlocked.Increment(ref sequence);
            var runId = $"{context.SubjectId}-PLANNED-R{repetition:00}";
            var runRoot = Path.Combine(root, "runs", request.ExperimentId, context.SubjectId, "PLANNED", $"R{repetition:00}");
            var recordPath = Path.Combine(runRoot, "run.json");
            var existing = await JsonSupport.ReadAsync<PlannedRunRecord>(recordPath, cancellationToken);
            EnsureCompatibleCheckpoint(existing, runId);
            if (IsTerminal(existing))
            {
                records.Add(existing!);
                progress?.Report($"[{currentSequence}/{expected}] Reusing immutable PLANNED artifact {runId}: {existing!.Status}.");
                continue;
            }

            var started = existing?.StartedAt ?? DateTimeOffset.UtcNow;
            var inventory = inventoryBySubject[context.SubjectId];
            var contextJson = JsonSerializer.Serialize(context, JsonSupport.Options);
            var inventoryJson = JsonSerializer.Serialize(inventory, JsonSupport.Options);
            var inventoryHash = Sha256(inventoryJson);
            var requiredTestClassName = GeneratedTestIdentity.For(context);
            Directory.CreateDirectory(Path.Combine(runRoot, "input"));
            Directory.CreateDirectory(Path.Combine(runRoot, "planner"));
            Directory.CreateDirectory(Path.Combine(runRoot, "generator"));
            await File.WriteAllTextAsync(Path.Combine(runRoot, "input", "common-context.json"), contextJson, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(runRoot, "input", "structural-target-inventory.json"), inventoryJson, cancellationToken);

            ScenarioPlan? plan = null;
            LlmCallResult? plannerResult = null;
            var planPath = Path.Combine(runRoot, "planner", "scenario-plan.json");
            if (CanResumeGenerator(existing, planPath))
            {
                plan = await JsonSupport.ReadAsync<ScenarioPlan>(planPath, cancellationToken);
                progress?.Report($"[{currentSequence}/{expected}] Resuming Generator for {runId} from validated immutable plan.");
            }
            else
            {
                progress?.Report($"[{currentSequence}/{expected}] Planner for {context.SubjectId}, repetition {repetition}...");
                var plannerUser = templates.PlannerUser
                    .Replace("{{COMMON_CONTEXT_JSON}}", contextJson, StringComparison.Ordinal)
                    .Replace("{{STRUCTURAL_TARGET_INVENTORY_JSON}}", inventoryJson, StringComparison.Ordinal)
                    + $"\nHARD_LIMIT: Produce 1 to {request.MaxTestMethods} scenarios. IDs must be S01 through SN in array order. "
                    + "When obligations exceed the cap, prioritize OFFICIAL targets by source line, then EXPLICIT_EXCEPTION_PATH, then RETURN_PATH; one scenario may own multiple target IDs.\n";
                var plannerAllocation = ConservativeTokenBudget.Allocate(
                    request.TotalTokenCeilingPerRun, templates.PlannerSystem + plannerUser,
                    request.MaxOutputTokensPerCall);
                var plannerBudget = plannerAllocation.MaxOutputTokens;
                if (plannerBudget < 256)
                {
                    var budget = NewRecord(request, context, inventoryHash, repetition, runId, started);
                    budget.Status = "INVALID"; budget.FailureStage = "PLANNER";
                    budget.FailureCode = "INPUT_TOKEN_BUDGET_ESTIMATE_EXCEEDED";
                    await FinishAsync(recordPath, budget, cancellationToken); records.Add(budget); continue;
                }
                var plannerRequest = LlmRequestFactory.Configure(
                    clientFactory.Create(request.ProviderId).CreateRequest(
                        request.RequestedModelId, "STRUCTURAL_PLANNER", templates.PlannerSystem, plannerUser),
                    request.BaseEndpoint, request.Temperature, plannerBudget,
                    transportRetryCount: request.TransportRetryCount,
                    deepSeekThinkingMode: request.DeepSeekThinkingMode);
                await PersistRequestAsync(Path.Combine(runRoot, "planner"), plannerRequest,
                    context.ContextHash, inventoryHash, plannerAllocation, null, cancellationToken);
                plannerResult = await SendWithTimeoutAsync(plannerRequest, request.TimeoutSeconds, cancellationToken);
                await PersistResponseAsync(Path.Combine(runRoot, "planner"), plannerResult, cancellationToken);
                var plannerRecord = NewRecord(request, context, inventoryHash, repetition, runId, started);
                ApplyPlanner(plannerRecord, plannerRequest, plannerResult);
                if (!plannerResult.IsSuccess)
                {
                    plannerRecord.Status = plannerResult.IsWaitingForApiKey ? "WAITING_FOR_API_KEY" : "INVALID";
                    plannerRecord.FailureStage = "PLANNER";
                    plannerRecord.FailureCode = plannerResult.ErrorCode;
                    await FinishAsync(recordPath, plannerRecord, cancellationToken); records.Add(plannerRecord);
                    if (plannerRecord.Status == "WAITING_FOR_API_KEY") break;
                    continue;
                }
                if (plannerResult.TotalTokens is null)
                {
                    plannerRecord.Status = "INVALID"; plannerRecord.FailureStage = "PLANNER";
                    plannerRecord.FailureCode = "TOKEN_USAGE_MISSING";
                    await FinishAsync(recordPath, plannerRecord, cancellationToken); records.Add(plannerRecord); continue;
                }
                var validTargets = inventory.Targets.Select(item => item.TargetId).ToHashSet(StringComparer.Ordinal);
                var parsedPlan = StructuredLlmOutputParser.ParseScenarioPlan(
                    plannerResult.ResponseText, context.SubjectId, validTargets,
                    inventory.ControlFlowGraph, request.MaxTestMethods);
                if (!parsedPlan.IsValid)
                {
                    plannerRecord.Status = "INVALID"; plannerRecord.FailureStage = "PLANNER";
                    plannerRecord.FailureCode = parsedPlan.FailureCode;
                    await FinishAsync(recordPath, plannerRecord, cancellationToken); records.Add(plannerRecord); continue;
                }
                plan = parsedPlan.Plan!;
                plannerRecord.ScenarioCount = plan.Scenarios.Count;
                await JsonSupport.WriteAsync(planPath, plan, cancellationToken);
                await JsonSupport.WriteAsync(recordPath, plannerRecord, cancellationToken);
                existing = plannerRecord;
            }

            if (plan is null) throw new InvalidOperationException("Validated plan unexpectedly missing.");
            progress?.Report($"[{currentSequence}/{expected}] Generator for {context.SubjectId}, repetition {repetition} ({plan.Scenarios.Count} scenarios)...");
            var planJson = JsonSerializer.Serialize(plan, JsonSupport.Options);
            var generatorUser = templates.GeneratorUser
                .Replace("{{COMMON_CONTEXT_JSON}}", contextJson, StringComparison.Ordinal)
                .Replace("{{SCENARIO_PLAN_JSON}}", planJson, StringComparison.Ordinal)
                .Replace("{{REQUIRED_TEST_CLASS_FQN}}", requiredTestClassName, StringComparison.Ordinal)
                + $"\nHARD_LIMIT: Generate exactly {plan.Scenarios.Count} non-parameterized @Test methods and a bijective mapping.\n";
            var tokensAlreadyUsed = plannerResult?.TotalTokens ?? existing?.PlannerTotalTokens ?? 0;
            var remaining = request.TotalTokenCeilingPerRun - tokensAlreadyUsed;
            var generatorAllocation = ConservativeTokenBudget.Allocate(
                remaining, templates.GeneratorSystem + generatorUser,
                request.MaxOutputTokensPerCall);
            var generatorBudget = generatorAllocation.MaxOutputTokens;
            var record = existing ?? NewRecord(request, context, inventoryHash, repetition, runId, started);
            record.ScenarioCount = plan.Scenarios.Count;
            if (generatorBudget < 256)
            {
                record.Status = "INVALID"; record.FailureStage = "GENERATOR";
                record.FailureCode = "TOTAL_TOKEN_BUDGET_ESTIMATE_EXCEEDED";
                await FinishAsync(recordPath, record, cancellationToken); records.Add(record); continue;
            }
            var generatorRequest = LlmRequestFactory.Configure(
                clientFactory.Create(request.ProviderId).CreateRequest(
                    request.RequestedModelId, "PLANNED_GENERATOR", templates.GeneratorSystem, generatorUser),
                request.BaseEndpoint, request.Temperature, generatorBudget,
                transportRetryCount: request.TransportRetryCount,
                deepSeekThinkingMode: request.DeepSeekThinkingMode);
            await PersistRequestAsync(Path.Combine(runRoot, "generator"), generatorRequest,
                context.ContextHash, Sha256(planJson), generatorAllocation,
                requiredTestClassName, cancellationToken);
            var generatorResult = await SendWithTimeoutAsync(generatorRequest, request.TimeoutSeconds, cancellationToken);
            await PersistResponseAsync(Path.Combine(runRoot, "generator"), generatorResult, cancellationToken);
            ApplyGenerator(record, generatorRequest, generatorResult);
            if (!generatorResult.IsSuccess)
            {
                record.Status = generatorResult.IsWaitingForApiKey ? "WAITING_FOR_API_KEY" : "INVALID";
                record.FailureStage = "GENERATOR"; record.FailureCode = generatorResult.ErrorCode;
                await FinishAsync(recordPath, record, cancellationToken); records.Add(record);
                if (record.Status == "WAITING_FOR_API_KEY") break;
                continue;
            }
            if (generatorResult.TotalTokens is null)
            {
                record.Status = "INVALID"; record.FailureStage = "GENERATOR";
                record.FailureCode = "TOKEN_USAGE_MISSING";
                await FinishAsync(recordPath, record, cancellationToken); records.Add(record); continue;
            }
            record.TotalTokens = (record.PlannerTotalTokens ?? 0) + generatorResult.TotalTokens;
            if (record.TotalTokens > request.TotalTokenCeilingPerRun)
            {
                record.Status = "INVALID"; record.FailureStage = "GENERATOR";
                record.FailureCode = "TOKEN_CEILING_EXCEEDED";
                await FinishAsync(recordPath, record, cancellationToken); records.Add(record); continue;
            }
            var parsedOutput = StructuredLlmOutputParser.ParsePlannedOutput(
                generatorResult.ResponseText,
                plan,
                request.MaxTestMethods,
                context.Build.RequiredTestApi,
                context.FocalMethodSignature,
                context.FocalMethodOverloadSignatures,
                context.FocalClass,
                requiredTestClassName);
            if (!parsedOutput.IsValid)
            {
                record.Status = "INVALID"; record.FailureStage = "GENERATOR";
                record.FailureCode = parsedOutput.FailureCode;
                await FinishAsync(recordPath, record, cancellationToken); records.Add(record); continue;
            }
            var output = parsedOutput.Output!;
            record.Status = "VALID"; record.FailureStage = null; record.FailureCode = null;
            record.TestMethodCount = output.DeclaredTestMethods.Count; record.BijectionValid = true;
            await JsonSupport.WriteAsync(Path.Combine(runRoot, "generator", "planned-output.json"), output, cancellationToken);
            await JsonSupport.WriteAsync(Path.Combine(runRoot, "generator", "scenario-test-mapping.json"),
                new { output.Mappings }, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(runRoot, "generator", output.TestClassName.Split('.').Last() + ".java"),
                output.SourceCode, cancellationToken);
            await FinishAsync(recordPath, record, cancellationToken); records.Add(record);
        }
        });

        var orderedRecords = records
            .OrderBy(item => item.SubjectId, StringComparer.Ordinal)
            .ThenBy(item => item.Repetition)
            .ToList();

        var report = new PlannedPipelineReport
        {
            Mode = request.Mode, SelectedSubjectCount = contexts.Count, ExpectedRunCount = expected,
            CompletedRunCount = orderedRecords.Count(item => item.Status != "WAITING_FOR_API_KEY"),
            ValidOutputCount = orderedRecords.Count(item => item.Status == "VALID"),
            InvalidPlannerCount = orderedRecords.Count(item => item.Status == "INVALID" && item.FailureStage == "PLANNER"),
            InvalidGeneratorCount = orderedRecords.Count(item => item.Status == "INVALID" && item.FailureStage == "GENERATOR"),
            WaitingForApiKeyCount = orderedRecords.Count(item => item.Status == "WAITING_FOR_API_KEY"),
            PlannerSemanticCallCount = orderedRecords.Sum(item => item.PlannerSemanticCalls),
            GeneratorSemanticCallCount = orderedRecords.Sum(item => item.GeneratorSemanticCalls),
            RepairCallCount = orderedRecords.Sum(item => item.RepairCalls), ReviewerCallCount = orderedRecords.Sum(item => item.ReviewerCalls),
            ContractViolationCount = orderedRecords.Count(item => item.Status == "INVALID" && IsContractViolation(item.FailureCode)),
            NoHiddenRetryInvariant = orderedRecords.All(item => item.PlannerSemanticCalls is 0 or 1 && item.GeneratorSemanticCalls is 0 or 1),
            NoRepairOrReviewerTrace = orderedRecords.All(item => item.RepairCalls == 0 && item.ReviewerCalls == 0),
            Complete = orderedRecords.Count(item => item.Status != "WAITING_FOR_API_KEY") == expected
                       && orderedRecords.All(item => item.Status != "WAITING_FOR_API_KEY"),
            GeneratedAt = DateTimeOffset.UtcNow
        };
        await JsonSupport.WriteAsync(Path.Combine(root, "planned-pipeline-report.json"), report, cancellationToken);
        await WriteLinesAsync(Path.Combine(root, "planned-run-ledger.jsonl"), orderedRecords, cancellationToken);
        progress?.Report($"PLANNED pipeline processed {report.CompletedRunCount}/{expected}: valid={report.ValidOutputCount}, planner-invalid={report.InvalidPlannerCount}, generator-invalid={report.InvalidGeneratorCount}.");
        return report;
    }

    private async Task<LlmCallResult> SendWithTimeoutAsync(LlmRequest request, int seconds, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        try { return await failoverClient.SendAsync(request, timeout.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return new LlmCallResult(false, false, request.RequestHash, null, null, 1, 1, [], "REQUEST_TIMEOUT"); }
    }

    private static PlannedRunRecord NewRecord(PlannedPipelineRequest request, RepositoryContext context,
        string inventoryHash, int repetition, string runId, DateTimeOffset started) => new()
    {
        PipelineContractVersion = PipelineContractVersions.Current,
        RunId = runId, SubjectId = context.SubjectId, Split = context.Split, Repetition = repetition,
        ContextHash = context.ContextHash, TargetInventoryHash = inventoryHash,
        ProviderId = request.ProviderId, BaseEndpoint = request.BaseEndpoint,
        RequestedModelId = request.RequestedModelId, StartedAt = started
    };
    private static void ApplyPlanner(PlannedRunRecord record, LlmRequest request, LlmCallResult result)
    {
        record.PlannerRequestHash = request.RequestHash; record.PlannerResponseModelId = result.ResponseModelId;
        record.PlannerSemanticCalls = result.SemanticLlmCalls; record.TransportAttempts += result.TransportAttempts;
        record.KeySlots.AddRange(result.KeySlots); record.PlannerInputTokens = result.InputTokens;
        record.PlannerOutputTokens = result.OutputTokens; record.PlannerTotalTokens = result.TotalTokens;
        record.TotalTokens = result.TotalTokens;
    }
    private static void ApplyGenerator(PlannedRunRecord record, LlmRequest request, LlmCallResult result)
    {
        record.GeneratorRequestHash = request.RequestHash; record.GeneratorResponseModelId = result.ResponseModelId;
        record.GeneratorSemanticCalls = result.SemanticLlmCalls; record.TransportAttempts += result.TransportAttempts;
        record.KeySlots.AddRange(result.KeySlots); record.GeneratorInputTokens = result.InputTokens;
        record.GeneratorOutputTokens = result.OutputTokens; record.GeneratorTotalTokens = result.TotalTokens;
    }
    private static async Task FinishAsync(string path, PlannedRunRecord record, CancellationToken token)
    { record.CompletedAt = DateTimeOffset.UtcNow; await JsonSupport.WriteAsync(path, record, token); }
    private static bool IsTerminal(PlannedRunRecord? record) =>
        record is not null
        && record.Status is "VALID" or "INVALID"
        && record.CompletedAt != default;

    private static void EnsureCompatibleCheckpoint(PlannedRunRecord? record, string runId)
    {
        if (record is not null
            && !string.Equals(record.PipelineContractVersion,
                PipelineContractVersions.Current, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"PIPELINE_CONTRACT_MISMATCH: {runId} was created by an older generation contract. "
                + "Preserve it and create a new Experiment ID.");
    }

    private static bool CanResumeGenerator(PlannedRunRecord? record, string planPath) =>
        record is not null
        && File.Exists(planPath)
        && record.PlannerSemanticCalls > 0
        && record.ScenarioCount > 0
        && ((record.Status == "WAITING_FOR_API_KEY"
             && record.FailureStage == "GENERATOR")
            || string.IsNullOrWhiteSpace(record.Status));
    private static bool IsContractViolation(string? code) => code is not null &&
        (code.Contains("SCHEMA", StringComparison.Ordinal) || code.Contains("SCENARIO", StringComparison.Ordinal)
         || code.Contains("TARGET", StringComparison.Ordinal) || code.Contains("TECHNIQUE", StringComparison.Ordinal)
         || code.Contains("BIJECTION", StringComparison.Ordinal) || code.Contains("PARAMETERIZED", StringComparison.Ordinal)
         || code.Contains("TEST_COUNT", StringComparison.Ordinal));
    private static string Sha256(string text) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static async Task PersistRequestAsync(string root, LlmRequest request, string contextHash,
        string treatmentHash, TokenBudgetAllocation allocation, string? requiredTestClassName,
        CancellationToken token)
    {
        await File.WriteAllTextAsync(Path.Combine(root, "system-prompt.md"), request.SystemPrompt, token);
        await File.WriteAllTextAsync(Path.Combine(root, "user-prompt.md"), request.UserPrompt, token);
        await JsonSupport.WriteAsync(Path.Combine(root, "request.json"), new
        {
            pipelineContractVersion = PipelineContractVersions.Current,
            request.ProviderId, request.ModelId, request.Role, request.BaseEndpoint, request.Temperature,
            request.MaxOutputTokens, request.RequireJsonObject, request.ThinkingLevel,
            request.DeepSeekThinkingMode,
            request.TransportRetryCount, requiredTestClassName,
            tokenBudgetEstimatorVersion = allocation.EstimatorVersion,
            estimatedInputTokens = allocation.EstimatedInputTokens,
            tokenSafetyReserveTokens = allocation.SafetyReserveTokens,
            responseJsonSchemaHash = Sha256(request.ResponseJsonSchema),
            request.RequestHash, contextHash, treatmentHash
        }, token);
    }
    private static async Task PersistResponseAsync(string root, LlmCallResult result, CancellationToken token)
    {
        await File.WriteAllTextAsync(Path.Combine(root, "assistant-content.raw.txt"), result.ResponseText ?? "", token);
        await File.WriteAllTextAsync(Path.Combine(root, "provider-response.raw.json"), result.RawResponseText ?? "", token);
    }
    private static async Task<(string PlannerSystem, string PlannerUser, string GeneratorSystem, string GeneratorUser)>
        LoadTemplatesAsync(string root, CancellationToken token)
    {
        var prompts = Path.Combine(root, "prompts");
        return (await File.ReadAllTextAsync(Path.Combine(prompts, "planner-system.md"), token),
            await File.ReadAllTextAsync(Path.Combine(prompts, "planner-user.md"), token),
            await File.ReadAllTextAsync(Path.Combine(prompts, "generator-system.md"), token),
            await File.ReadAllTextAsync(Path.Combine(prompts, "generator-user.md"), token));
    }
    private static async Task<List<T>> ReadLinesAsync<T>(string path, CancellationToken token)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Required frozen artifact missing.", path);
        var result = new List<T>();
        foreach (var line in await File.ReadAllLinesAsync(path, token))
            if (!string.IsNullOrWhiteSpace(line)) result.Add(JsonSerializer.Deserialize<T>(line, JsonSupport.Options)!);
        return result;
    }
    private static async Task WriteLinesAsync<T>(string path, IEnumerable<T> rows, CancellationToken token) =>
        await File.WriteAllTextAsync(path,
            string.Join('\n', rows.Select(row => JsonSerializer.Serialize(row, CompactJson))) + "\n", token);
}
