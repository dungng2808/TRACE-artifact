using System;
using FullChain.Contracts;

namespace FullChain.Core;

public sealed class BudgetExhaustedException : Exception
{
    public string ExhaustedResource { get; }

    public BudgetExhaustedException(string resource, string message) : base(message)
    {
        ExhaustedResource = resource;
    }
}

public sealed class GlobalBudgetGovernor : IGlobalBudgetGovernor
{
    private readonly object _lock = new();
    private readonly DateTimeOffset _startTimeUtc;

    public double HardLimitSeconds { get; }
    public int MaxRequests { get; }
    public int MaxTokens { get; }

    public double TotalElapsedSeconds => (DateTimeOffset.UtcNow - _startTimeUtc).TotalSeconds;
    public int RequestsUsed { get; private set; }
    public int TokensUsed { get; private set; }

    public double RemainingSeconds => Math.Max(0.0, HardLimitSeconds - TotalElapsedSeconds);
    public bool IsExhausted => TotalElapsedSeconds >= HardLimitSeconds || RequestsUsed >= MaxRequests || TokensUsed >= MaxTokens;

    public GlobalBudgetGovernor(BudgetPolicy policy)
    {
        _startTimeUtc = DateTimeOffset.UtcNow;
        HardLimitSeconds = policy.HardWallClockSeconds;
        MaxRequests = policy.FullChain.MaxProviderRequests;
        MaxTokens = policy.FullChain.MaxCumulativeTokens;
    }

    public void CheckBudget(string nextStage, int estimatedTokens = 0)
    {
        lock (_lock)
        {
            if (TotalElapsedSeconds >= HardLimitSeconds)
            {
                throw new BudgetExhaustedException("WALL_CLOCK",
                    $"Hard wall-clock limit reached ({TotalElapsedSeconds:F1}s >= {HardLimitSeconds}s) before entering stage '{nextStage}'.");
            }

            if (RequestsUsed >= MaxRequests)
            {
                throw new BudgetExhaustedException("REQUESTS",
                    $"Maximum provider request ceiling reached ({RequestsUsed}/{MaxRequests}) before entering stage '{nextStage}'.");
            }

            if (TokensUsed + estimatedTokens > MaxTokens)
            {
                throw new BudgetExhaustedException("TOKENS",
                    $"Cumulative token limit exceeded ({TokensUsed} + {estimatedTokens} > {MaxTokens}) before entering stage '{nextStage}'.");
            }
        }
    }

    public void RecordUsage(string stage, int requestsUsed, int tokensUsed, double elapsedSeconds)
    {
        lock (_lock)
        {
            RequestsUsed += requestsUsed;
            TokensUsed += tokensUsed;
        }
    }

    public TimeSpan GetRemainingStageTimeout(string stage, TimeSpan stageBudget)
    {
        lock (_lock)
        {
            var remainingWallClock = TimeSpan.FromSeconds(RemainingSeconds);
            return remainingWallClock < stageBudget ? remainingWallClock : stageBudget;
        }
    }
}

