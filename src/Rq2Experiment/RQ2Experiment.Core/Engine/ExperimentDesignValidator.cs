using RQ2Experiment.Core.Domain;

namespace RQ2Experiment.Core.Engine;

public sealed record ExperimentDesignAssessment(
    int TaskCount,
    int IndependentUnitCount,
    int ProjectCount,
    int TreatmentCount,
    int Repetitions,
    int ExpectedRunCount,
    bool IsExactlyCounterbalanced,
    IReadOnlyList<string> Warnings)
{
    public string Summary =>
        $"{TaskCount} task(s), {IndependentUnitCount} independent unit(s), {ProjectCount} project(s), {TreatmentCount} treatment(s), " +
        $"{Repetitions} repetition(s), {ExpectedRunCount} expected run(s); " +
        (IsExactlyCounterbalanced ? "exact counterbalance" : "partial counterbalance");
}

public static class ExperimentDesignValidator
{
    public static ExperimentDesignAssessment Validate(
        IReadOnlyList<ExperimentTask> tasks,
        ExperimentConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.Validate();
        if (tasks.Count == 0)
        {
            throw new ArgumentException("At least one experiment task is required.", nameof(tasks));
        }

        EnsureUnique(tasks, task => task.TaskId, "taskId");
        EnsureUnique(tasks, task => task.ScenarioId, "scenarioId");
        EnsureUnique(tasks, task => task.TestId, "testId");

        foreach (var task in tasks)
        {
            if (string.IsNullOrWhiteSpace(task.ScenarioId) ||
                string.IsNullOrWhiteSpace(task.TestId) ||
                string.IsNullOrWhiteSpace(task.FailingTestMethod))
            {
                throw new InvalidOperationException(
                    $"Task {task.TaskId} requires scenarioId, testId, and failingTestMethod.");
            }

            if (task.RequiresBuggyOracle &&
                Path.GetFullPath(task.RepositoryPath)
                    .Equals(Path.GetFullPath(task.BuggyRepositoryPath), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Task {task.TaskId} points fixed and buggy revisions to the same repository.");
            }
        }

        var schedule = ExperimentScheduleBuilder.Build(tasks, configuration);
        var expectedRuns = checked(tasks.Count * configuration.Repetitions * configuration.Strategies.Count);
        if (schedule.Count != expectedRuns ||
            schedule.Select(item => item.ScheduleIndex).Distinct().Count() != expectedRuns)
        {
            throw new InvalidOperationException(
                $"Schedule is incomplete: expected {expectedRuns} unique run slots, found {schedule.Count}.");
        }

        foreach (var block in schedule.GroupBy(item => item.BlockId, StringComparer.Ordinal))
        {
            if (block.Count() != configuration.Strategies.Count ||
                block.Select(item => item.Strategy).Distinct().Count() != configuration.Strategies.Count ||
                block.Select(item => item.TreatmentPosition).Order().SequenceEqual(
                    Enumerable.Range(1, configuration.Strategies.Count)) is false)
            {
                throw new InvalidOperationException(
                    $"Block {block.Key} must contain every treatment exactly once in unique positions.");
            }
        }

        var exactlyCounterbalanced = true;
        foreach (var task in tasks)
        {
            foreach (var strategy in configuration.Strategies)
            {
                var positionCounts = schedule
                    .Where(item => item.Task.TaskId.Equals(task.TaskId, StringComparison.OrdinalIgnoreCase) &&
                                   item.Strategy == strategy)
                    .GroupBy(item => item.TreatmentPosition)
                    .ToDictionary(group => group.Key, group => group.Count());
                var counts = Enumerable.Range(1, configuration.Strategies.Count)
                    .Select(position => positionCounts.GetValueOrDefault(position))
                    .ToArray();
                if (counts.Max() - counts.Min() > 1)
                {
                    throw new InvalidOperationException(
                        $"Treatment {strategy.ToDisplayName()} is not position-balanced for task {task.TaskId}.");
                }

                exactlyCounterbalanced &= counts.Distinct().Count() == 1;
            }
        }

        var projectKeys = tasks
            .Select(task => string.IsNullOrWhiteSpace(task.ProjectId) ? task.RepositoryPath : task.ProjectId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var independentUnitKeys = tasks
            .Select(task => string.IsNullOrWhiteSpace(task.IndependenceUnitId)
                ? task.TaskId
                : task.IndependenceUnitId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var warnings = new List<string>();
        if (independentUnitKeys.Length < 2)
        {
            warnings.Add("Only one independent unit is configured; multiple tasks or repetitions from the same bug do not increase the inferential sample size.");
        }
        if (projectKeys.Length < 2)
        {
            warnings.Add("Only one project is represented; do not generalize the result across projects.");
        }
        if (!exactlyCounterbalanced)
        {
            warnings.Add(
                $"Repetitions ({configuration.Repetitions}) are not a multiple of treatment count " +
                $"({configuration.Strategies.Count}); position balance is only approximate.");
        }

        var incompleteProvenance = tasks.Count(task =>
            string.IsNullOrWhiteSpace(task.ProjectId) ||
            string.IsNullOrWhiteSpace(task.DatasetId) ||
            string.IsNullOrWhiteSpace(task.ArtifactId) ||
            string.IsNullOrWhiteSpace(task.SourceUri));
        if (incompleteProvenance > 0)
        {
            warnings.Add($"{incompleteProvenance} task(s) lack complete project/dataset/artifact/source provenance.");
        }

        var incompleteSamplingIdentity = tasks.Count(task =>
            string.IsNullOrWhiteSpace(task.IndependenceUnitId) ||
            string.IsNullOrWhiteSpace(task.SamplingStratum));
        if (incompleteSamplingIdentity > 0)
        {
            warnings.Add($"{incompleteSamplingIdentity} task(s) lack independenceUnitId or samplingStratum.");
        }

        return new ExperimentDesignAssessment(
            tasks.Count,
            independentUnitKeys.Length,
            projectKeys.Length,
            configuration.Strategies.Count,
            configuration.Repetitions,
            expectedRuns,
            exactlyCounterbalanced,
            warnings);
    }

    private static void EnsureUnique(
        IReadOnlyList<ExperimentTask> tasks,
        Func<ExperimentTask, string> selector,
        string fieldName)
    {
        var duplicate = tasks
            .Select(selector)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Duplicate {fieldName} '{duplicate}' in benchmark manifest.");
        }
    }
}
