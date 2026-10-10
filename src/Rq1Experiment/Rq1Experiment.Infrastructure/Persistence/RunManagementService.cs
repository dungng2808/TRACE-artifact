using System.Text.Json;
using Rq1Experiment.Core.Abstractions;

namespace Rq1Experiment.Infrastructure.Persistence;

public sealed class RunManagementService : IRunManagementService
{
    private static readonly HashSet<string> KnownTokenFailureCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "TOKEN_CEILING_EXCEEDED",
        "TOTAL_TOKEN_BUDGET_ESTIMATE_EXCEEDED",
        "INPUT_TOKEN_BUDGET_ESTIMATE_EXCEEDED",
        "TOKEN_USAGE_MISSING"
    };

    public Task<IReadOnlyList<FailedRunSummary>> GetFailedRunsAsync(
        string experimentOutputRoot,
        string experimentId,
        CancellationToken cancellationToken = default)
    {
        var runsRoot = Path.Combine(experimentOutputRoot, "runs", experimentId);
        if (!Directory.Exists(runsRoot))
            return Task.FromResult<IReadOnlyList<FailedRunSummary>>([]);

        var failedRuns = new List<FailedRunSummary>();
        foreach (var runJsonPath in Directory.EnumerateFiles(runsRoot, "run.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var content = File.ReadAllText(runJsonPath);
                using var document = JsonDocument.Parse(content);
                var root = document.RootElement;

                var subjectId = JsonString(root, "subjectId");
                var approach = JsonString(root, "approach");
                var repetition = JsonInt(root, "repetition") ?? 1;
                var status = JsonString(root, "status");
                var failureCode = JsonString(root, "failureCode");
                var failureStage = JsonString(root, "failureStage");
                var runDirectory = Path.GetDirectoryName(runJsonPath)!;

                if (!status.Equals("VALID", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(failureCode))
                {
                    failedRuns.Add(new FailedRunSummary(
                        subjectId, approach, repetition, status,
                        string.IsNullOrWhiteSpace(failureCode) ? null : failureCode,
                        string.IsNullOrWhiteSpace(failureStage) ? null : failureStage,
                        runDirectory));
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Unreadable run file is considered a failed run needing reset
                var runDirectory = Path.GetDirectoryName(runJsonPath)!;
                failedRuns.Add(new FailedRunSummary(
                    "UNKNOWN", "UNKNOWN", 1, "INVALID", "UNREADABLE_METADATA", null, runDirectory));
            }
        }

        return Task.FromResult<IReadOnlyList<FailedRunSummary>>(
            failedRuns.OrderBy(r => r.SubjectId, StringComparer.Ordinal)
                .ThenBy(r => r.Approach, StringComparer.Ordinal)
                .ThenBy(r => r.Repetition)
                .ToList());
    }

    public Task<ResetRunsReport> ResetFailedRunsAsync(
        ResetRunsRequest request,
        CancellationToken cancellationToken = default)
    {
        var runsRoot = Path.Combine(request.ExperimentOutputRoot, "runs", request.ExperimentId);
        if (!Directory.Exists(runsRoot))
        {
            return Task.FromResult(new ResetRunsReport(
                request.ExperimentId, 0, 0, 0, []));
        }

        var totalScanned = 0;
        var resetRunIds = new List<string>();
        var retainedCount = 0;

        var targetSubjectSet = request.SubjectIds is not null && request.SubjectIds.Count > 0
            ? new HashSet<string>(request.SubjectIds, StringComparer.OrdinalIgnoreCase)
            : null;
        var targetApproachSet = request.Approaches is not null && request.Approaches.Count > 0
            ? new HashSet<string>(request.Approaches, StringComparer.OrdinalIgnoreCase)
            : null;

        foreach (var runJsonPath in Directory.EnumerateFiles(runsRoot, "run.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            totalScanned++;

            var runDirectory = Path.GetDirectoryName(runJsonPath)!;
            var shouldReset = false;
            var runId = Path.GetFileName(runDirectory);

            try
            {
                var content = File.ReadAllText(runJsonPath);
                using var document = JsonDocument.Parse(content);
                var root = document.RootElement;

                var subjectId = JsonString(root, "subjectId");
                var approach = JsonString(root, "approach");
                var repetition = JsonInt(root, "repetition") ?? 1;
                var status = JsonString(root, "status");
                var failureCode = JsonString(root, "failureCode");
                runId = $"{subjectId}-{approach}-R{repetition:00}";

                if (targetSubjectSet is not null && !targetSubjectSet.Contains(subjectId))
                {
                    retainedCount++;
                    continue;
                }

                if (targetApproachSet is not null && !targetApproachSet.Contains(approach))
                {
                    retainedCount++;
                    continue;
                }

                var isFailed = !status.Equals("VALID", StringComparison.OrdinalIgnoreCase)
                               || !string.IsNullOrWhiteSpace(failureCode);

                if (isFailed)
                {
                    if (request.OnlyTokenFailures)
                    {
                        shouldReset = !string.IsNullOrWhiteSpace(failureCode)
                                      && KnownTokenFailureCodes.Contains(failureCode);
                    }
                    else
                    {
                        shouldReset = true;
                    }
                }
            }
            catch (Exception)
            {
                // Corrupted or unreadable run is reset
                shouldReset = true;
            }

            if (shouldReset)
            {
                CleanRunDirectory(runDirectory);
                resetRunIds.Add(runId);
            }
            else
            {
                retainedCount++;
            }
        }

        return Task.FromResult(new ResetRunsReport(
            request.ExperimentId,
            totalScanned,
            resetRunIds.Count,
            retainedCount,
            resetRunIds));
    }

    public Task<bool> ResetSpecificRunAsync(
        string runDirectory,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(runDirectory)) return Task.FromResult(false);
        CleanRunDirectory(runDirectory);
        return Task.FromResult(true);
    }

    private static void CleanRunDirectory(string runDirectory)
    {
        try
        {
            var filesToDelete = new[]
            {
                "run.json",
                "metrics.json"
            };

            foreach (var file in filesToDelete)
            {
                var filePath = Path.Combine(runDirectory, file);
                if (File.Exists(filePath))
                    File.Delete(filePath);
            }

            var subdirsToDelete = new[]
            {
                "generation",
                "generator",
                "planner",
                "tools",
                "logs"
            };

            foreach (var subdir in subdirsToDelete)
            {
                var dirPath = Path.Combine(runDirectory, subdir);
                if (Directory.Exists(dirPath))
                    Directory.Delete(dirPath, recursive: true);
            }
        }
        catch (Exception)
        {
            // Best effort file deletion
        }
    }

    private static string JsonString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int? JsonInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? number
            : null;
}
