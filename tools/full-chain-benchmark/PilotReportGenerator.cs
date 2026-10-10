using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using FullChain.Contracts;

namespace FullChain.Benchmark;

public sealed record PilotReports(
    string CompletenessReportPath,
    string ExclusionsReportPath,
    string EnvironmentReportPath,
    string EvidenceReportPath,
    string CostReportPath
);

public static class PilotReportGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<PilotReports> GenerateReportsAsync(
        string reportsDirectory,
        string campaignId,
        PairedCampaignResult campaignResult,
        FullChainConfiguration config,
        ToolLock toolLock,
        IReadOnlyList<TargetDescriptor> manifestTargets,
        IReadOnlyList<int> repetitions)
    {
        Directory.CreateDirectory(reportsDirectory);

        var completenessPath = Path.Combine(reportsDirectory, "pilot-completeness.json");
        var exclusionsPath = Path.Combine(reportsDirectory, "pilot-exclusions.json");
        var environmentPath = Path.Combine(reportsDirectory, "pilot-environment.json");
        var evidencePath = Path.Combine(reportsDirectory, "pilot-evidence.json");
        var costPath = Path.Combine(reportsDirectory, "pilot-cost.json");

        int totalExpectedPairs = manifestTargets.Count * repetitions.Count;
        int completedPairs = campaignResult.PairedRows.Count;
        double completionRate = totalExpectedPairs > 0 ? (double)completedPairs / totalExpectedPairs : 0.0;

        // 1. Completeness Report
        var completenessReport = new
        {
            schemaVersion = "pilot-completeness-v1",
            campaignId = campaignId,
            timestamp = DateTime.UtcNow.ToString("o"),
            totalTargets = manifestTargets.Count,
            repetitions = repetitions,
            totalExpectedPairs = totalExpectedPairs,
            completedPairs = completedPairs,
            completionRate = completionRate,
            isHeadlineAnalysisEligible = campaignResult.ReadinessReport.IsEligible,
            missingEvidenceCount = campaignResult.ReadinessReport.MissingEvidenceCount,
            pairedResults = campaignResult.PairedRows
        };
        await File.WriteAllTextAsync(completenessPath, JsonSerializer.Serialize(completenessReport, JsonOptions));

        // 2. Exclusions Report
        var exclusionsReport = new
        {
            schemaVersion = "pilot-exclusions-v1",
            campaignId = campaignId,
            timestamp = DateTime.UtcNow.ToString("o"),
            totalTargetsEvaluated = manifestTargets.Count,
            disqualifications = campaignResult.ReadinessReport.Disqualifications,
            exclusionsCount = campaignResult.ReadinessReport.Disqualifications.Count,
            exclusionPolicy = "Zero deletions or zero-filling permitted. Unresolved items block headline analysis."
        };
        await File.WriteAllTextAsync(exclusionsPath, JsonSerializer.Serialize(exclusionsReport, JsonOptions));

        // 3. Environment Report
        var environmentReport = new
        {
            schemaVersion = "pilot-environment-v1",
            campaignId = campaignId,
            timestamp = DateTime.UtcNow.ToString("o"),
            operatingSystem = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.OSArchitecture.ToString(),
            dotnetVersion = Environment.Version.ToString(),
            toolLock = toolLock.Environment,
            llmModel = config.Llm.ModelId,
            llmEndpoint = config.Llm.Endpoint,
            budget = config.Budget
        };
        await File.WriteAllTextAsync(environmentPath, JsonSerializer.Serialize(environmentReport, JsonOptions));

        // 4. Evidence Report
        var evidenceReport = new
        {
            schemaVersion = "pilot-evidence-v1",
            campaignId = campaignId,
            timestamp = DateTime.UtcNow.ToString("o"),
            rawRecordsPath = campaignResult.RawRecordsPath,
            pairedCsvPath = campaignResult.PairedCsvPath,
            availabilityLedgerPath = campaignResult.AvailabilityLedgerPath,
            totalRawRecords = campaignResult.Records.Count,
            totalPairedRows = campaignResult.PairedRows.Count,
            records = campaignResult.Records.Select(r => new
            {
                runId = r.RunId,
                subjectId = r.SubjectId,
                repetitionId = r.RepetitionId,
                treatment = r.Treatment,
                terminalStatus = r.TerminalStatus,
                terminalReason = r.TerminalReason,
                workspace = r.WorkspaceDirectory
            })
        };
        await File.WriteAllTextAsync(evidencePath, JsonSerializer.Serialize(evidenceReport, JsonOptions));

        // 5. Cost Report
        long totalTokens = campaignResult.Records.Sum(r => (long)r.TotalTokensUsed);
        int totalRequests = campaignResult.Records.Sum(r => r.TotalProviderRequests);
        double totalSeconds = campaignResult.Records.Sum(r => r.TotalWallClockSeconds);

        // OpenRouter / Gemini 2.5 Flash typical cost: $0.075 / 1M prompt tokens, $0.30 / 1M completion tokens
        // Conservative blended rate: $0.20 per 1M tokens
        double estimatedCostUsd = (totalTokens / 1_000_000.0) * 0.20;

        var costReport = new
        {
            schemaVersion = "pilot-cost-v1",
            campaignId = campaignId,
            timestamp = DateTime.UtcNow.ToString("o"),
            totalWallClockSeconds = totalSeconds,
            totalProviderRequests = totalRequests,
            totalTokensUsed = totalTokens,
            fullChain = new
            {
                treatment = "FullChain",
                tokens = campaignResult.Records.Where(r => r.Treatment == "FullChain").Sum(r => (long)r.TotalTokensUsed),
                requests = campaignResult.Records.Where(r => r.Treatment == "FullChain").Sum(r => r.TotalProviderRequests),
                seconds = campaignResult.Records.Where(r => r.Treatment == "FullChain").Sum(r => r.TotalWallClockSeconds)
            },
            evoSuite = new
            {
                treatment = "EvoSuite",
                tokens = 0,
                requests = 0,
                seconds = campaignResult.Records.Where(r => r.Treatment == "EvoSuite").Sum(r => r.TotalWallClockSeconds)
            },
            estimatedCostUsd = estimatedCostUsd
        };
        await File.WriteAllTextAsync(costPath, JsonSerializer.Serialize(costReport, JsonOptions));

        return new PilotReports(
            completenessPath,
            exclusionsPath,
            environmentPath,
            evidencePath,
            costPath
        );
    }
}

