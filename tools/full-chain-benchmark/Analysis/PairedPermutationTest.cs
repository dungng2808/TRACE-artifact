using System;
using System.Collections.Generic;
using System.Linq;

namespace FullChain.Benchmark.Analysis;

public record PermutationTestResult(
    string Metric,
    double ObservedDifference,
    double PermutationPValue,
    int PermutationCount
);

public class PairedPermutationTest
{
    private readonly int _permutations;
    private readonly int _seed;

    public PairedPermutationTest(int permutations = 10000, int seed = 20260919)
    {
        _permutations = permutations;
        _seed = seed;
    }

    public PermutationTestResult RunTest(string metricName, IReadOnlyList<double> pairedDifferences)
    {
        if (pairedDifferences.Count == 0)
        {
            return new PermutationTestResult(metricName, 0.0, 1.0, 0);
        }

        double obsMeanDiff = pairedDifferences.Average();
        double absObs = Math.Abs(obsMeanDiff);

        if (absObs < 1e-9)
        {
            return new PermutationTestResult(metricName, obsMeanDiff, 1.0, _permutations);
        }

        var rng = new Random(_seed);
        int extremeCount = 0;
        int n = pairedDifferences.Count;

        for (int p = 0; p < _permutations; p++)
        {
            double permSum = 0.0;
            for (int i = 0; i < n; i++)
            {
                int sign = rng.Next(2) == 0 ? -1 : 1;
                permSum += sign * pairedDifferences[i];
            }

            double permMean = permSum / n;
            if (Math.Abs(permMean) >= absObs)
            {
                extremeCount++;
            }
        }

        double pValue = (double)(extremeCount + 1) / (_permutations + 1);

        return new PermutationTestResult(
            Metric: metricName,
            ObservedDifference: obsMeanDiff,
            PermutationPValue: pValue,
            PermutationCount: _permutations
        );
    }
}

