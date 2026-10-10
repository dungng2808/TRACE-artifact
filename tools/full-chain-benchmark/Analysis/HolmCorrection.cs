using System;
using System.Collections.Generic;
using System.Linq;

namespace FullChain.Benchmark.Analysis;

public record HolmAdjustedResult(
    string Metric,
    double RawPValue,
    double AdjustedPValue,
    int Rank,
    bool IsSignificant
);

public class HolmCorrection
{
    public static List<HolmAdjustedResult> Adjust(
        IReadOnlyList<(string Metric, double RawPValue)> tests,
        double alpha = 0.05)
    {
        int m = tests.Count;
        if (m == 0) return new List<HolmAdjustedResult>();

        // Sort by raw p-value ascending
        var sorted = tests
            .Select((t, originalIndex) => new { t.Metric, t.RawPValue, originalIndex })
            .OrderBy(x => x.RawPValue)
            .ToList();

        var adjustedPValues = new double[m];
        double cumulativeMax = 0.0;

        for (int k = 0; k < m; k++)
        {
            int multiplier = m - k;
            double stepP = sorted[k].RawPValue * multiplier;
            stepP = Math.Min(1.0, stepP);
            cumulativeMax = Math.Max(cumulativeMax, stepP);
            adjustedPValues[k] = cumulativeMax;
        }

        var results = new List<HolmAdjustedResult>();
        for (int k = 0; k < m; k++)
        {
            results.Add(new HolmAdjustedResult(
                Metric: sorted[k].Metric,
                RawPValue: sorted[k].RawPValue,
                AdjustedPValue: adjustedPValues[k],
                Rank: k + 1,
                IsSignificant: adjustedPValues[k] < alpha
            ));
        }

        return results;
    }
}

