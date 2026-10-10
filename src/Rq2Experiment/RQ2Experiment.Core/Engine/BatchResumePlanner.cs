using System.Security.Cryptography;
using System.Text;
using RQ2Experiment.Core.Domain;

namespace RQ2Experiment.Core.Engine;

public sealed record RunSlot(string TaskId, RepairStrategy Strategy, int Repetition);

public sealed record BatchResumePlan(
    string BatchId,
    string Fingerprint,
    int PlannedRuns,
    int RunsToExecute,
    int ReusedTerminalRuns,
    int RetriedInfrastructureRuns,
    IReadOnlySet<RunSlot> SkippedSlots)
{
    public string Summary =>
        $"batch={BatchId}; planned={PlannedRuns}; execute={RunsToExecute}; " +
        $"reused-terminal={ReusedTerminalRuns}; retry-infrastructure={RetriedInfrastructureRuns}.";
}

public static class BatchResumePlanner
{
    public static string CreateFingerprint(
        IReadOnlyList<ExperimentTask> tasks,
        ExperimentConfiguration configuration,
        string manifestFingerprint)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(configuration);
        var canonical = string.Join("\n", new[]
        {
            "RQ2-RUN-PLAN-1",
            manifestFingerprint.Trim(),
            configuration.ProviderName.Trim(),
            configuration.ModelName.Trim(),
            configuration.ApiProtocol.Trim(),
            configuration.ReasoningEffort.Trim(),
            configuration.MaxIterations.ToString(System.Globalization.CultureInfo.InvariantCulture),
            configuration.TokenBudget.ToString(System.Globalization.CultureInfo.InvariantCulture),
            configuration.Repetitions.ToString(System.Globalization.CultureInfo.InvariantCulture),
            configuration.ScheduleSeed.ToString(System.Globalization.CultureInfo.InvariantCulture),
            string.Join("|", configuration.Strategies.Order().Select(strategy => ((int)strategy).ToString())),
            string.Join("|", tasks.OrderBy(task => task.TaskId, StringComparer.Ordinal)
                .Select(task => $"{task.TaskId}::{task.ScenarioId}::{task.ArtifactId}::{task.FixedRevision}"))
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static BatchResumePlan Build(
        IReadOnlyList<ExperimentTask> tasks,
        ExperimentConfiguration configuration,
        IReadOnlyList<ExperimentRunResult> existingResults,
        bool retryInfrastructureFailures)
    {
        if (string.IsNullOrWhiteSpace(configuration.BatchId))
            throw new ArgumentException("BatchId is required for resume planning.", nameof(configuration));
        if (string.IsNullOrWhiteSpace(configuration.RunPlanFingerprint))
            throw new ArgumentException("RunPlanFingerprint is required for resume planning.", nameof(configuration));

        var schedule = ExperimentScheduleBuilder.Build(tasks, configuration);
        var matching = existingResults
            .Where(result => result.BatchId.Equals(configuration.BatchId, StringComparison.Ordinal) &&
                             result.RunPlanFingerprint.Equals(configuration.RunPlanFingerprint, StringComparison.Ordinal))
            .GroupBy(ToSlot)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(result => result.CompletedAt).First());

        var skipped = new HashSet<RunSlot>();
        var reusedTerminal = 0;
        var retriedInfrastructure = 0;
        foreach (var item in schedule)
        {
            var slot = new RunSlot(item.Task.TaskId, item.Strategy, item.Repetition);
            if (!matching.TryGetValue(slot, out var prior)) continue;
            if (ExperimentOutcomePolicy.IsInfrastructureFailure(prior) && retryInfrastructureFailures)
            {
                retriedInfrastructure++;
                continue;
            }

            // A repair outcome is an observation. Do not silently replace it on resume.
            skipped.Add(slot);
            reusedTerminal++;
        }

        return new BatchResumePlan(
            configuration.BatchId,
            configuration.RunPlanFingerprint,
            schedule.Count,
            schedule.Count - skipped.Count,
            reusedTerminal,
            retriedInfrastructure,
            skipped);
    }

    public static RunSlot ToSlot(ExperimentRunResult result) =>
        new(result.TaskId, result.Strategy, result.Repetition);
}
