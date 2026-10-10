using Rq1Experiment.Contracts.Config;
using Rq1Experiment.Contracts.Experiments;
using Rq1Experiment.Contracts.Generation;
using Rq1Experiment.Contracts.Progress;
using Rq1Experiment.Contracts.Prompts;
using Rq1Experiment.Contracts.Subjects;
using Rq1Experiment.Contracts.Targets;

namespace Rq1Experiment.Core.Abstractions;

public interface IExperimentConfigStore
{
    Task<ExperimentConfig> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(ExperimentConfig config, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ListExperimentIdsAsync(CancellationToken cancellationToken = default);
    Task<ExperimentConfig?> LoadExperimentAsync(string experimentId, CancellationToken cancellationToken = default);
    Task DeleteExperimentAsync(string experimentId, CancellationToken cancellationToken = default);
}

public interface IApiKeyStore
{
    Task<IReadOnlyList<string>> LoadAsync(string providerId, CancellationToken cancellationToken = default);
    Task SaveAsync(string providerId, IEnumerable<string> keys, CancellationToken cancellationToken = default);
}

public interface IModelCatalogService
{
    Task<IReadOnlyList<string>> GetModelsAsync(string providerId, CancellationToken cancellationToken = default);
    Task<ModelCatalogRefreshResult> RefreshAsync(
        string providerId,
        string? baseUrl,
        string apiKey,
        CancellationToken cancellationToken = default);
}

public sealed record ModelCatalogRefreshResult(
    bool IsSuccess,
    IReadOnlyList<string> Models,
    string Message);

public interface IPhaseProgressStore
{
    Task<PhaseProgressDocument> LoadAsync(string experimentId, CancellationToken cancellationToken = default);
    Task SaveAsync(PhaseProgressDocument progress, CancellationToken cancellationToken = default);
}

public interface IExperimentOrchestrator
{
    bool IsRunning { get; }
    bool CanResume { get; }
    Task<PipelineRunResult> RunAsync(
        ExperimentConfig config,
        IProgress<PipelineProgress> progress,
        CancellationToken cancellationToken);
    void PauseAfterCurrentStage();
    Task<PipelineRunResult> ResumeAsync(
        ExperimentConfig config,
        IProgress<PipelineProgress> progress,
        CancellationToken cancellationToken);
    Task<PipelineRunResult> RunFromStageAsync(
        ExperimentConfig config,
        string stageId,
        IProgress<PipelineProgress> progress,
        CancellationToken cancellationToken);
    Task<PipelineRunResult> RetryFailedRunsAsync(
        ExperimentConfig config,
        IProgress<PipelineProgress> progress,
        CancellationToken cancellationToken,
        bool onlyTokenFailures = false);
}

public interface IOfficialConfigLockService
{
    Task<OfficialConfigLockResult> ValidateOrCreateAsync(
        ExperimentConfig config,
        CancellationToken cancellationToken = default);

    Task UpdateLockAsync(
        ExperimentConfig config,
        CancellationToken cancellationToken = default);
}

public sealed record OfficialConfigLockResult(
    bool IsAccepted,
    bool WasCreated,
    string Fingerprint,
    string Message);

public interface IRunArtifactReader
{
    Task<IReadOnlyList<RunArtifactSummary>> ReadAsync(
        string experimentOutputRoot,
        CancellationToken cancellationToken = default);
}

public interface IRepositoryPreflightService
{
    Task<PreflightReport> RunAsync(
        PreflightRequest request,
        IProgress<PreflightProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface ISubjectSelectionService
{
    Task<SubjectSelectionReport> RunAsync(
        SubjectSelectionRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface ISmokeSubjectService
{
    Task<SmokeMethodDiscoveryReport> DiscoverAsync(
        SmokeMethodDiscoveryRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    Task<FocalMethodSubject> FreezeAsync(
        SmokeSubjectFreezeRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IStructuralTargetService
{
    Task<StructuralTargetReport> RunAsync(
        StructuralTargetRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IRepositoryContextPromptService
{
    Task<RepositoryContextPromptReport> RunAsync(
        RepositoryContextPromptRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IDirectBaselineService
{
    Task<DirectBaselineReport> RunAsync(
        DirectBaselineRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IPlannedPipelineService
{
    Task<PlannedPipelineReport> RunAsync(
        PlannedPipelineRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IToolExecutionService
{
    Task<ToolExecutionReport> RunCompileAndTestAsync(
        ToolExecutionRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    Task<ToolExecutionReport> RunAdequacyAsync(
        ToolExecutionRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IMetricsLedgerService
{
    Task<MetricsLedgerReport> RebuildAsync(
        MetricsLedgerRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed record RunArtifactSummary(
    string ExperimentId,
    string Kind,
    string ProviderId,
    string RequestedModelId,
    string ArtifactPath);
