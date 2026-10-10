namespace Rq1Experiment.Wpf.ViewModels;

public sealed class RunItemViewModel : ObservableObject
{
    private string _subjectId = "No focal method selected";
    private string _approach = "-";
    private int _repetition = 1;
    private string _focalClass = string.Empty;
    private string _focalMethod = string.Empty;
    private string _stage = "Pending";
    private string _status = "PENDING";

    public string SubjectId { get => _subjectId; set => SetProperty(ref _subjectId, value); }
    public string Approach { get => _approach; set => SetProperty(ref _approach, value); }
    public int Repetition { get => _repetition; set => SetProperty(ref _repetition, value); }
    public string FocalClass { get => _focalClass; set => SetProperty(ref _focalClass, value); }
    public string FocalMethod { get => _focalMethod; set => SetProperty(ref _focalMethod, value); }
    public string Stage { get => _stage; set => SetProperty(ref _stage, value); }
    public string Status { get => _status; set => SetProperty(ref _status, value); }
}

public sealed record ProviderOption(string Id, string DisplayName);
