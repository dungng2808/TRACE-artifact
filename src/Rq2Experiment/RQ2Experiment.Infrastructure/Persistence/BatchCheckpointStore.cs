using System.Text.Json;
using System.Text.Json.Serialization;
using RQ2Experiment.Core.Engine;

namespace RQ2Experiment.Infrastructure.Persistence;

public static class BatchCheckpointStore
{
    public static async Task SavePlanAsync(
        string outputDirectory,
        BatchResumePlan plan,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetFullPath(outputDirectory), "checkpoints");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"batch-{Sanitize(plan.BatchId)}.json");
        var tempPath = path + ".tmp";
        var payload = new
        {
            contractVersion = "RQ2-BATCH-CHECKPOINT-1",
            generatedAtUtc = DateTimeOffset.UtcNow,
            plan.BatchId,
            plan.Fingerprint,
            plan.PlannedRuns,
            plan.RunsToExecute,
            plan.ReusedTerminalRuns,
            plan.RetriedInfrastructureRuns,
            skippedSlots = plan.SkippedSlots.Select(slot => new { slot.TaskId, strategy = slot.Strategy.ToString(), slot.Repetition })
        };
        var options = new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
        await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(payload, options), cancellationToken);
        File.Move(tempPath, path, true);
    }

    private static string Sanitize(string value) => string.Concat(value.Select(character =>
        Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
}
