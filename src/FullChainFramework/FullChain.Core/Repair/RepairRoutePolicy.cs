using System;
using System.Collections.Generic;
using System.Linq;

namespace FullChain.Core.Repair;

public enum RepairEligibilityStatus
{
    Eligible,
    AlreadyPassing,
    IneligibleTimeout,
    IneligibleProviderFault,
    IneligibleCoverageOrMutationToolFailure,
    IneligibleMissingScenarioMapping,
    IneligibleInfrastructureFailure,
    IneligibleMaxRoundsExceeded,
    IneligibleBudgetExceeded,
    IneligibleNonDeterministicFailure
}

public sealed record RepairEligibilityDecision(
    bool IsEligible,
    RepairEligibilityStatus Status,
    string Reason,
    string? FailingTestMethod = null,
    string? ScenarioId = null
);

public static class RepairRoutePolicy
{
    public const int MaxAllowedRepairRounds = 2;

    public static RepairEligibilityDecision EvaluateEligibility(
        bool compileSuccess,
        bool testExecutionSuccess,
        bool timedOut,
        bool providerFault,
        bool toolFailure,
        string failureOutput,
        IReadOnlyDictionary<string, string> scenarioToMethodMap,
        int currentRound,
        int maxRounds,
        bool budgetAllowsRepair)
    {
        if (currentRound >= maxRounds || currentRound >= MaxAllowedRepairRounds)
        {
            return new RepairEligibilityDecision(
                false,
                RepairEligibilityStatus.IneligibleMaxRoundsExceeded,
                $"Maximum repair rounds ({Math.Min(maxRounds, MaxAllowedRepairRounds)}) reached.");
        }

        if (!budgetAllowsRepair)
        {
            return new RepairEligibilityDecision(
                false,
                RepairEligibilityStatus.IneligibleBudgetExceeded,
                "Budget governor does not permit further repair rounds.");
        }

        if (timedOut)
        {
            return new RepairEligibilityDecision(
                false,
                RepairEligibilityStatus.IneligibleTimeout,
                "Timeouts are ineligible for TGSLR repair.");
        }

        if (providerFault)
        {
            return new RepairEligibilityDecision(
                false,
                RepairEligibilityStatus.IneligibleProviderFault,
                "Provider/LLM faults are ineligible for TGSLR repair.");
        }

        if (toolFailure)
        {
            return new RepairEligibilityDecision(
                false,
                RepairEligibilityStatus.IneligibleCoverageOrMutationToolFailure,
                "Coverage or mutation tool errors are ineligible for TGSLR repair.");
        }

        if (compileSuccess && testExecutionSuccess)
        {
            return new RepairEligibilityDecision(
                false,
                RepairEligibilityStatus.AlreadyPassing,
                "Candidate test suite already compiles and passes; repair is not needed.");
        }

        var (failingMethod, scenarioId) = IdentifyFailingMethodAndScenario(failureOutput, scenarioToMethodMap);
        if (string.IsNullOrWhiteSpace(failingMethod) || string.IsNullOrWhiteSpace(scenarioId))
        {
            return new RepairEligibilityDecision(
                false,
                RepairEligibilityStatus.IneligibleMissingScenarioMapping,
                "Could not map failure to a known test method with scenario traceability link.");
        }

        return new RepairEligibilityDecision(
            true,
            RepairEligibilityStatus.Eligible,
            compileSuccess
                ? $"Eligible deterministic assertion/execution failure in mapped method '{failingMethod}' ({scenarioId})."
                : $"Eligible compilation failure in mapped method '{failingMethod}' ({scenarioId}).",
            FailingTestMethod: failingMethod,
            ScenarioId: scenarioId);
    }

    public static (string? FailingMethod, string? ScenarioId) IdentifyFailingMethodAndScenario(
        string failureOutput,
        IReadOnlyDictionary<string, string> scenarioToMethodMap)
    {
        if (scenarioToMethodMap == null || scenarioToMethodMap.Count == 0)
        {
            return (null, null);
        }

        // 1. First check if any method name appears directly in the failure output (e.g. stack trace or compiler error)
        foreach (var (scenarioId, methodName) in scenarioToMethodMap)
        {
            if (failureOutput.Contains(methodName, StringComparison.OrdinalIgnoreCase))
            {
                return (methodName, scenarioId);
            }
        }

        // 2. If output mentions scenario ID (e.g. S01, S05)
        foreach (var (scenarioId, methodName) in scenarioToMethodMap)
        {
            if (failureOutput.Contains(scenarioId, StringComparison.OrdinalIgnoreCase))
            {
                return (methodName, scenarioId);
            }
        }

        // 3. Fallback: if single method exists in map and test execution failed
        if (scenarioToMethodMap.Count == 1)
        {
            var single = scenarioToMethodMap.First();
            return (single.Value, single.Key);
        }

        return (null, null);
    }
}

