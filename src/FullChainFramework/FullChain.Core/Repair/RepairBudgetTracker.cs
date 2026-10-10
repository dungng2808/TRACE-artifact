using System;
using System.Diagnostics;
using FullChain.Contracts;

namespace FullChain.Core.Repair;

public sealed class RepairBudgetTracker
{
    private readonly int _maxRounds;
    private int _completedRounds;
    private int _repairTokensUsed;
    private int _repairRequestsMade;
    private readonly Stopwatch _stopwatch = new();

    public RepairBudgetTracker(int maxRounds = 2)
    {
        _maxRounds = Math.Min(maxRounds, RepairRoutePolicy.MaxAllowedRepairRounds);
    }

    public int MaxRounds => _maxRounds;
    public int CompletedRounds => _completedRounds;
    public int RepairTokensUsed => _repairTokensUsed;
    public int RepairRequestsMade => _repairRequestsMade;
    public TimeSpan Elapsed => _stopwatch.Elapsed;
    public bool CanRepair => _completedRounds < _maxRounds;

    public void StartRound()
    {
        if (!_stopwatch.IsRunning)
        {
            _stopwatch.Start();
        }
    }

    public void RecordRound(int tokens, int requests = 1)
    {
        _completedRounds++;
        _repairTokensUsed += tokens;
        _repairRequestsMade += requests;
    }
}

