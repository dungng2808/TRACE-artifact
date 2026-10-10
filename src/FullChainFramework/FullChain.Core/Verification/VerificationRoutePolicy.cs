using System;
using FullChain.Contracts;

namespace FullChain.Core.Verification;

public enum VerificationVerdict
{
    Verified,
    ActionableRepairRequired,
    TerminalVerifierRejected,
    TerminalUnverifiedAbstain
}

public sealed record VerificationRouteDecision(
    VerificationVerdict Verdict,
    string Decision,
    string Cause,
    string Route,
    string Rationale,
    bool CanLoopToRepair
);

public static class VerificationRoutePolicy
{
    public const int MaxVerificationPasses = 3;

    public static VerificationRouteDecision EvaluateVerdict(
        string decision,
        string cause,
        string route,
        string rationale,
        int currentVerificationPass,
        int remainingRepairBudgetRounds)
    {
        // 1. If verified by B2
        if (string.Equals(decision, VerificationVerdicts.Verified, StringComparison.OrdinalIgnoreCase))
        {
            return new VerificationRouteDecision(
                Verdict: VerificationVerdict.Verified,
                Decision: decision,
                Cause: cause,
                Route: route,
                Rationale: rationale,
                CanLoopToRepair: false
            );
        }

        // 2. If abstained by B2 -> Terminal UNVERIFIED (never coerce to pass or to repair)
        if (string.Equals(decision, VerificationVerdicts.Abstain, StringComparison.OrdinalIgnoreCase))
        {
            return new VerificationRouteDecision(
                Verdict: VerificationVerdict.TerminalUnverifiedAbstain,
                Decision: decision,
                Cause: cause,
                Route: route,
                Rationale: $"Exploratory verifier abstained ({cause}): {rationale}",
                CanLoopToRepair: false
            );
        }

        // 3. If REPAIR_REQUIRED:
        if (string.Equals(decision, VerificationVerdicts.RepairRequired, StringComparison.OrdinalIgnoreCase))
        {
            // Only actionable LOCAL_TEST_REPAIR can loop back to repair, IF repair budget remains and verification passes < Max
            var isLocalRepair = string.Equals(route, VerificationRoutes.LocalTestRepair, StringComparison.OrdinalIgnoreCase);
            if (isLocalRepair && remainingRepairBudgetRounds > 0 && currentVerificationPass < MaxVerificationPasses)
            {
                return new VerificationRouteDecision(
                    Verdict: VerificationVerdict.ActionableRepairRequired,
                    Decision: decision,
                    Cause: cause,
                    Route: route,
                    Rationale: rationale,
                    CanLoopToRepair: true
                );
            }

            // Non-actionable repair or budget exhausted -> Terminal VERIFIER_REJECTED
            var rejectionReason = !isLocalRepair
                ? $"Non-actionable verifier route '{route}' ({cause}); repair loop cannot service this category."
                : (remainingRepairBudgetRounds <= 0
                    ? $"Repair budget exhausted; cannot service verifier repair requirement ({cause})."
                    : $"Maximum verification passes ({MaxVerificationPasses}) reached.");

            return new VerificationRouteDecision(
                Verdict: VerificationVerdict.TerminalVerifierRejected,
                Decision: decision,
                Cause: cause,
                Route: route,
                Rationale: rejectionReason,
                CanLoopToRepair: false
            );
        }

        // Default unknown verdict -> Terminal unverified
        return new VerificationRouteDecision(
            Verdict: VerificationVerdict.TerminalUnverifiedAbstain,
            Decision: decision,
            Cause: cause,
            Route: route,
            Rationale: $"Unknown verifier decision '{decision}'.",
            CanLoopToRepair: false
        );
    }
}
