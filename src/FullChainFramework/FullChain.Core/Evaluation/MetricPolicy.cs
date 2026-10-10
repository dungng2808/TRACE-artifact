using System;
using FullChain.Contracts;

namespace FullChain.Core.Evaluation;

public static class MetricPolicy
{
    public static DeliveredSystemOutcome ComputeDeliveredOutcome(bool isVerified, CandidateDiagnostic diagnostic)
    {
        if (!isVerified)
        {
            // Legitimate terminal non-delivery is scored 0.0 across all delivered metrics
            return new DeliveredSystemOutcome(
                VerifiedSuiteRate: 0.0,
                CompilationSatisfactionRate: 0.0,
                ExecutionSatisfactionRate: 0.0,
                BranchCoverage: 0.0,
                MutationScore: 0.0,
                StructuralTargetSatisfaction: 0.0
            );
        }

        return new DeliveredSystemOutcome(
            VerifiedSuiteRate: 1.0,
            CompilationSatisfactionRate: diagnostic.Compiled ? 1.0 : 0.0,
            ExecutionSatisfactionRate: diagnostic.Executed ? 1.0 : 0.0,
            BranchCoverage: diagnostic.BranchCoverage,
            MutationScore: diagnostic.MutationScore,
            StructuralTargetSatisfaction: diagnostic.StructuralTargetSatisfaction
        );
    }
}

