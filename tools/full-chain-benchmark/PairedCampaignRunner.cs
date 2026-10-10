using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using FullChain.Contracts;
using FullChain.Core;
using FullChain.Core.Orchestration;

namespace FullChain.Benchmark;

public sealed record PairedCampaignResult(
    IReadOnlyList<FullChainRecord> Records,
    IReadOnlyList<PairedResultRow> PairedRows,
    ReadinessReport ReadinessReport,
    string RawRecordsPath,
    string PairedCsvPath,
    string AvailabilityLedgerPath
);

public sealed class PairedCampaignRunner
{
    private readonly FullChainOrchestrator _fullChainOrchestrator;
    private readonly EvoSuiteAdapter _evoSuiteAdapter;

    public event Action<string>? LogEmitted;

    public PairedCampaignRunner(
        FullChainOrchestrator fullChainOrchestrator,
        EvoSuiteAdapter evoSuiteAdapter)
    {
        _fullChainOrchestrator = fullChainOrchestrator ?? throw new ArgumentNullException(nameof(fullChainOrchestrator));
        _evoSuiteAdapter = evoSuiteAdapter ?? throw new ArgumentNullException(nameof(evoSuiteAdapter));
    }

    public async Task<PairedCampaignResult> RunPairedCampaignAsync(
        IReadOnlyList<TargetDescriptor> targets,
        IReadOnlyList<int> repetitions,
        FullChainConfiguration config,
        ToolLock toolLock,
        string campaignId,
        string outputDirectory,
        int evoSuiteSearchTimeoutSeconds = 420,
        bool resumeIfPossible = true,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(repetitions);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(toolLock);

        Directory.CreateDirectory(outputDirectory);
        var allRecords = new List<FullChainRecord>();
        var targetToProjectMap = targets.ToDictionary(t => t.SubjectId, t => t.ProjectId);

        var rawJsonlPath = Path.Combine(outputDirectory, "raw-records.jsonl");
        var pairedCsvPath = Path.Combine(outputDirectory, "paired-comparison.csv");
        var ledgerPath = Path.Combine(outputDirectory, "availability-ledger.json");

        // Load existing records if resuming
        if (File.Exists(rawJsonlPath))
        {
            try
            {
                var lines = await File.ReadAllLinesAsync(rawJsonlPath, ct);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                foreach (var line in lines)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        var rec = JsonSerializer.Deserialize<FullChainRecord>(line, options);
                        if (rec != null) allRecords.Add(rec);
                    }
                }
                Log($"[Campaign Resume] Loaded {allRecords.Count} prior records from {rawJsonlPath}.");
            }
            catch (Exception ex)
            {
                Log($"[Campaign Resume] Warning: Failed to parse existing records: {ex.Message}");
            }
        }

        int totalExpectedPairs = targets.Count * repetitions.Count;
        int pairIndex = 0;

        Log($"[Campaign] Starting paired campaign '{campaignId}' with {targets.Count} targets across {repetitions.Count} repetitions ({totalExpectedPairs} total pairs).");

        foreach (var target in targets)
        {
            foreach (var rep in repetitions)
            {
                pairIndex++;
                ct.ThrowIfCancellationRequested();

                // Check if pair was already completed in prior run
                var hasFc = allRecords.Any(r => r.SubjectId == target.SubjectId && r.RepetitionId == rep && r.Treatment == "FullChain");
                var hasEvo = allRecords.Any(r => r.SubjectId == target.SubjectId && r.RepetitionId == rep && r.Treatment == "EvoSuite");

                if (hasFc && hasEvo)
                {
                    Log($"[Campaign ({pairIndex}/{totalExpectedPairs})] Skipping {target.SubjectId} (Rep: {rep}) - already completed.");
                    continue;
                }

                Log($"\n===============================================================================");
                Log($"[Target: {target.SubjectId} (Rep: {rep}) ({pairIndex}/{totalExpectedPairs})] Running Treatment 1: FullChain...");
                Log($"===============================================================================");

                if (!hasFc)
                {
                    var fcBudget = new GlobalBudgetGovernor(config.Budget);
                    var fcRequest = new OrchestratorRunRequest(
                        Configuration: config,
                        Target: target,
                        RepetitionId: rep,
                        ToolLock: toolLock,
                        CampaignId: campaignId,
                        Treatment: "FullChain",
                        ResumeIfPossible: resumeIfPossible
                    );

                    var fcResult = await _fullChainOrchestrator.RunAsync(fcRequest, fcBudget, ct);
                    allRecords.Add(fcResult.Record);
                    Log($"[FullChain Result] Status: {fcResult.TerminalStatus}, Reason: {fcResult.TerminalReason}, BC: {fcResult.Record.Diagnostic.BranchCoverage:P1}");
                }
                else
                {
                    Log($"[FullChain] Already completed for {target.SubjectId} (Rep: {rep}).");
                }

                Log($"\n===============================================================================");
                Log($"[Target: {target.SubjectId} (Rep: {rep}) ({pairIndex}/{totalExpectedPairs})] Running Treatment 2: EvoSuite Baseline...");
                Log($"===============================================================================");

                if (!hasEvo)
                {
                    var evoBudget = new GlobalBudgetGovernor(config.Budget);
                    var evoRecord = await _evoSuiteAdapter.RunEvoSuiteTargetAsync(
                        target: target,
                        repetitionId: rep,
                        config: config,
                        toolLock: toolLock,
                        campaignId: campaignId,
                        budget: evoBudget,
                        searchTimeoutSeconds: evoSuiteSearchTimeoutSeconds,
                        ct: ct);

                    allRecords.Add(evoRecord);
                    Log($"[EvoSuite Result] Status: {evoRecord.TerminalStatus}, Reason: {evoRecord.TerminalReason}, BC: {evoRecord.Diagnostic.BranchCoverage:P1}");
                }
                else
                {
                    Log($"[EvoSuite] Already completed for {target.SubjectId} (Rep: {rep}).");
                }

                // Incremental export after every single pair
                await PairedResultWriter.WriteRawRecordsJsonlAsync(rawJsonlPath, allRecords, ct);
                await PairedResultWriter.WritePairedComparisonCsvAsync(pairedCsvPath, allRecords, targetToProjectMap, ct);
                var currentReadiness = ReadinessGate.EvaluateCampaign(targets, repetitions, allRecords);
                await PairedResultWriter.WriteAvailabilityLedgerAsync(ledgerPath, campaignId, allRecords, currentReadiness, ct);
            }
        }

        // Final Export & summary
        await PairedResultWriter.WriteRawRecordsJsonlAsync(rawJsonlPath, allRecords, ct);
        await PairedResultWriter.WritePairedComparisonCsvAsync(pairedCsvPath, allRecords, targetToProjectMap, ct);
        var readiness = ReadinessGate.EvaluateCampaign(targets, repetitions, allRecords);
        await PairedResultWriter.WriteAvailabilityLedgerAsync(ledgerPath, campaignId, allRecords, readiness, ct);

        var pairedRows = PairedResultWriter.BuildPairedRows(allRecords, targetToProjectMap);

        Log($"\n[Campaign Complete] Raw records: {rawJsonlPath}");
        Log($"[Campaign Complete] Paired CSV: {pairedCsvPath}");
        Log($"[Campaign Complete] Availability ledger: {ledgerPath}");
        Log($"[Campaign Complete] Headline eligible: {readiness.IsEligible} (Completed pairs: {readiness.CompletedPairs}/{readiness.TotalExpectedPairs})");

        return new PairedCampaignResult(
            Records: allRecords,
            PairedRows: pairedRows,
            ReadinessReport: readiness,
            RawRecordsPath: rawJsonlPath,
            PairedCsvPath: pairedCsvPath,
            AvailabilityLedgerPath: ledgerPath
        );
    }

    private void Log(string message)
    {
        LogEmitted?.Invoke(message);
        Console.WriteLine(message);
    }
}
