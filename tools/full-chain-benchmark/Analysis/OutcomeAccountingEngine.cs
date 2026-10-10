using System;
using System.Collections.Generic;
using System.Linq;
using FullChain.Contracts;

namespace FullChain.Benchmark.Analysis;

public record TableCellOutcome(
    string MetricName,
    int Numerator,
    int Denominator,
    double RateOrMeanPercent,
    string DisplayString
);

public record DualAccountingReport(
    IReadOnlyList<TableCellOutcome> DeliveredSystemItt,
    IReadOnlyList<TableCellOutcome> CandidateStageDiagnostics,
    int TotalRecords,
    int TotalPairs,
    int MissingEvidenceCount,
    bool IsHeadlineEligible
);

public class OutcomeAccountingEngine
{
    public static DualAccountingReport ComputeAccounting(
        string treatment,
        IReadOnlyList<FullChainRecord> records)
    {
        var treatmentRecords = records.Where(r => r.Treatment == treatment).ToList();
        int total = treatmentRecords.Count;
        int missingCount = treatmentRecords.Count(r => r.TerminalStatus == TerminalStatus.MissingEvidence);

        if (total == 0)
        {
            return new DualAccountingReport(
                Array.Empty<TableCellOutcome>(),
                Array.Empty<TableCellOutcome>(),
                0, 0, 0, false
            );
        }

        bool isHeadlineEligible = (missingCount == 0);

        // 1. Delivered-System ITT Outcomes
        var validIttRecords = treatmentRecords.Where(r => r.TerminalStatus != TerminalStatus.MissingEvidence).ToList();
        int validTotal = validIttRecords.Count;

        int vsrNum = validIttRecords.Count(r => r.Outcome.VerifiedSuiteRate > 0.5);
        int csrNum = validIttRecords.Count(r => r.Outcome.CompilationSatisfactionRate > 0.5);
        int esrNum = validIttRecords.Count(r => r.Outcome.ExecutionSatisfactionRate > 0.5);
        double avgSts = validTotal > 0 ? validIttRecords.Average(r => r.Outcome.StructuralTargetSatisfaction) : 0;
        double avgBc = validTotal > 0 ? validIttRecords.Average(r => r.Outcome.BranchCoverage) : 0;
        double avgMs = validTotal > 0 ? validIttRecords.Average(r => r.Outcome.MutationScore) : 0;

        var ittCells = new List<TableCellOutcome>
        {
            new("VSR", vsrNum, validTotal, validTotal > 0 ? (double)vsrNum / validTotal * 100.0 : 0.0, $"{vsrNum}/{validTotal} ({(validTotal > 0 ? (double)vsrNum / validTotal * 100.0 : 0.0):F1}%)"),
            new("CSR", csrNum, validTotal, validTotal > 0 ? (double)csrNum / validTotal * 100.0 : 0.0, $"{csrNum}/{validTotal} ({(validTotal > 0 ? (double)csrNum / validTotal * 100.0 : 0.0):F1}%)"),
            new("ESR", esrNum, validTotal, validTotal > 0 ? (double)esrNum / validTotal * 100.0 : 0.0, $"{esrNum}/{validTotal} ({(validTotal > 0 ? (double)esrNum / validTotal * 100.0 : 0.0):F1}%)"),
            new("STS", -1, validTotal, avgSts, $"{avgSts:F1}%"),
            new("BC", -1, validTotal, avgBc, $"{avgBc:F1}%"),
            new("MS", -1, validTotal, avgMs, $"{avgMs:F1}%")
        };

        // 2. Candidate-Stage Diagnostics
        int candCompileNum = validIttRecords.Count(r => r.Diagnostic.Compiled);
        int candExecNum = validIttRecords.Count(r => r.Diagnostic.Executed);
        double candAvgSts = validTotal > 0 ? validIttRecords.Average(r => r.Diagnostic.StructuralTargetSatisfaction) : 0;
        double candAvgBc = validTotal > 0 ? validIttRecords.Average(r => r.Diagnostic.BranchCoverage) : 0;
        double candAvgMs = validTotal > 0 ? validIttRecords.Average(r => r.Diagnostic.MutationScore) : 0;

        var diagCells = new List<TableCellOutcome>
        {
            new("VSR", 0, 0, 0, "N/A"),
            new("CSR", candCompileNum, validTotal, validTotal > 0 ? (double)candCompileNum / validTotal * 100.0 : 0.0, $"{candCompileNum}/{validTotal} ({(validTotal > 0 ? (double)candCompileNum / validTotal * 100.0 : 0.0):F1}%)"),
            new("ESR", candExecNum, validTotal, validTotal > 0 ? (double)candExecNum / validTotal * 100.0 : 0.0, $"{candExecNum}/{validTotal} ({(validTotal > 0 ? (double)candExecNum / validTotal * 100.0 : 0.0):F1}%)"),
            new("STS", -1, validTotal, candAvgSts, $"{candAvgSts:F1}%"),
            new("BC", -1, validTotal, candAvgBc, $"{candAvgBc:F1}%"),
            new("MS", -1, validTotal, candAvgMs, $"{candAvgMs:F1}%")
        };

        return new DualAccountingReport(
            DeliveredSystemItt: ittCells,
            CandidateStageDiagnostics: diagCells,
            TotalRecords: total,
            TotalPairs: total,
            MissingEvidenceCount: missingCount,
            IsHeadlineEligible: isHeadlineEligible
        );
    }
}

