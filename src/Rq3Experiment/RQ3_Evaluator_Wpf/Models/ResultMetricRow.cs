namespace RQ3Evaluator.Wpf.Models;

public sealed record ResultMetricRow(
    string Verifier,
    int Cases,
    double DecisionAccuracy,
    string DecisionAccuracyDisplay,
    string Wilson95Ci,
    string Bootstrap95Ci,
    double RoutingAccuracy,
    string RoutingAccuracyDisplay,
    double MacroAttributionF1,
    string MacroAttributionF1Display,
    double AbstentionRate,
    string AbstentionRateDisplay,
    double SelectiveAccuracy,
    string SelectiveAccuracyDisplay,
    int ApiOrOutputErrors,
    int ApiCalls,
    int InputTokens,
    int OutputTokens,
    double CostUsd,
    string CostUsdDisplay
);

public sealed record StabilityRecordRow(
    int Repetition,
    string CaseId,
    string Verifier,
    string ReferenceDecision,
    string RepeatedDecision,
    string ReferenceCause,
    string RepeatedCause,
    string ReferenceRoute,
    string RepeatedRoute,
    bool TupleAgreement,
    string ExecutionStatus,
    double CostUsd
);

public sealed record PredictionDisplayRow(
    string CaseId,
    string Project,
    string Origin,
    string Verifier,
    string Decision,
    string Cause,
    string Route,
    string ExecutionStatus,
    double CostUsd,
    string? GoldCategory = null,
    string? GoldDecision = null,
    bool? DecisionCorrect = null,
    bool? CauseCorrect = null,
    bool? RouteCorrect = null
);
