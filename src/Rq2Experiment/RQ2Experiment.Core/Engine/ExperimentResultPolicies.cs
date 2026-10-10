using RQ2Experiment.Core.Domain;

namespace RQ2Experiment.Core.Engine;

public static class ExperimentResultBatchSelector
{
    public static IReadOnlyList<ExperimentRunResult> SelectLatest(
        IReadOnlyList<ExperimentRunResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var latestBatch = results
            .Where(result => !string.IsNullOrWhiteSpace(result.BatchId))
            .GroupBy(result => result.BatchId, StringComparer.Ordinal)
            .OrderByDescending(group => group.Max(result => result.CompletedAt))
            .FirstOrDefault();
        if (latestBatch is null) return results;

        // Retry records share a slot with an earlier infrastructure failure. Analysis must use
        // the latest observation for that slot rather than double-counting a retry.
        return latestBatch
            .GroupBy(BatchResumePlanner.ToSlot)
            .Select(group => group.OrderByDescending(result => result.CompletedAt).First())
            .OrderBy(result => result.Design.ScheduleIndex)
            .ThenBy(result => result.StartedAt)
            .ToArray();
    }
}

public static class ExperimentOutcomePolicy
{
    public static bool IsInfrastructureFailure(ExperimentRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.FailureCategory is
            RunFailureCategory.ApiRateLimit or
            RunFailureCategory.ProviderUnavailable or
            RunFailureCategory.AuthenticationFailure or
            RunFailureCategory.NetworkFailure;
    }

    public static bool IsAnalysisEligible(ExperimentRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return !IsInfrastructureFailure(result) &&
               result.Status != RunStatus.Cancelled &&
               result.FailureCategory != RunFailureCategory.TreatmentIsolationViolation &&
               result.FailureCategory != RunFailureCategory.Unexpected;
    }
}
