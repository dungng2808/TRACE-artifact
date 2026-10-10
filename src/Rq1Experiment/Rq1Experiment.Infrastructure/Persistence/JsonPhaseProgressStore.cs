using Rq1Experiment.Contracts.Progress;
using Rq1Experiment.Core.Abstractions;

namespace Rq1Experiment.Infrastructure.Persistence;

public sealed class JsonPhaseProgressStore(WorkspaceLayout layout) : IPhaseProgressStore
{
    private static readonly string[] PhaseNames =
    [
        "Scope and Protocol Freeze",
        "WPF .NET 8 Experiment Scaffold",
        "Environment Preflight",
        "Focal-Method Subject Selection",
        "Independent Structural Target Model",
        "Repository Context and Prompt Contracts",
        "DIRECT Baseline",
        "PLANNED Pipeline",
        "Gated Tool Execution Harness",
        "Metrics, Evidence, and Run Ledger",
        "Pilot Experiment",
        "Official RQ1 Experiment",
        "Statistical Analysis and Paper Results"
    ];

    public async Task<PhaseProgressDocument> LoadAsync(
        string experimentId,
        CancellationToken cancellationToken = default)
    {
        var document = await JsonSupport.ReadAsync<PhaseProgressDocument>(
            layout.PhaseProgressPath,
            cancellationToken);

        if (document is not null && document.ExperimentId == experimentId && document.Phases.Count == PhaseNames.Length)
            return document;

        return new PhaseProgressDocument
        {
            ExperimentId = experimentId,
            Phases = PhaseNames.Select((name, index) => new PhaseProgressRecord
            {
                PhaseId = index,
                Name = name
            }).ToList()
        };
    }

    public Task SaveAsync(
        PhaseProgressDocument progress,
        CancellationToken cancellationToken = default) =>
        JsonSupport.WriteAsync(layout.PhaseProgressPath, progress, cancellationToken);
}
