using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rq1Experiment.Contracts.Config;
using Rq1Experiment.Contracts.Generation;
using Rq1Experiment.Contracts.Prompts;
using Rq1Experiment.Core.Abstractions;
using Rq1Experiment.Core.Llm;
using Rq1Experiment.Infrastructure.Persistence;

namespace Rq1Experiment.Infrastructure.Generation;

public sealed class DirectBaselineService(
    ILlmClientFactory clientFactory,
    IFailoverLlmClient failoverClient) : IDirectBaselineService
{
    private static readonly JsonSerializerOptions CompactJson = new(JsonSupport.Options) { WriteIndented = false };

    public async Task<DirectBaselineReport> RunAsync(
        DirectBaselineRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var experimentRoot = Path.GetFullPath(request.ExperimentOutputRoot);
        var contextRoot = Path.Combine(experimentRoot, "context");
        var promptRoot = Path.Combine(experimentRoot, "prompts");
        var systemPath = Path.Combine(promptRoot, "direct-system.md");
        var userPath = Path.Combine(promptRoot, "direct-user.md");
        if (!File.Exists(systemPath) || !File.Exists(userPath))
            throw new FileNotFoundException("Frozen Phase 5 DIRECT prompt templates are required.");
        var split = request.Mode.Equals("Official", StringComparison.OrdinalIgnoreCase) ? "official"
            : request.Mode.Equals("Smoke", StringComparison.OrdinalIgnoreCase) ? "smoke" : "pilot";
        var contextPath = Path.Combine(contextRoot, $"contexts-{split}.jsonl");
        if (!File.Exists(contextPath)) throw new FileNotFoundException("Frozen Phase 5 context is missing.", contextPath);
        var contexts = await ReadJsonLinesAsync<RepositoryContext>(contextPath, cancellationToken);
        contexts = contexts.OrderBy(item => item.SubjectId, StringComparer.Ordinal).ToList();
        var repetitions = request.Mode.Equals("Smoke", StringComparison.OrdinalIgnoreCase) ? 1 : request.Repetitions;
        if (contexts.Count == 0 || repetitions <= 0) throw new InvalidOperationException("DIRECT schedule is empty.");

        var systemTemplate = await File.ReadAllTextAsync(systemPath, cancellationToken);
        var userTemplate = await File.ReadAllTextAsync(userPath, cancellationToken);
        if (userTemplate.Contains("STRUCTURAL_TARGET", StringComparison.OrdinalIgnoreCase)
            || userTemplate.Contains("SCENARIO_PLAN", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("DIRECT prompt template contains forbidden treatment-specific evidence.");

        var records = new ConcurrentBag<DirectRunRecord>();
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
            var runId = $"{context.SubjectId}-DIRECT-R{repetition:00}";
            var runRoot = Path.Combine(experimentRoot, "runs", request.ExperimentId,
                context.SubjectId, "DIRECT", $"R{repetition:00}");
            var recordPath = Path.Combine(runRoot, "run.json");
            var existing = await JsonSupport.ReadAsync<DirectRunRecord>(recordPath, cancellationToken);
            EnsureCompatibleCheckpoint(existing, runId);
            if (IsTerminal(existing))
            {
                records.Add(existing!);
                progress?.Report($"[{currentSequence}/{expected}] Reusing immutable DIRECT artifact {runId}: {existing!.Status}.");
                continue;
            }

            progress?.Report($"[{currentSequence}/{expected}] DIRECT generation for {context.SubjectId}, repetition {repetition}...");
            Directory.CreateDirectory(Path.Combine(runRoot, "input"));
            Directory.CreateDirectory(Path.Combine(runRoot, "generation"));
            Directory.CreateDirectory(Path.Combine(runRoot, "logs"));
            var contextJson = JsonSerializer.Serialize(context, JsonSupport.Options);
            var requiredTestClassName = GeneratedTestIdentity.For(context);
            var renderedUser = userTemplate.Replace("{{COMMON_CONTEXT_JSON}}", contextJson, StringComparison.Ordinal)
                               .Replace("{{REQUIRED_TEST_CLASS_FQN}}", requiredTestClassName, StringComparison.Ordinal)
                               + $"\nHARD_LIMIT: Generate no more than {request.MaxTestMethods} @Test methods.\n";
            var allocation = ConservativeTokenBudget.Allocate(
                request.TotalTokenCeilingPerRun,
                systemTemplate + renderedUser,
                request.MaxOutputTokensPerCall);
            var maxOutputTokens = allocation.MaxOutputTokens;
            var started = DateTimeOffset.UtcNow;
            await File.WriteAllTextAsync(Path.Combine(runRoot, "input", "system-prompt.md"), systemTemplate, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(runRoot, "input", "user-prompt.md"), renderedUser, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(runRoot, "input", "common-context.json"), contextJson, cancellationToken);

            if (maxOutputTokens < 256)
            {
                var budgetFailure = FailureRecord(request, context, repetition, runId, started,
                    "INPUT_TOKEN_BUDGET_ESTIMATE_EXCEEDED", systemTemplate, userTemplate);
                await JsonSupport.WriteAsync(recordPath, budgetFailure, cancellationToken);
                records.Add(budgetFailure);
                continue;
            }

            var client = clientFactory.Create(request.ProviderId);
            var llmRequest = LlmRequestFactory.Configure(
                client.CreateRequest(request.RequestedModelId, "DIRECT_GENERATOR", systemTemplate, renderedUser),
                request.BaseEndpoint, request.Temperature, maxOutputTokens,
                transportRetryCount: request.TransportRetryCount,
                deepSeekThinkingMode: request.DeepSeekThinkingMode);
            await JsonSupport.WriteAsync(Path.Combine(runRoot, "input", "request.json"), new
            {
                pipelineContractVersion = PipelineContractVersions.Current,
                llmRequest.ProviderId,
                llmRequest.ModelId,
                llmRequest.Role,
                llmRequest.BaseEndpoint,
                llmRequest.Temperature,
                llmRequest.MaxOutputTokens,
                llmRequest.RequireJsonObject,
                llmRequest.ThinkingLevel,
                llmRequest.DeepSeekThinkingMode,
                llmRequest.TransportRetryCount,
                requiredTestClassName,
                tokenBudgetEstimatorVersion = allocation.EstimatorVersion,
                estimatedInputTokens = allocation.EstimatedInputTokens,
                tokenSafetyReserveTokens = allocation.SafetyReserveTokens,
                responseJsonSchemaHash = Sha256(llmRequest.ResponseJsonSchema),
                llmRequest.RequestHash,
                context.ContextHash,
                systemTemplateHash = Sha256(systemTemplate),
                userTemplateHash = Sha256(userTemplate)
            }, cancellationToken);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));
            LlmCallResult result;
            try
            {
                result = await failoverClient.SendAsync(llmRequest, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                result = new LlmCallResult(false, false, llmRequest.RequestHash, null, null,
                    1, 1, [], "REQUEST_TIMEOUT");
            }
            await File.WriteAllTextAsync(Path.Combine(runRoot, "generation", "assistant-content.raw.txt"),
                result.ResponseText ?? string.Empty, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(runRoot, "generation", "provider-response.raw.json"),
                result.RawResponseText ?? string.Empty, cancellationToken);

            DirectOutputParseResult? parsed = null;
            var status = "INVALID";
            var failure = result.ErrorCode;
            if (result.IsSuccess)
            {
                parsed = StructuredLlmOutputParser.ParseDirect(
                    result.ResponseText,
                    request.MaxTestMethods,
                    context.Build.RequiredTestApi,
                    context.FocalMethodSignature,
                    context.FocalMethodOverloadSignatures,
                    context.FocalClass,
                    requiredTestClassName);
                failure = parsed.FailureCode;
                status = parsed.IsValid ? "VALID" : "INVALID";
                if (result.TotalTokens is null)
                {
                    status = "INVALID";
                    failure = "TOKEN_USAGE_MISSING";
                }
                else if (result.TotalTokens > request.TotalTokenCeilingPerRun)
                {
                    status = "INVALID";
                    failure = "TOKEN_CEILING_EXCEEDED";
                }
            }
            else if (result.IsWaitingForApiKey)
            {
                status = "WAITING_FOR_API_KEY";
                failure = "NO_AVAILABLE_API_KEY";
            }
            if (parsed?.Output is not null)
            {
                await JsonSupport.WriteAsync(Path.Combine(runRoot, "generation", "direct-output.json"),
                    parsed.Output, cancellationToken);
                var simpleClassName = parsed.Output.TestClassName.Split('.').Last();
                await File.WriteAllTextAsync(Path.Combine(runRoot, "generation", simpleClassName + ".java"),
                    parsed.Output.SourceCode, cancellationToken);
            }

            var record = new DirectRunRecord
            {
                PipelineContractVersion = PipelineContractVersions.Current,
                RunId = runId, SubjectId = context.SubjectId, Split = context.Split, Repetition = repetition,
                Status = status, FailureCode = failure, ContextHash = context.ContextHash,
                SystemTemplateHash = Sha256(systemTemplate), UserTemplateHash = Sha256(userTemplate),
                RequestHash = llmRequest.RequestHash, ProviderId = request.ProviderId,
                BaseEndpoint = request.BaseEndpoint, RequestedModelId = request.RequestedModelId,
                ResponseModelId = result.ResponseModelId, SemanticLlmCalls = result.SemanticLlmCalls,
                TransportAttempts = result.TransportAttempts, KeySlots = result.KeySlots.ToList(),
                InputTokens = result.InputTokens, OutputTokens = result.OutputTokens, TotalTokens = result.TotalTokens,
                FinishReason = result.FinishReason, HttpStatusCode = result.HttpStatusCode,
                DeclaredTestMethodCount = parsed?.Output?.DeclaredTestMethods.Count ?? 0,
                StartedAt = started, CompletedAt = DateTimeOffset.UtcNow
            };
            await JsonSupport.WriteAsync(recordPath, record, cancellationToken);
            records.Add(record);
            if (status == "WAITING_FOR_API_KEY") break;
        }
        });

        var orderedRecords = records
            .OrderBy(record => record.SubjectId, StringComparer.Ordinal)
            .ThenBy(record => record.Repetition)
            .ToList();

        var report = new DirectBaselineReport
        {
            Mode = request.Mode,
            SelectedSubjectCount = contexts.Count,
            ExpectedRunCount = expected,
            CompletedRunCount = orderedRecords.Count(record => record.Status != "WAITING_FOR_API_KEY"),
            ValidOutputCount = orderedRecords.Count(record => record.Status == "VALID"),
            InvalidOutputCount = orderedRecords.Count(record => record.Status == "INVALID"),
            WaitingForApiKeyCount = orderedRecords.Count(record => record.Status == "WAITING_FOR_API_KEY"),
            SemanticLlmCallCount = orderedRecords.Sum(record => record.SemanticLlmCalls),
            PlannerCallCount = 0,
            RepairCallCount = 0,
            TokenCeilingViolationCount = orderedRecords.Count(record => record.FailureCode == "TOKEN_CEILING_EXCEEDED"),
            NoHiddenRetryInvariant = orderedRecords.All(record => record.SemanticLlmCalls is 0 or 1),
            NoPlannerOrRepairTrace = orderedRecords.All(record => record.Approach == "DIRECT") && orderedRecords.All(record => record.SemanticLlmCalls is 0 or 1),
            Complete = orderedRecords.Count(record => record.Status != "WAITING_FOR_API_KEY") == expected
                       && orderedRecords.All(record => record.Status != "WAITING_FOR_API_KEY"),
            GeneratedAt = DateTimeOffset.UtcNow
        };
        await JsonSupport.WriteAsync(Path.Combine(experimentRoot, "direct-baseline-report.json"), report, cancellationToken);
        await WriteJsonLinesAsync(Path.Combine(experimentRoot, "direct-run-ledger.jsonl"), orderedRecords, cancellationToken);
        progress?.Report($"DIRECT baseline processed {report.CompletedRunCount}/{expected} runs: valid={report.ValidOutputCount}, invalid={report.InvalidOutputCount}.");
        return report;
    }

    private static DirectRunRecord FailureRecord(DirectBaselineRequest request, RepositoryContext context,
        int repetition, string runId, DateTimeOffset started, string code, string system, string user) => new()
    {
        PipelineContractVersion = PipelineContractVersions.Current,
        RunId = runId, SubjectId = context.SubjectId, Split = context.Split, Repetition = repetition,
        Status = "INVALID", FailureCode = code, ContextHash = context.ContextHash,
        SystemTemplateHash = Sha256(system), UserTemplateHash = Sha256(user), ProviderId = request.ProviderId,
        BaseEndpoint = request.BaseEndpoint, RequestedModelId = request.RequestedModelId,
        StartedAt = started, CompletedAt = DateTimeOffset.UtcNow
    };

    private static bool IsTerminal(DirectRunRecord? record) =>
        record is not null
        && record.Status is "VALID" or "INVALID"
        && record.CompletedAt != default;
    private static void EnsureCompatibleCheckpoint(DirectRunRecord? record, string runId)
    {
        if (record is not null
            && !string.Equals(record.PipelineContractVersion,
                PipelineContractVersions.Current, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"PIPELINE_CONTRACT_MISMATCH: {runId} was created by an older generation contract. "
                + "Preserve it and create a new Experiment ID.");
    }
    private static string Sha256(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static async Task<List<T>> ReadJsonLinesAsync<T>(string path, CancellationToken token)
    {
        var result = new List<T>();
        foreach (var line in await File.ReadAllLinesAsync(path, token))
            if (!string.IsNullOrWhiteSpace(line)) result.Add(JsonSerializer.Deserialize<T>(line, JsonSupport.Options)
                ?? throw new InvalidDataException("Invalid JSONL row in " + path));
        return result;
    }
    private static async Task WriteJsonLinesAsync<T>(string path, IEnumerable<T> rows, CancellationToken token)
    {
        var text = string.Join('\n', rows.Select(row => JsonSerializer.Serialize(row, CompactJson))) + "\n";
        await File.WriteAllTextAsync(path, text, token);
    }
}
