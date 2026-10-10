using System;
using System.Collections.Generic;
using System.Linq;
using FullChain.Contracts;

namespace FullChain.Benchmark;

public sealed record ReadinessReport(
    bool IsEligible,
    int TotalExpectedPairs,
    int CompletedPairs,
    int MissingEvidenceCount,
    IReadOnlyList<string> Disqualifications,
    IReadOnlyList<string> Warnings
);

public static class ReadinessGate
{
    public static ReadinessReport EvaluateCampaign(
        IReadOnlyList<TargetDescriptor> expectedTargets,
        IReadOnlyList<int> expectedRepetitions,
        IReadOnlyList<FullChainRecord> allRecords)
    {
        ArgumentNullException.ThrowIfNull(expectedTargets);
        ArgumentNullException.ThrowIfNull(expectedRepetitions);
        ArgumentNullException.ThrowIfNull(allRecords);

        var disqualifications = new List<string>();
        var warnings = new List<string>();

        int expectedPairs = expectedTargets.Count * expectedRepetitions.Count;
        int completedPairs = 0;
        int missingEvidenceCount = 0;

        // Group records by (SubjectId, RepetitionId)
        var grouped = allRecords
            .GroupBy(r => (r.SubjectId, r.RepetitionId))
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var target in expectedTargets)
        {
            foreach (var rep in expectedRepetitions)
            {
                var key = (target.SubjectId, rep);
                if (!grouped.TryGetValue(key, out var pairRecords))
                {
                    disqualifications.Add($"Missing entire pair for target '{target.SubjectId}', repetition {rep}.");
                    continue;
                }

                var fullChainRec = pairRecords.FirstOrDefault(r => string.Equals(r.Treatment, "FullChain", StringComparison.OrdinalIgnoreCase));
                var evoSuiteRec = pairRecords.FirstOrDefault(r => string.Equals(r.Treatment, "EvoSuite", StringComparison.OrdinalIgnoreCase));

                if (fullChainRec == null)
                {
                    disqualifications.Add($"Target '{target.SubjectId}', rep {rep}: missing FullChain treatment run.");
                }

                if (evoSuiteRec == null)
                {
                    disqualifications.Add($"Target '{target.SubjectId}', rep {rep}: missing EvoSuite baseline run.");
                }

                if (fullChainRec != null && evoSuiteRec != null)
                {
                    completedPairs++;

                    // Check for MissingEvidence in either treatment
                    if (fullChainRec.TerminalStatus == TerminalStatus.MissingEvidence || fullChainRec.Diagnostic.HasMissingEvidence)
                    {
                        missingEvidenceCount++;
                        disqualifications.Add($"Target '{target.SubjectId}', rep {rep}: FullChain contains unresolved MissingEvidence.");
                    }

                    if (evoSuiteRec.TerminalStatus == TerminalStatus.MissingEvidence || evoSuiteRec.Diagnostic.HasMissingEvidence)
                    {
                        missingEvidenceCount++;
                        disqualifications.Add($"Target '{target.SubjectId}', rep {rep}: EvoSuite contains unresolved MissingEvidence.");
                    }
                }
            }
        }

        // Check for unexpected extra records (mixed campaign corruption)
        var extraRecords = allRecords.Where(r => !expectedTargets.Any(t => t.SubjectId == r.SubjectId) || !expectedRepetitions.Contains(r.RepetitionId)).ToList();
        if (extraRecords.Count > 0)
        {
            warnings.Add($"Found {extraRecords.Count} records not matching declared manifest targets or repetitions.");
        }

        bool isEligible = disqualifications.Count == 0 && completedPairs == expectedPairs;

        return new ReadinessReport(
            IsEligible: isEligible,
            TotalExpectedPairs: expectedPairs,
            CompletedPairs: completedPairs,
            MissingEvidenceCount: missingEvidenceCount,
            Disqualifications: disqualifications,
            Warnings: warnings
        );
    }
}

