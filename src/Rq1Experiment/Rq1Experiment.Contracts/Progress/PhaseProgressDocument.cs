namespace Rq1Experiment.Contracts.Progress;

public sealed class PhaseProgressDocument
{
    public string ExperimentId { get; set; } = "rq1-draft";
    public List<PhaseProgressRecord> Phases { get; set; } = [];
}

public sealed class PhaseProgressRecord
{
    public int PhaseId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsCompleted { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string Notes { get; set; } = string.Empty;
    public bool Overridden { get; set; }
}
