using System;
using System.Threading;
using System.Threading.Tasks;

namespace FullChain.Contracts;

public interface IGlobalBudgetGovernor
{
    double TotalElapsedSeconds { get; }
    double HardLimitSeconds { get; }
    int RequestsUsed { get; }
    int MaxRequests { get; }
    int TokensUsed { get; }
    int MaxTokens { get; }
    bool IsExhausted { get; }

    void CheckBudget(string nextStage, int estimatedTokens = 0);
    void RecordUsage(string stage, int requestsUsed, int tokensUsed, double elapsedSeconds);
    TimeSpan GetRemainingStageTimeout(string stage, TimeSpan stageBudget);
}

public interface IWorkspaceManager
{
    Task<string> PrepareIsolatedWorkspaceAsync(
        string projectPath,
        string campaignId,
        string targetId,
        int repetitionId,
        string treatment,
        string stage,
        CancellationToken ct = default);

    Task CleanupWorkspaceAsync(string workspacePath, CancellationToken ct = default);
}

public interface ITestGenerator
{
    Task<GeneratedTestArtifact> GenerateTestsAsync(
        TargetDescriptor target,
        string contextJson,
        IGlobalBudgetGovernor budget,
        CancellationToken ct = default);
}

public sealed record CandidateExecutionResult(
    bool CompileSuccess,
    bool ExecutionSuccess,
    string BuildLogSnippet,
    FailureEvidence? Failure
);

public interface ICandidateTestRunner
{
    Task<CandidateExecutionResult> CompileAndExecuteCandidateAsync(
        TargetDescriptor target,
        string workspacePath,
        string testClassFqn,
        string testSourceCode,
        IGlobalBudgetGovernor budget,
        CancellationToken ct = default);
}

public interface ITestRepairExecutor

{
    Task<RepairArtifact?> AttemptRepairAsync(
        TargetDescriptor target,
        string workspacePath,
        string currentTestSource,
        FailureEvidence failure,
        string? finalizedPlanJson,
        int roundNumber,
        IGlobalBudgetGovernor budget,
        CancellationToken ct = default);
}

public interface ICommonEvaluator
{
    Task<CommonEnvelope> EvaluateSuiteAsync(
        TargetDescriptor target,
        string workspacePath,
        string testClassFqn,
        string testSourceCode,
        IGlobalBudgetGovernor budget,
        CancellationToken ct = default);
}

public interface IVerifierAdapter
{
    Task<VerificationDecision> VerifyBlindedEnvelopeAsync(
        VerificationRequest request,
        IGlobalBudgetGovernor budget,
        CancellationToken ct = default);
}

public interface ICheckpointStore
{
    Task SaveCheckpointAsync(CampaignCheckpoint checkpoint, CancellationToken ct = default);
    Task<CampaignCheckpoint?> LoadCheckpointAsync(string campaignId, string targetId, int repetitionId, string treatment, CancellationToken ct = default);
}

public interface IArtifactIndexWriter
{
    Task RecordArtifactAsync(
        string campaignId,
        string targetId,
        int repetitionId,
        string treatment,
        string stage,
        string artifactType,
        string filePath,
        CancellationToken ct = default);

    Task<ArtifactIndex> GetIndexAsync(
        string campaignId,
        string targetId,
        int repetitionId,
        string treatment,
        CancellationToken ct = default);

    Task SaveIndexAsync(
        string campaignId,
        string targetId,
        int repetitionId,
        string treatment,
        string outputPath,
        CancellationToken ct = default);
}
