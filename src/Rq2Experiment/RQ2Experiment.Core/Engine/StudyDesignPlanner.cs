using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RQ2Experiment.Core.Domain;

namespace RQ2Experiment.Core.Engine;

public sealed record PowerPlanResult(
    int RequiredDiscordantUnits,
    int RequiredIndependentUnits,
    double PlanningAlpha,
    double AchievedPower);

public sealed record StudyPlanAssessment(
    StudyPlanEvidence Evidence,
    int SelectedTasks,
    int SelectedIndependentUnits,
    int SelectedProjects,
    IReadOnlyList<string> Warnings)
{
    public string Summary =>
        $"study={Evidence.StudyId}; selected={SelectedTasks} task(s)/{SelectedIndependentUnits} independent unit(s)/" +
        $"{SelectedProjects} project(s); required={Evidence.RequiredIndependentUnits} independent unit(s) " +
        $"({Evidence.RequiredDiscordantUnits} expected discordant); planned={Evidence.PlannedIndependentUnits}";
}

public static class StudyDesignPlanner
{
    public static void EnsureConfirmatoryReady(
        IReadOnlyList<ExperimentTask> tasks,
        StudyPlanEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(evidence);
        var independentUnits = tasks
            .Select(task => string.IsNullOrWhiteSpace(task.IndependenceUnitId) ? task.TaskId : task.IndependenceUnitId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var projects = tasks
            .Select(task => string.IsNullOrWhiteSpace(task.ProjectId) ? task.RepositoryPath : task.ProjectId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        var issues = new List<string>();
        if (!evidence.OutcomePlanDeclared || evidence.OutcomePlanProvisional ||
            evidence.OutcomeContractVersion != "RQ2-OUTCOME-PLAN-1")
            issues.Add("outcome plan must be declared, non-provisional, and use RQ2-OUTCOME-PLAN-1");
        if (evidence.Provisional)
            issues.Add("power/sampling plan is still provisional");
        if (evidence.RequiredIndependentUnits > 0 && independentUnits < evidence.RequiredIndependentUnits)
            issues.Add($"independent units {independentUnits}/{evidence.RequiredIndependentUnits}");
        if (evidence.MinimumProjects > 0 && projects < evidence.MinimumProjects)
            issues.Add($"projects {projects}/{evidence.MinimumProjects}");

        if (issues.Count > 0)
            throw new InvalidOperationException(
                "Confirmatory batch blocked: " + string.Join("; ", issues) + ". Run as pilot or freeze a new protocol before execution.");
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static StudyPlanAssessment Assess(
        IReadOnlyList<ExperimentTask> tasks,
        BenchmarkStudyDesign studyDesign)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(studyDesign);

        var warnings = new List<string>();
        var independentUnits = tasks
            .Select(task => task.IndependenceUnitId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var projects = tasks
            .Select(task => task.ProjectId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var samplingComplete = !string.IsNullOrWhiteSpace(studyDesign.StudyId) &&
                               !string.IsNullOrWhiteSpace(studyDesign.SamplingFrame) &&
                               !string.IsNullOrWhiteSpace(studyDesign.SelectionMethod) &&
                               studyDesign.SelectionSeed != 0 &&
                               studyDesign.Inclusions.Count > 0 &&
                               studyDesign.Exclusions.Count > 0 &&
                               tasks.All(task =>
                                   !string.IsNullOrWhiteSpace(task.IndependenceUnitId) &&
                                   !string.IsNullOrWhiteSpace(task.SamplingStratum));
        if (!samplingComplete)
        {
            warnings.Add(
                "Sampling plan is incomplete: require studyId, frame, selection method/seed, explicit inclusion/exclusion criteria, independenceUnitId, and samplingStratum.");
        }

        var power = studyDesign.PowerPlan;
        PowerPlanResult? powerResult = null;
        if (power is null)
        {
            warnings.Add("Power plan is missing; repetitions do not substitute for independent sampling units.");
        }
        else
        {
            powerResult = Calculate(power);
            if (power.Provisional)
            {
                warnings.Add(
                    "Power assumptions are provisional; estimate discordance and effect assumptions from a pilot, freeze them, then rerun preflight before the confirmatory batch.");
            }
            if (power.PlannedIndependentUnits < powerResult.RequiredIndependentUnits)
            {
                warnings.Add(
                    $"Planned independent units ({power.PlannedIndependentUnits}) are below the calculated requirement ({powerResult.RequiredIndependentUnits}).");
            }
            if (independentUnits < powerResult.RequiredIndependentUnits)
            {
                warnings.Add(
                    $"Selected manifest contains {independentUnits}/{powerResult.RequiredIndependentUnits} required independent units; this is a pilot subset, not a confirmatory sample.");
            }
            if (projects < power.MinimumProjects)
            {
                warnings.Add($"Selected manifest contains {projects}/{power.MinimumProjects} required projects.");
            }
        }

        var outcomePlan = studyDesign.OutcomePlan;
        if (outcomePlan is null)
        {
            warnings.Add("Outcome analysis plan is missing; metric hierarchy and non-inferiority margins must be frozen before a confirmatory batch.");
        }
        else
        {
            ValidateOutcomePlan(outcomePlan);
            if (outcomePlan.Provisional)
            {
                warnings.Add("Outcome non-inferiority margins are provisional; justify and freeze them before the confirmatory batch.");
            }
        }

        var duplicateArtifacts = tasks
            .Where(task => !string.IsNullOrWhiteSpace(task.ArtifactId))
            .GroupBy(task => task.ArtifactId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateArtifacts.Length > 0)
        {
            warnings.Add($"Duplicate artifactId values in sampling frame: {string.Join(", ", duplicateArtifacts)}.");
        }

        var evidence = new StudyPlanEvidence(
            studyDesign.StudyId,
            Hash(new
            {
                studyDesign.StudyId,
                studyDesign.SamplingFrame,
                studyDesign.SelectionMethod,
                studyDesign.SelectionSeed,
                inclusionCriteria = studyDesign.Inclusions,
                exclusionCriteria = studyDesign.Exclusions,
                units = tasks.Select(task => new
                {
                    task.TaskId,
                    task.ProjectId,
                    task.ArtifactId,
                    task.IndependenceUnitId,
                    task.SamplingStratum
                }).OrderBy(item => item.TaskId, StringComparer.Ordinal).ToArray()
            }),
            power is null ? "" : Hash(power),
            studyDesign.SelectionSeed,
            power?.PlannedIndependentUnits ?? 0,
            powerResult?.RequiredIndependentUnits ?? 0,
            powerResult?.RequiredDiscordantUnits ?? 0,
            power?.MinimumProjects ?? 0,
            power?.FamilyWiseAlpha ?? 0,
            powerResult?.PlanningAlpha ?? 0,
            power?.TargetPower ?? 0,
            powerResult?.AchievedPower ?? 0,
            power?.AlternativeWinProbability ?? 0,
            power?.ExpectedDiscordantUnitRate ?? 0,
            power?.Provisional ?? true,
            samplingComplete,
            powerResult is not null,
            outcomePlan is null ? "" : Hash(outcomePlan),
            outcomePlan?.ContractVersion ?? "",
            outcomePlan?.SuccessNonInferiorityMargin ?? 0,
            outcomePlan?.CoverageNonInferiorityMargin ?? 0,
            outcomePlan?.MutationNonInferiorityMargin ?? 0,
            outcomePlan is not null,
            outcomePlan?.Provisional ?? true);
        return new StudyPlanAssessment(evidence, tasks.Count, independentUnits, projects, warnings);
    }

    public static PowerPlanResult Calculate(PowerPlanSpecification specification)
    {
        ArgumentNullException.ThrowIfNull(specification);
        if (specification.FamilyWiseAlpha is <= 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(specification), "Family-wise alpha must be between 0 and 1.");
        if (specification.TargetPower is <= 0.5 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(specification), "Target power must be between 0.5 and 1.");
        if (specification.AlternativeWinProbability is <= 0.5 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(specification), "Alternative win probability must be between 0.5 and 1.");
        if (specification.ExpectedDiscordantUnitRate is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(specification), "Expected discordant-unit rate must be in (0, 1].");
        if (specification.ComparisonCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(specification), "Comparison count must be positive.");
        if (specification.MinimumProjects < 2)
            throw new ArgumentOutOfRangeException(nameof(specification), "At least two projects are required.");
        if (specification.PlannedIndependentUnits < 0)
            throw new ArgumentOutOfRangeException(nameof(specification), "Planned independent units cannot be negative.");

        var planningAlpha = specification.FamilyWiseAlpha / specification.ComparisonCount;
        for (var discordantUnits = 1; discordantUnits <= 500; discordantUnits++)
        {
            var power = ExactTwoSidedPower(
                discordantUnits,
                planningAlpha,
                specification.AlternativeWinProbability);
            if (power + 1e-12 < specification.TargetPower) continue;

            return new PowerPlanResult(
                discordantUnits,
                (int)Math.Ceiling(discordantUnits / specification.ExpectedDiscordantUnitRate),
                planningAlpha,
                power);
        }

        throw new InvalidOperationException("Power target requires more than 500 discordant units; revise assumptions explicitly.");
    }

    public static double EstimateAchievedPower(
        PowerPlanSpecification specification,
        int independentUnits)
    {
        ArgumentNullException.ThrowIfNull(specification);
        if (independentUnits <= 0) throw new ArgumentOutOfRangeException(nameof(independentUnits));
        var discordantUnits = Math.Max(1, (int)Math.Round(
            independentUnits * specification.ExpectedDiscordantUnitRate,
            MidpointRounding.AwayFromZero));
        return ExactTwoSidedPower(
            discordantUnits,
            specification.FamilyWiseAlpha / specification.ComparisonCount,
            specification.AlternativeWinProbability);
    }

    private static void ValidateOutcomePlan(OutcomeAnalysisPlanSpecification plan)
    {
        if (!plan.ContractVersion.Equals("RQ2-OUTCOME-PLAN-1", StringComparison.Ordinal))
            throw new ArgumentException("Unsupported outcome analysis contract version.", nameof(plan));
        ValidateMargin(plan.SuccessNonInferiorityMargin, nameof(plan.SuccessNonInferiorityMargin));
        ValidateMargin(plan.CoverageNonInferiorityMargin, nameof(plan.CoverageNonInferiorityMargin));
        ValidateMargin(plan.MutationNonInferiorityMargin, nameof(plan.MutationNonInferiorityMargin));
    }

    private static void ValidateMargin(double margin, string name)
    {
        if (margin is <= 0 or >= 0.5)
            throw new ArgumentOutOfRangeException(name, "Non-inferiority margins must be in (0, 0.5).");
    }

    private static double ExactTwoSidedPower(int trials, double alpha, double alternativeProbability)
    {
        var nullDistribution = BinomialDistribution(trials, 0.5);
        var alternativeDistribution = BinomialDistribution(trials, alternativeProbability);
        var power = 0d;
        for (var wins = 0; wins <= trials; wins++)
        {
            var tail = Math.Min(wins, trials - wins);
            var exactP = Math.Min(1, 2 * nullDistribution.Take(tail + 1).Sum());
            if (exactP <= alpha + 1e-12) power += alternativeDistribution[wins];
        }
        return power;
    }

    private static double[] BinomialDistribution(int trials, double probability)
    {
        var values = new double[trials + 1];
        values[0] = Math.Pow(1 - probability, trials);
        var odds = probability / (1 - probability);
        for (var k = 0; k < trials; k++)
        {
            values[k + 1] = values[k] * (trials - k) / (k + 1d) * odds;
        }
        return values;
    }

    private static string Hash(object value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }
}
