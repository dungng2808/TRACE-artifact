namespace RQ2Experiment.Wpf.ViewModels;

public sealed record TreatmentStatisticsViewModel(
    string Strategy,
    int Runs,
    int Successes,
    double SuccessRate,
    int AnalysisEligibleRuns,
    double AnalysisSuccessRate,
    int InfrastructureFailures,
    double AverageTokens,
    double AverageApiCalls,
    double AverageIterations,
    double AverageChangedLines,
    int TotalRegressions,
    int MeasurementsComplete,
    int CoverageMeasurementsComplete,
    int MutationMeasurementsComplete,
    int TokenAccountedRuns,
    int PromptAuditedRuns,
    int TotalApiRetries,
    int TotalKeyFailovers,
    double? AverageLineCoverage,
    double? AverageMutationScore,
    double TokenBarPercent)
{
    public double SuccessRatePercent => SuccessRate * 100;
    public double AnalysisSuccessRatePercent => AnalysisSuccessRate * 100;
    public double MutationScorePercent => AverageMutationScore.GetValueOrDefault() * 100;
    public string AverageLineCoverageDisplay => AverageLineCoverage.HasValue
        ? $"{AverageLineCoverage.Value:P1}"
        : "N/A";
    public string AverageMutationScoreDisplay => AverageMutationScore.HasValue
        ? $"{AverageMutationScore.Value:P1}"
        : "N/A";
    public string MutationScoreDisplay => AverageMutationScore.HasValue
        ? $"{AverageMutationScore.Value:P0}"
        : "N/A";
}
