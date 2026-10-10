using Rq1Experiment.Contracts.Progress;

namespace Rq1Experiment.Wpf.ViewModels;

public sealed class PhaseProgressItemViewModel : ObservableObject
{
    private bool _isCompleted;
    private DateTimeOffset? _completedAt;
    private string _notes = string.Empty;
    private bool _overridden;

    public required int PhaseId { get; init; }
    public required string Name { get; init; }
    public string DisplayName => $"Phase {PhaseId} — {Name}";
    public bool IsCompleted
    {
        get => _isCompleted;
        set
        {
            if (!SetProperty(ref _isCompleted, value)) return;
            CompletedAt = value ? DateTimeOffset.UtcNow : null;
            Overridden = true;
        }
    }
    public DateTimeOffset? CompletedAt { get => _completedAt; set => SetProperty(ref _completedAt, value); }
    public string Notes { get => _notes; set => SetProperty(ref _notes, value); }
    public bool Overridden { get => _overridden; set => SetProperty(ref _overridden, value); }

    public void MarkCompletedFromEvidence(string notes)
    {
        _isCompleted = true;
        _completedAt = DateTimeOffset.UtcNow;
        _notes = notes;
        _overridden = false;
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(CompletedAt));
        OnPropertyChanged(nameof(Notes));
        OnPropertyChanged(nameof(Overridden));
    }

    public static PhaseProgressItemViewModel FromRecord(PhaseProgressRecord record) => new()
    {
        PhaseId = record.PhaseId,
        Name = record.Name,
        _isCompleted = record.IsCompleted,
        _completedAt = record.CompletedAt,
        _notes = record.Notes,
        _overridden = record.Overridden
    };

    public PhaseProgressRecord ToRecord() => new()
    {
        PhaseId = PhaseId,
        Name = Name,
        IsCompleted = IsCompleted,
        CompletedAt = CompletedAt,
        Notes = Notes,
        Overridden = Overridden
    };
}
