using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FullChain.Contracts;

namespace FullChain.Benchmark;

public sealed record PairedResultRow(
    string TargetId,
    string ProjectId,
    int RepetitionId,
    string FullChainStatus,
    string? FullChainReason,
    double FullChainVSR,
    double FullChainDeliveredCSR,
    double FullChainDeliveredESR,
    double FullChainDeliveredBC,
    double FullChainDeliveredMS,
    double FullChainDeliveredSTS,
    double FullChainCandidateBC,
    double FullChainCandidateMS,
    double FullChainCandidateSTS,
    int FullChainRepairRounds,
    int FullChainTokens,
    double FullChainSeconds,
    string EvoSuiteStatus,
    string? EvoSuiteReason,
    double EvoSuiteVSR,
    double EvoSuiteDeliveredCSR,
    double EvoSuiteDeliveredESR,
    double EvoSuiteDeliveredBC,
    double EvoSuiteDeliveredMS,
    double EvoSuiteDeliveredSTS,
    double EvoSuiteCandidateBC,
    double EvoSuiteCandidateMS,
    double EvoSuiteCandidateSTS,
    int EvoSuiteTokens,
    double EvoSuiteSeconds
);

public static class PairedResultWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // JSON Lines requires each record to occupy exactly one physical line.
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task WriteRawRecordsJsonlAsync(
        string outputPath,
        IEnumerable<FullChainRecord> records,
        CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        await using var writer = new StreamWriter(outputPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        foreach (var r in records)
        {
            var line = JsonSerializer.Serialize(r, JsonOptions);
            await writer.WriteLineAsync(line.AsMemory(), ct);
        }
    }

    public static async Task WritePairedComparisonCsvAsync(
        string outputPath,
        IReadOnlyList<FullChainRecord> allRecords,
        IReadOnlyDictionary<string, string> targetToProjectMap,
        CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var rows = BuildPairedRows(allRecords, targetToProjectMap);

        var sb = new StringBuilder();
        // Header with distinct dual-accounting views
        sb.AppendLine("TargetId,ProjectId,RepetitionId," +
                      "FC_Status,FC_Reason,FC_Delivered_VSR,FC_Delivered_CSR,FC_Delivered_ESR,FC_Delivered_BC,FC_Delivered_MS,FC_Delivered_STS," +
                      "FC_Diag_BC,FC_Diag_MS,FC_Diag_STS,FC_RepairRounds,FC_Tokens,FC_Seconds," +
                      "Evo_Status,Evo_Reason,Evo_Delivered_VSR,Evo_Delivered_CSR,Evo_Delivered_ESR,Evo_Delivered_BC,Evo_Delivered_MS,Evo_Delivered_STS," +
                      "Evo_Diag_BC,Evo_Diag_MS,Evo_Diag_STS,Evo_Tokens,Evo_Seconds");

        foreach (var row in rows)
        {
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0},{1},{2}," +
                "\"{3}\",\"{4}\",{5:F4},{6:F4},{7:F4},{8:F4},{9:F4},{10:F4}," +
                "{11:F4},{12:F4},{13:F4},{14},{15},{16:F2}," +
                "\"{17}\",\"{18}\",{19:F4},{20:F4},{21:F4},{22:F4},{23:F4},{24:F4}," +
                "{25:F4},{26:F4},{27:F4},{28},{29:F2}",
                row.TargetId, row.ProjectId, row.RepetitionId,
                row.FullChainStatus, row.FullChainReason ?? "",
                row.FullChainVSR, row.FullChainDeliveredCSR, row.FullChainDeliveredESR, row.FullChainDeliveredBC, row.FullChainDeliveredMS, row.FullChainDeliveredSTS,
                row.FullChainCandidateBC, row.FullChainCandidateMS, row.FullChainCandidateSTS, row.FullChainRepairRounds, row.FullChainTokens, row.FullChainSeconds,
                row.EvoSuiteStatus, row.EvoSuiteReason ?? "",
                row.EvoSuiteVSR, row.EvoSuiteDeliveredCSR, row.EvoSuiteDeliveredESR, row.EvoSuiteDeliveredBC, row.EvoSuiteDeliveredMS, row.EvoSuiteDeliveredSTS,
                row.EvoSuiteCandidateBC, row.EvoSuiteCandidateMS, row.EvoSuiteCandidateSTS, row.EvoSuiteTokens, row.EvoSuiteSeconds
            ));
        }

        await File.WriteAllTextAsync(outputPath, sb.ToString(), Encoding.UTF8, ct);
    }

    public static async Task WriteAvailabilityLedgerAsync(
        string outputPath,
        string campaignId,
        IReadOnlyList<FullChainRecord> allRecords,
        ReadinessReport readiness,
        CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var ledger = new
        {
            schemaVersion = "full-chain-availability-ledger-v1",
            campaignId = campaignId,
            timestamp = DateTime.UtcNow.ToString("o"),
            totalRecords = allRecords.Count,
            totalExpectedPairs = readiness.TotalExpectedPairs,
            completedPairs = readiness.CompletedPairs,
            missingEvidenceCount = readiness.MissingEvidenceCount,
            isHeadlineAnalysisEligible = readiness.IsEligible,
            disqualifications = readiness.Disqualifications,
            warnings = readiness.Warnings,
            statusBreakdown = allRecords.GroupBy(r => r.TerminalStatus).ToDictionary(g => g.Key, g => g.Count())
        };

        var json = JsonSerializer.Serialize(ledger, JsonOptions);
        await File.WriteAllTextAsync(outputPath, json, Encoding.UTF8, ct);
    }

    public static IReadOnlyList<PairedResultRow> BuildPairedRows(
        IReadOnlyList<FullChainRecord> allRecords,
        IReadOnlyDictionary<string, string> targetToProjectMap)
    {
        var rows = new List<PairedResultRow>();
        var grouped = allRecords.GroupBy(r => (r.SubjectId, r.RepetitionId));

        foreach (var group in grouped)
        {
            var targetId = group.Key.SubjectId;
            var repId = group.Key.RepetitionId;
            var projectId = targetToProjectMap.TryGetValue(targetId, out var p) ? p : "unknown";

            var fc = group.FirstOrDefault(r => string.Equals(r.Treatment, "FullChain", StringComparison.OrdinalIgnoreCase));
            var evo = group.FirstOrDefault(r => string.Equals(r.Treatment, "EvoSuite", StringComparison.OrdinalIgnoreCase));

            rows.Add(new PairedResultRow(
                TargetId: targetId,
                ProjectId: projectId,
                RepetitionId: repId,
                FullChainStatus: fc?.TerminalStatus ?? "MISSING",
                FullChainReason: fc?.TerminalReason,
                FullChainVSR: fc?.Outcome.VerifiedSuiteRate ?? 0.0,
                FullChainDeliveredCSR: fc?.Outcome.CompilationSatisfactionRate ?? 0.0,
                FullChainDeliveredESR: fc?.Outcome.ExecutionSatisfactionRate ?? 0.0,
                FullChainDeliveredBC: fc?.Outcome.BranchCoverage ?? 0.0,
                FullChainDeliveredMS: fc?.Outcome.MutationScore ?? 0.0,
                FullChainDeliveredSTS: fc?.Outcome.StructuralTargetSatisfaction ?? 0.0,
                FullChainCandidateBC: fc?.Diagnostic.BranchCoverage ?? 0.0,
                FullChainCandidateMS: fc?.Diagnostic.MutationScore ?? 0.0,
                FullChainCandidateSTS: fc?.Diagnostic.StructuralTargetSatisfaction ?? 0.0,
                FullChainRepairRounds: fc?.TotalRepairRounds ?? 0,
                FullChainTokens: fc?.TotalTokensUsed ?? 0,
                FullChainSeconds: fc?.TotalWallClockSeconds ?? 0.0,
                EvoSuiteStatus: evo?.TerminalStatus ?? "MISSING",
                EvoSuiteReason: evo?.TerminalReason,
                EvoSuiteVSR: evo?.Outcome.VerifiedSuiteRate ?? 0.0,
                EvoSuiteDeliveredCSR: evo?.Outcome.CompilationSatisfactionRate ?? 0.0,
                EvoSuiteDeliveredESR: evo?.Outcome.ExecutionSatisfactionRate ?? 0.0,
                EvoSuiteDeliveredBC: evo?.Outcome.BranchCoverage ?? 0.0,
                EvoSuiteDeliveredMS: evo?.Outcome.MutationScore ?? 0.0,
                EvoSuiteDeliveredSTS: evo?.Outcome.StructuralTargetSatisfaction ?? 0.0,
                EvoSuiteCandidateBC: evo?.Diagnostic.BranchCoverage ?? 0.0,
                EvoSuiteCandidateMS: evo?.Diagnostic.MutationScore ?? 0.0,
                EvoSuiteCandidateSTS: evo?.Diagnostic.StructuralTargetSatisfaction ?? 0.0,
                EvoSuiteTokens: evo?.TotalTokensUsed ?? 0,
                EvoSuiteSeconds: evo?.TotalWallClockSeconds ?? 0.0
            ));
        }

        return rows;
    }
}
