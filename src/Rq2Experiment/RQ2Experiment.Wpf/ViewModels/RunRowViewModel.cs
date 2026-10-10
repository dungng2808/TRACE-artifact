using RQ2Experiment.Core.Domain;

namespace RQ2Experiment.Wpf.ViewModels;

public sealed class RunRowViewModel : ObservableObject
{
    private string status = string.Empty;
    private int iteration;
    private int totalTokens;
    private int changedLines;
    private int regressedTests;
    private string message = string.Empty;

    public required Guid RunId { get; init; }
    public required string TaskId { get; init; }
    public required string ScenarioId { get; init; }
    public required string Strategy { get; init; }
    public required int Repetition { get; init; }

    public string Status { get => status; private set => SetProperty(ref status, value); }
    public int Iteration { get => iteration; private set => SetProperty(ref iteration, value); }
    public int TotalTokens { get => totalTokens; private set => SetProperty(ref totalTokens, value); }
    public int ChangedLines { get => changedLines; private set => SetProperty(ref changedLines, value); }
    public int RegressedTests { get => regressedTests; private set => SetProperty(ref regressedTests, value); }
    public string Message { get => message; private set => SetProperty(ref message, value); }

    public void Update(ExperimentProgress progress)
    {
        Status = progress.Status.ToString();
        Iteration = progress.Iteration;
        TotalTokens = progress.TotalTokens;
        ChangedLines = progress.ChangedLines;
        RegressedTests = progress.RegressedTests;
        Message = progress.Message;
    }
}
