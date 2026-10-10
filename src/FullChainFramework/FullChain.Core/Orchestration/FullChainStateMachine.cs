using System;
using FullChain.Contracts;

namespace FullChain.Core.Orchestration;

public static class FullChainStages
{
    public const string PlannedGeneration = "PLANNED_GENERATION";
    public const string CandidateCompileAndExecute = "CANDIDATE_COMPILE_AND_EXECUTE";
    public const string Repair = "REPAIR";
    public const string CommonEvaluation = "COMMON_EVALUATION";
    public const string B2Verify = "B2_VERIFY";
    public const string Delivered = "DELIVERED";
    public const string Terminal = "TERMINAL";
}

public sealed class FullChainStateMachine
{
    public const int DefaultMaxRepairRounds = 2;
    public const int DefaultMaxVerificationPasses = 3;

    public int MaxRepairRounds { get; }
    public int MaxVerificationPasses { get; }

    public FullChainStateMachine(
        int maxRepairRounds = DefaultMaxRepairRounds,
        int maxVerificationPasses = DefaultMaxVerificationPasses)
    {
        if (maxRepairRounds <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxRepairRounds), "MaxRepairRounds must be positive.");
        if (maxVerificationPasses <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxVerificationPasses), "MaxVerificationPasses must be positive.");

        MaxRepairRounds = maxRepairRounds;
        MaxVerificationPasses = maxVerificationPasses;
    }

    public static FullChainState CreateInitialState()
    {
        return new FullChainState(
            CurrentStage: FullChainStages.PlannedGeneration,
            Status: "Running",
            TerminalReason: null,
            RepairRound: 0,
            VerificationPass: 0,
            RequestsUsed: 0,
            TokensUsed: 0,
            WallClockSecondsElapsed: 0.0
        );
    }

    public FullChainState TransitionFromGeneration(FullChainState current, bool generationSuccess, string? failureStatus = null, string? failureReason = null)
    {
        EnsureValidFromStage(current, FullChainStages.PlannedGeneration);

        if (generationSuccess)
        {
            return current with
            {
                CurrentStage = FullChainStages.CandidateCompileAndExecute,
                Status = "Running"
            };
        }

        string status = failureStatus ?? TerminalStatus.GenerationFailed;
        return current with
        {
            CurrentStage = FullChainStages.Terminal,
            Status = status,
            TerminalReason = failureReason ?? "GenerationFailed"
        };
    }

    public FullChainState TransitionFromCompileAndExecute(
        FullChainState current,
        bool compileSuccess,
        bool executionSuccess,
        bool isRepairEligible,
        bool hasRemainingBudget)
    {
        EnsureValidFromStage(current, FullChainStages.CandidateCompileAndExecute);

        // If tests compiled and passed execution cleanly:
        if (compileSuccess && executionSuccess)
        {
            return current with
            {
                CurrentStage = FullChainStages.CommonEvaluation,
                Status = "Running"
            };
        }

        // Test failed to compile or failed execution:
        // Check if repair is bounded and eligible
        if (isRepairEligible && hasRemainingBudget && current.RepairRound < MaxRepairRounds)
        {
            return current with
            {
                CurrentStage = FullChainStages.Repair,
                Status = "Running",
                RepairRound = current.RepairRound + 1
            };
        }

        // Repair exhausted, not eligible, or budget exceeded:
        // Candidate proceeds to COMMON_EVALUATION so that CandidateDiagnostic is recorded
        return current with
        {
            CurrentStage = FullChainStages.CommonEvaluation,
            Status = "Running"
        };
    }

    public FullChainState TransitionFromRepair(FullChainState current)
    {
        EnsureValidFromStage(current, FullChainStages.Repair);

        // From repair, always proceed to CandidateCompileAndExecute
        return current with
        {
            CurrentStage = FullChainStages.CandidateCompileAndExecute,
            Status = "Running"
        };
    }

    public FullChainState TransitionFromEvaluation(FullChainState current)
    {
        EnsureValidFromStage(current, FullChainStages.CommonEvaluation);

        // From common evaluation, proceed to B2_VERIFY
        return current with
        {
            CurrentStage = FullChainStages.B2Verify,
            Status = "Running",
            VerificationPass = current.VerificationPass + 1
        };
    }

    public FullChainState TransitionFromVerification(
        FullChainState current,
        VerificationDecision decision,
        bool isLocalRepairEligible,
        bool hasRemainingBudget)
    {
        EnsureValidFromStage(current, FullChainStages.B2Verify);

        switch (decision.AggregateVerdict)
        {
            case VerificationVerdicts.Verified:
                return current with
                {
                    CurrentStage = FullChainStages.Delivered,
                    Status = TerminalStatus.Succeeded,
                    TerminalReason = "VerifiedByB2"
                };

            case VerificationVerdicts.Abstain:
                // Invariant: ABSTAIN cannot loop back to generation or repair
                return current with
                {
                    CurrentStage = FullChainStages.Terminal,
                    Status = TerminalStatus.Unverified,
                    TerminalReason = "B2Abstain"
                };

            case VerificationVerdicts.VerifierRejected:
                // Invariant: non-actionable rejection cannot loop back
                return current with
                {
                    CurrentStage = FullChainStages.Terminal,
                    Status = TerminalStatus.VerifierRejected,
                    TerminalReason = "B2Rejected"
                };

            case VerificationVerdicts.RepairRequired:
                // Invariant: only actionable LOCAL_TEST_REPAIR with remaining budget and rounds can loop
                bool canRepair = decision.ActionableRoute == VerificationRoutes.LocalTestRepair
                                 && isLocalRepairEligible
                                 && hasRemainingBudget
                                 && current.RepairRound < MaxRepairRounds
                                 && current.VerificationPass < MaxVerificationPasses;

                if (canRepair)
                {
                    return current with
                    {
                        CurrentStage = FullChainStages.Repair,
                        Status = "Running",
                        RepairRound = current.RepairRound + 1
                    };
                }

                // If rounds/passes exhausted or budget depleted:
                string terminalStatus = current.RepairRound >= MaxRepairRounds
                    ? TerminalStatus.RepairExhausted
                    : !hasRemainingBudget
                        ? TerminalStatus.BudgetExhausted
                        : TerminalStatus.VerifierRejected;

                return current with
                {
                    CurrentStage = FullChainStages.Terminal,
                    Status = terminalStatus,
                    TerminalReason = $"B2RepairRequiredButCannotProceed_{terminalStatus}"
                };

            default:
                return current with
                {
                    CurrentStage = FullChainStages.Terminal,
                    Status = TerminalStatus.Unverified,
                    TerminalReason = $"UnknownVerifierVerdict_{decision.AggregateVerdict}"
                };
        }
    }

    public FullChainState TransitionToTerminal(FullChainState current, string terminalStatus, string reason)
    {
        if (IsTerminal(current.CurrentStage))
            return current;

        return current with
        {
            CurrentStage = FullChainStages.Terminal,
            Status = terminalStatus,
            TerminalReason = reason
        };
    }

    public static bool IsTerminal(string stage)
    {
        return stage is FullChainStages.Delivered or FullChainStages.Terminal;
    }

    public static bool IsValidTransition(string fromStage, string toStage)
    {
        if (fromStage == toStage)
            return false;

        return fromStage switch
        {
            FullChainStages.PlannedGeneration => toStage is FullChainStages.CandidateCompileAndExecute or FullChainStages.Terminal,
            FullChainStages.CandidateCompileAndExecute => toStage is FullChainStages.Repair or FullChainStages.CommonEvaluation or FullChainStages.Terminal,
            FullChainStages.Repair => toStage is FullChainStages.CandidateCompileAndExecute or FullChainStages.Terminal,
            FullChainStages.CommonEvaluation => toStage is FullChainStages.B2Verify or FullChainStages.Terminal,
            FullChainStages.B2Verify => toStage is FullChainStages.Repair or FullChainStages.Delivered or FullChainStages.Terminal,
            FullChainStages.Delivered => false,
            FullChainStages.Terminal => false,
            _ => false
        };
    }

    private static void EnsureValidFromStage(FullChainState state, string expectedStage)
    {
        if (state.CurrentStage != expectedStage)
        {
            throw new InvalidOperationException(
                $"Invalid state transition: expected stage '{expectedStage}', but current stage is '{state.CurrentStage}'.");
        }
    }
}
