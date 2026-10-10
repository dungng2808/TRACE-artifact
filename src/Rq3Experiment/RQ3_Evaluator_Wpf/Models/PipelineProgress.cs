namespace RQ3Evaluator.Wpf.Models;

public sealed record PipelineProgress(
    PipelineStage Stage,
    string StageDescription,
    string? CurrentCaseId,
    int CompletedCases,
    int TotalCases,
    int CompletedPredictions,
    int TotalExpectedPredictions,
    int ApiCalls,
    int InputTokens,
    int OutputTokens,
    double CostUsd,
    int ErrorCount,
    double ElapsedSeconds,
    string StatusMessage
);
