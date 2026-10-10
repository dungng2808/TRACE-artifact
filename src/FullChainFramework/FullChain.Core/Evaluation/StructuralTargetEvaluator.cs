using System;
using System.Collections.Generic;
using System.Linq;
using FullChain.Contracts;

namespace FullChain.Core.Evaluation;

public sealed record StructuralTargetEvaluationResult(
    int TotalObligations,
    int SatisfiedObligations,
    double SatisfactionPercentage,
    IReadOnlyDictionary<string, bool> ObligationStatus
);

public static class StructuralTargetEvaluator
{
    public static StructuralTargetEvaluationResult Evaluate(
        IReadOnlyList<BranchObligation> obligations,
        IReadOnlyDictionary<int, int> lineCoveredBranches,
        IReadOnlyDictionary<int, int>? lineCoveredInstructions = null)
    {
        if (obligations == null || obligations.Count == 0)
        {
            return new StructuralTargetEvaluationResult(0, 0, 100.0, new Dictionary<string, bool>());
        }

        var statusMap = new Dictionary<string, bool>();
        int satisfied = 0;

        foreach (var obl in obligations)
        {
            // Branch sites require a covered branch outcome. Return and explicit
            // exception paths are executable source lines, not branch counters;
            // JaCoCo reports their execution through covered instructions.
            var coveredBranches = lineCoveredBranches.TryGetValue(obl.Line, out var branchCount) && branchCount > 0;
            var coveredInstructions = lineCoveredInstructions != null
                                      && lineCoveredInstructions.TryGetValue(obl.Line, out var instructionCount)
                                      && instructionCount > 0;
            bool isSatisfied = obl.Type switch
            {
                "BRANCH_SITE" => coveredBranches,
                "RETURN_PATH" or "EXPLICIT_EXCEPTION_PATH" => coveredInstructions,
                _ => coveredBranches || coveredInstructions
            };
            statusMap[obl.TargetId] = isSatisfied;
            if (isSatisfied) satisfied++;
        }

        double pct = (double)satisfied / obligations.Count * 100.0;
        return new StructuralTargetEvaluationResult(
            TotalObligations: obligations.Count,
            SatisfiedObligations: satisfied,
            SatisfactionPercentage: pct,
            ObligationStatus: statusMap
        );
    }
}
