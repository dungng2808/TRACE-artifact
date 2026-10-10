namespace RQ2Experiment.Wpf.ViewModels;

public sealed record StageFunnelStatisticsViewModel(
    string Strategy,
    int Runs,
    int LlmOutputPassed,
    int SourceApplicationPassed,
    int CompilationPassed,
    int FocalTestPassed,
    int BuggyOraclePassed,
    int RegressionPassed,
    int CoveragePassed,
    int MutationPassed,
    int InferredRuns)
{
    public string LlmDisplay => Display(LlmOutputPassed);
    public string SourceDisplay => Display(SourceApplicationPassed);
    public string CompilationDisplay => Display(CompilationPassed);
    public string FocalDisplay => Display(FocalTestPassed);
    public string BuggyOracleDisplay => Display(BuggyOraclePassed);
    public string RegressionDisplay => Display(RegressionPassed);
    public string CoverageDisplay => Display(CoveragePassed);
    public string MutationDisplay => Display(MutationPassed);

    private string Display(int count) => $"{count}/{Runs}";
}
