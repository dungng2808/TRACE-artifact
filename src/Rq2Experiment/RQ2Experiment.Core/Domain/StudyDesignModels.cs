namespace RQ2Experiment.Core.Domain;

public sealed record PowerPlanSpecification(
    double FamilyWiseAlpha = 0.05,
    double TargetPower = 0.80,
    double AlternativeWinProbability = 0.70,
    double ExpectedDiscordantUnitRate = 0.40,
    int ComparisonCount = 2,
    int MinimumProjects = 5,
    int PlannedIndependentUnits = 0,
    bool Provisional = true);

public sealed record OutcomeAnalysisPlanSpecification(
    string ContractVersion = "RQ2-OUTCOME-PLAN-1",
    double SuccessNonInferiorityMargin = 0.05,
    double CoverageNonInferiorityMargin = 0.05,
    double MutationNonInferiorityMargin = 0.05,
    bool Provisional = true);

public sealed record BenchmarkStudyDesign(
    string StudyId = "",
    string SamplingFrame = "",
    string SelectionMethod = "",
    int SelectionSeed = 0,
    IReadOnlyList<string>? InclusionCriteria = null,
    IReadOnlyList<string>? ExclusionCriteria = null,
    PowerPlanSpecification? PowerPlan = null,
    OutcomeAnalysisPlanSpecification? OutcomePlan = null)
{
    public IReadOnlyList<string> Inclusions => InclusionCriteria ?? [];
    public IReadOnlyList<string> Exclusions => ExclusionCriteria ?? [];
}

public sealed record StudyPlanEvidence(
    string StudyId = "",
    string SamplingPlanSha256 = "",
    string PowerPlanSha256 = "",
    int SelectionSeed = 0,
    int PlannedIndependentUnits = 0,
    int RequiredIndependentUnits = 0,
    int RequiredDiscordantUnits = 0,
    int MinimumProjects = 0,
    double FamilyWiseAlpha = 0,
    double PlanningAlpha = 0,
    double TargetPower = 0,
    double AchievedPower = 0,
    double AlternativeWinProbability = 0,
    double ExpectedDiscordantUnitRate = 0,
    bool Provisional = true,
    bool SamplingPlanComplete = false,
    bool PowerPlanDeclared = false,
    string OutcomePlanSha256 = "",
    string OutcomeContractVersion = "",
    double SuccessNonInferiorityMargin = 0,
    double CoverageNonInferiorityMargin = 0,
    double MutationNonInferiorityMargin = 0,
    bool OutcomePlanDeclared = false,
    bool OutcomePlanProvisional = true);
