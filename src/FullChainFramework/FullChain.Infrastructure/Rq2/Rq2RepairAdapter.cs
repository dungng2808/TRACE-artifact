using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FullChain.Contracts;
using FullChain.Core;
using FullChain.Core.Planning;
using FullChain.Infrastructure.Java;
using FullChain.Infrastructure.LLM;
using FullChain.Infrastructure.Processes;
using RQ2Experiment.Core.Abstractions;
using RQ2Experiment.Core.Domain;
using RQ2Experiment.Infrastructure.LLM;
using RQ2Experiment.Infrastructure.OpenAI;
using RQ2Experiment.Infrastructure.Real;

namespace FullChain.Infrastructure.Rq2;

public sealed record RepairResult(
    bool Success,
    string RepairedTestSource,
    int ChangedLines,
    int TouchedMethods,
    string Summary,
    string PromptAuditPath,
    string BeforeSourcePath,
    string AfterSourcePath,
    int PromptTokens,
    int CompletionTokens,
    int RoundNumber,
    string StopReason,
    bool SourceCheckoutUnchanged
);

public sealed class Rq2RepairAdapter : ITestRepairExecutor
{
    private static readonly SemaphoreSlim ProcessEnvironmentMutationLock = new(1, 1);

    private readonly ResolvedToolchain _toolchain;
    private readonly LlmConfiguration _llmConfig;
    private readonly IApiKeyResolver _apiKeyResolver;
    private readonly string? _resolvedApiKey;

    public Rq2RepairAdapter(
        ResolvedToolchain toolchain,
        LlmConfiguration llmConfig,
        string? resolvedApiKey = null,
        IApiKeyResolver? apiKeyResolver = null)
    {
        _toolchain = toolchain ?? throw new ArgumentNullException(nameof(toolchain));
        _llmConfig = llmConfig ?? throw new ArgumentNullException(nameof(llmConfig));
        _resolvedApiKey = resolvedApiKey;
        _apiKeyResolver = apiKeyResolver ?? new ApiKeyResolver();
    }

    public async Task<RepairResult> ExecuteTgslrRepairAsync(
        TargetDescriptor target,
        string workspacePath,
        string testRelativePath,
        string currentTestSource,
        string failingTestMethod,
        string scenarioId,
        string scenarioSpecification,
        string traceabilityLink,
        string failureEvidence,
        int roundNumber,
        string repairOutputDir,
        IGlobalBudgetGovernor budgetGovernor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(workspacePath);
        ArgumentNullException.ThrowIfNull(currentTestSource);
        ArgumentNullException.ThrowIfNull(failingTestMethod);

        await ProcessEnvironmentMutationLock.WaitAsync(cancellationToken);
        try
        {
            using var environmentScope = ScopedToolchainEnvironment.Apply(_toolchain);
            return await ExecuteTgslrRepairCoreAsync(
                target,
                workspacePath,
                testRelativePath,
                currentTestSource,
                failingTestMethod,
                scenarioId,
                scenarioSpecification,
                traceabilityLink,
                failureEvidence,
                roundNumber,
                repairOutputDir,
                budgetGovernor,
                cancellationToken);
        }
        finally
        {
            ProcessEnvironmentMutationLock.Release();
        }
    }

    private async Task<RepairResult> ExecuteTgslrRepairCoreAsync(
        TargetDescriptor target,
        string workspacePath,
        string testRelativePath,
        string currentTestSource,
        string failingTestMethod,
        string scenarioId,
        string scenarioSpecification,
        string traceabilityLink,
        string failureEvidence,
        int roundNumber,
        string repairOutputDir,
        IGlobalBudgetGovernor budgetGovernor,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(repairOutputDir);

        var testClassName = Path.GetFileNameWithoutExtension(testRelativePath);

        // Create RQ2 ExperimentTask
        var task = ExperimentTaskFactory.CreateTask(
            target: target,
            workspacePath: workspacePath,
            testRelativePath: testRelativePath,
            testClassName: testClassName,
            failingTestMethod: failingTestMethod,
            scenarioId: scenarioId,
            scenarioSpecification: scenarioSpecification,
            traceabilityLink: traceabilityLink,
            failureEvidence: failureEvidence,
            javaHome: _toolchain.JavaHome
        );

        // Record production source file hash before repair to ensure it is never modified
        var focalSourcePath = Path.Combine(workspacePath, task.FocalSources[0]);
        var focalSourceHashBefore = File.Exists(focalSourcePath) ? ComputeSha256(focalSourcePath) : "";

        if (string.Equals(_llmConfig.Provider, "agy", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("RQ2 repair currently requires an OpenAI-compatible HTTP provider; provider 'agy' is not supported.");
        }

        var apiKey = _resolvedApiKey ?? _apiKeyResolver.Resolve(_llmConfig);
        ProviderCredentialValidator.EnsureUsable(_llmConfig, apiKey);

        var baseUrl = _llmConfig.Endpoint;
        if (baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = baseUrl[..^"chat/completions".Length];
        }

        using var llmClient = new CompatibleLlmClient(
            apiKey: apiKey,
            baseUrl: baseUrl,
            protocol: LlmApiProtocol.ChatCompletions,
            requiresApiKey: true,
            useJsonMode: true
        );

        var executor = new RealRepairExecutor(llmClient, repairOutputDir);

        var expConfig = new ExperimentConfiguration(
            ModelName: _llmConfig.ModelId,
            MaxIterations: 1,
            TokenBudget: 64000,
            Repetitions: 1,
            Strategies: [RepairStrategy.TraceabilityGuidedScenarioLocal],
            ReasoningEffort: "none",
            ProviderName: ResolveProviderName(_llmConfig),
            ApiProtocol: "ChatCompletions",
            BatchId: "FullChain_Repair",
            ScheduleSeed: 42,
            MaxParallelRuns: 1
        );

        var runId = Guid.NewGuid();
        var artifactsDir = Path.Combine(repairOutputDir, "runs", runId.ToString("N"), "artifacts");
        var auditPath = Path.Combine(artifactsDir, $"iteration-{roundNumber:00}-prompt-audit.json");
        var beforePath = Path.Combine(artifactsDir, $"iteration-{roundNumber:00}-before.java");
        var afterPath = Path.Combine(artifactsDir, $"iteration-{roundNumber:00}-after.java");

        IRepairSession session;
        try
        {
            session = await executor.CreateSessionAsync(
                runId,
                task,
                RepairStrategy.TraceabilityGuidedScenarioLocal,
                1,
                expConfig,
                cancellationToken
            );
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new RepairResult(
                Success: false,
                RepairedTestSource: currentTestSource,
                ChangedLines: 0,
                TouchedMethods: 0,
                Summary: $"Failed to create repair session: {ex.Message}",
                PromptAuditPath: auditPath,
                BeforeSourcePath: beforePath,
                AfterSourcePath: afterPath,
                PromptTokens: 0,
                CompletionTokens: 0,
                RoundNumber: roundNumber,
                StopReason: ex.Message,
                SourceCheckoutUnchanged: true
            );
        }

        RepairIterationResult iterationResult;
        try
        {
            iterationResult = await session.ExecuteIterationAsync(roundNumber, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new RepairResult(
                Success: false,
                RepairedTestSource: currentTestSource,
                ChangedLines: 0,
                TouchedMethods: 0,
                Summary: $"Repair iteration failed with exception: {ex.Message}",
                PromptAuditPath: auditPath,
                BeforeSourcePath: beforePath,
                AfterSourcePath: afterPath,
                PromptTokens: 0,
                CompletionTokens: 0,
                RoundNumber: roundNumber,
                StopReason: ex.Message,
                SourceCheckoutUnchanged: true
            );
        }

        // Record budget usage
        budgetGovernor.RecordUsage("Repair", requestsUsed: 1, tokensUsed: iterationResult.Tokens.TotalTokens, elapsedSeconds: 0);

        var repairedSource = currentTestSource;
        if (File.Exists(afterPath))
        {
            repairedSource = await File.ReadAllTextAsync(afterPath, cancellationToken);
        }

        // Verify production source code in workspace was untouched
        var focalSourceHashAfter = File.Exists(focalSourcePath) ? ComputeSha256(focalSourcePath) : "";
        var productionSourceUnchanged = (focalSourceHashBefore == focalSourceHashAfter);

        var compileSucceeded = iterationResult.Stages.Compilation.Status == RepairStageStatus.Succeeded;
        var focalPassed = iterationResult.Verification.FocalTestPassed;
        var success = compileSucceeded && focalPassed;

        return new RepairResult(
            Success: success,
            RepairedTestSource: repairedSource,
            ChangedLines: iterationResult.Verification.ChangedLines,
            TouchedMethods: iterationResult.Verification.TouchedMethods,
            Summary: iterationResult.Summary,
            PromptAuditPath: auditPath,
            BeforeSourcePath: beforePath,
            AfterSourcePath: afterPath,
            PromptTokens: iterationResult.Tokens.InputTokens,
            CompletionTokens: iterationResult.Tokens.OutputTokens,
            RoundNumber: roundNumber,
            StopReason: success ? "Focal test passed" : "Focal test or compilation failed",
            SourceCheckoutUnchanged: productionSourceUnchanged
        );
    }

    public async Task<RepairArtifact?> AttemptRepairAsync(
        TargetDescriptor target,
        string workspacePath,
        string currentTestSource,
        FailureEvidence failure,
        string? finalizedPlanJson,
        int roundNumber,
        IGlobalBudgetGovernor budget,
        CancellationToken ct = default)
    {
        var failingMethod = failure.FailingTestMethodName;
        if (string.IsNullOrWhiteSpace(failingMethod))
        {
            failingMethod = "testTargetMethod";
        }

        var scenarioId = failure.AssociatedScenarioId ?? "S1";
        var (scenarioSpec, traceLink) = BuildRepairContext(target, finalizedPlanJson, scenarioId);
        var failureDiag = failure.DiagnosticMessage;

        var pkgMatch = Regex.Match(currentTestSource, @"^\s*package\s+([a-zA-Z0-9_.]+)\s*;", RegexOptions.Multiline);
        var testPackage = pkgMatch.Success 
            ? pkgMatch.Groups[1].Value.Trim() 
            : (target.FocalClass.Contains('.') ? target.FocalClass[..target.FocalClass.LastIndexOf('.')] : "");

        var classMatch = Regex.Match(currentTestSource, @"public\s+class\s+([a-zA-Z0-9_]+)", RegexOptions.Multiline);
        var testSimpleName = classMatch.Success 
            ? classMatch.Groups[1].Value.Trim() 
            : target.FocalClass.Split('.').Last().Split('$').Last() + "GeneratedTest";

        var packageDir = testPackage.Replace('.', Path.DirectorySeparatorChar);
        var testRelativePath = Path.Combine("src", "test", "java", packageDir, $"{testSimpleName}.java");

        var fullTestPath = Path.Combine(workspacePath, testRelativePath);
        if (!File.Exists(fullTestPath))
        {
            var testJavaRoot = Path.Combine(workspacePath, "src", "test", "java");
            if (Directory.Exists(testJavaRoot))
            {
                var candidate = Directory.GetFiles(testJavaRoot, $"{testSimpleName}.java", SearchOption.AllDirectories).FirstOrDefault()
                             ?? Directory.GetFiles(testJavaRoot, "*GeneratedTest.java", SearchOption.AllDirectories).FirstOrDefault();
                if (candidate != null)
                {
                    testRelativePath = Path.GetRelativePath(workspacePath, candidate);
                    fullTestPath = candidate;
                }
            }
        }

        // Guarantee that the test file exists on disk in workspacePath before repair begins
        if (!File.Exists(fullTestPath))
        {
            var dir = Path.GetDirectoryName(fullTestPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(fullTestPath, currentTestSource);
        }

        var repairOutputDir = Path.Combine(workspacePath, "evidence", $"repair_round_{roundNumber}");

        var repairResult = await ExecuteTgslrRepairAsync(
            target,
            workspacePath,
            testRelativePath,
            currentTestSource,
            failingMethod,
            scenarioId,
            scenarioSpec,
            traceLink,
            failureDiag,
            roundNumber,
            repairOutputDir,
            budget,
            ct);

        if (!repairResult.Success || string.IsNullOrWhiteSpace(repairResult.RepairedTestSource))
        {
            return null;
        }

        var beforeSource = File.Exists(repairResult.BeforeSourcePath) ? await File.ReadAllTextAsync(repairResult.BeforeSourcePath, ct) : "";
        var afterSource = File.Exists(repairResult.AfterSourcePath) ? await File.ReadAllTextAsync(repairResult.AfterSourcePath, ct) : "";

        using var sha = SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(repairResult.RepairedTestSource);
        var hash = Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();

        return new RepairArtifact(
            RoundNumber: roundNumber,
            Strategy: "TGSLR",
            TargetMethodName: failingMethod,
            BeforeMethodSource: beforeSource,
            AfterMethodSource: afterSource,
            FullRepairedSource: repairResult.RepairedTestSource,
            SourceSha256: hash,
            PromptTokens: repairResult.PromptTokens,
            CompletionTokens: repairResult.CompletionTokens,
            BuildPassed: true,
            StopReason: repairResult.StopReason
        );
    }

    private static string ComputeSha256(string filePath)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private static (string ScenarioSpecification, string TraceabilityLink) BuildRepairContext(
        TargetDescriptor target,
        string? finalizedPlanJson,
        string scenarioId)
    {
        if (string.IsNullOrWhiteSpace(finalizedPlanJson))
        {
            return (
                $"Scenario for {target.FocalMethodSignature}",
                $"Traceability link for {target.SubjectId}");
        }

        try
        {
            var plan = ScenarioPlanValidator.Validate(finalizedPlanJson, target.SubjectId, maxScenarios: 3);
            var scenario = plan.Scenarios.FirstOrDefault(item =>
                string.Equals(item.ScenarioId, scenarioId, StringComparison.Ordinal));
            if (scenario == null)
            {
                throw new FormatException($"Finalized plan does not contain repair scenario '{scenarioId}'.");
            }

            var specification = new StringBuilder()
                .AppendLine($"FINALIZED SCENARIO {scenario.ScenarioId}; do not change its input, ownership, or expected observation.")
                .AppendLine($"Technique: {scenario.Technique}")
                .AppendLine("Required preconditions:");
            foreach (var precondition in scenario.Preconditions)
            {
                specification.AppendLine($"- {precondition}");
            }
            specification.AppendLine($"Concrete input: {scenario.ConcreteInput}")
                .AppendLine($"Expected observation: {scenario.ExpectedObservation}")
                .AppendLine("Return a materially changed replacement only when it preserves this scenario exactly.");

            var traceability = $"Subject={target.SubjectId}; focal={target.FocalClass}#{target.FocalMethodSignature}; "
                + $"scenario={scenario.ScenarioId}; targetIds={string.Join(",", scenario.TargetIds)}.";
            return (specification.ToString(), traceability);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException or InvalidOperationException)
        {
            throw new FormatException("Cannot construct TGSLR context from the finalized RQ1 plan.", ex);
        }
    }

    private static string ResolveProviderName(LlmConfiguration configuration)
    {
        return configuration.Endpoint.Contains("openrouter", StringComparison.OrdinalIgnoreCase)
            ? "OpenRouter"
            : configuration.Provider;
    }

}
