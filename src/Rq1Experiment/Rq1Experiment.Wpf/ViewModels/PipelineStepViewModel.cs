using Rq1Experiment.Contracts.Experiments;

namespace Rq1Experiment.Wpf.ViewModels;

public sealed class PipelineStepViewModel(string id, string name) : ObservableObject
{
    private PipelineStageState _state = PipelineStageState.Pending;
    private string _message = string.Empty;

    public string Id { get; } = id;
    public string Name { get; } = name;
    public PipelineStageState State { get => _state; set => SetProperty(ref _state, value); }
    public string Message { get => _message; set => SetProperty(ref _message, value); }
}
