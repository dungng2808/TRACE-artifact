namespace RQ2Experiment.Core.Domain;

public enum RepairStrategy
{
    TraceabilityGuidedScenarioLocal,
    FailingTestMethod,
    FullClassRegeneration
}

public static class RepairStrategyExtensions
{
    public static string ToDisplayName(this RepairStrategy strategy) => strategy switch
    {
        RepairStrategy.TraceabilityGuidedScenarioLocal => "TGSLR",
        RepairStrategy.FailingTestMethod => "FTMR",
        RepairStrategy.FullClassRegeneration => "FCR",
        _ => strategy.ToString()
    };
}
