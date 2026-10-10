namespace RQ3Evaluator.Wpf.Models;

public enum PipelineStage
{
    NotStarted,
    ValidatingEvidence,
    EvidenceValidated,
    RunningPredictions,
    PredictionsCompleted,
    PredictionsFrozen,
    Scoring,
    ScoringCompleted,
    RunningStability,
    StabilityCompleted,
    PipelineCompleted,
    Failed,
    Cancelled
}
