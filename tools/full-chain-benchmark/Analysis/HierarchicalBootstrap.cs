using System;
using System.Collections.Generic;
using System.Linq;
using FullChain.Contracts;

namespace FullChain.Benchmark.Analysis;

public record BootstrapMetricResult(
    string Metric,
    double FullChainMean,
    double EvoSuiteMean,
    double PointEstimateDifference,
    double StandardError,
    double CiLower95,
    double CiUpper95
);

public class HierarchicalBootstrap
{
    private readonly int _resamples;
    private readonly int _seed;

    public HierarchicalBootstrap(int resamples = 2000, int seed = 20260919)
    {
        _resamples = resamples;
        _seed = seed;
    }

    public record TargetRepetitionData(
        string TargetId,
        string ProjectId,
        int RepetitionId,
        DeliveredSystemOutcome FullChainOutcome,
        CandidateDiagnostic FullChainDiagnostic,
        DeliveredSystemOutcome EvoSuiteOutcome,
        CandidateDiagnostic EvoSuiteDiagnostic
    );

    public Dictionary<string, BootstrapMetricResult> ComputeDeliveredSystemBootstrap(
        IReadOnlyList<TargetRepetitionData> dataset)
    {
        return ComputeBootstrap(dataset, r => (
            fc: (r.FullChainOutcome.VerifiedSuiteRate, r.FullChainOutcome.CompilationSatisfactionRate, r.FullChainOutcome.ExecutionSatisfactionRate, r.FullChainOutcome.StructuralTargetSatisfaction, r.FullChainOutcome.BranchCoverage, r.FullChainOutcome.MutationScore),
            evo: (r.EvoSuiteOutcome.VerifiedSuiteRate, r.EvoSuiteOutcome.CompilationSatisfactionRate, r.EvoSuiteOutcome.ExecutionSatisfactionRate, r.EvoSuiteOutcome.StructuralTargetSatisfaction, r.EvoSuiteOutcome.BranchCoverage, r.EvoSuiteOutcome.MutationScore)
        ));
    }

    public Dictionary<string, BootstrapMetricResult> ComputeCandidateDiagnosticBootstrap(
        IReadOnlyList<TargetRepetitionData> dataset)
    {
        return ComputeBootstrap(dataset, r => (
            fc: (0.0, r.FullChainDiagnostic.Compiled ? 1.0 : 0.0, r.FullChainDiagnostic.Executed ? 1.0 : 0.0, r.FullChainDiagnostic.StructuralTargetSatisfaction, r.FullChainDiagnostic.BranchCoverage, r.FullChainDiagnostic.MutationScore),
            evo: (0.0, r.EvoSuiteDiagnostic.Compiled ? 1.0 : 0.0, r.EvoSuiteDiagnostic.Executed ? 1.0 : 0.0, r.EvoSuiteDiagnostic.StructuralTargetSatisfaction, r.EvoSuiteDiagnostic.BranchCoverage, r.EvoSuiteDiagnostic.MutationScore)
        ));
    }

    private Dictionary<string, BootstrapMetricResult> ComputeBootstrap(
        IReadOnlyList<TargetRepetitionData> dataset,
        Func<TargetRepetitionData, ((double vsr, double csr, double esr, double sts, double bc, double ms) fc, (double vsr, double csr, double esr, double sts, double bc, double ms) evo)> metricExtractor)
    {
        var rng = new Random(_seed);
        var metrics = new[] { "VSR", "CSR", "ESR", "STS", "BC", "MS" };

        // Group dataset: Project -> Target -> List<TargetRepetitionData>
        var projectGroups = dataset
            .GroupBy(d => d.ProjectId)
            .ToDictionary(g => g.Key, g => g.GroupBy(t => t.TargetId).ToDictionary(tg => tg.Key, tg => tg.ToList()));

        var projectsList = projectGroups.Keys.ToList();

        // 1. Calculate observed sample means and difference
        var originalTargetMeans = dataset
            .GroupBy(d => d.TargetId)
            .Select(tg =>
            {
                var repValues = tg.Select(metricExtractor).ToList();
                return (
                    TargetId: tg.Key,
                    FcVsr: repValues.Average(v => v.fc.vsr),
                    FcCsr: repValues.Average(v => v.fc.csr),
                    FcEsr: repValues.Average(v => v.fc.esr),
                    FcSts: repValues.Average(v => v.fc.sts),
                    FcBc: repValues.Average(v => v.fc.bc),
                    FcMs: repValues.Average(v => v.fc.ms),
                    EvoVsr: repValues.Average(v => v.evo.vsr),
                    EvoCsr: repValues.Average(v => v.evo.csr),
                    EvoEsr: repValues.Average(v => v.evo.esr),
                    EvoSts: repValues.Average(v => v.evo.sts),
                    EvoBc: repValues.Average(v => v.evo.bc),
                    EvoMs: repValues.Average(v => v.evo.ms)
                );
            })
            .ToList();

        var obsFc = (
            vsr: originalTargetMeans.Average(t => t.FcVsr),
            csr: originalTargetMeans.Average(t => t.FcCsr),
            esr: originalTargetMeans.Average(t => t.FcEsr),
            sts: originalTargetMeans.Average(t => t.FcSts),
            bc: originalTargetMeans.Average(t => t.FcBc),
            ms: originalTargetMeans.Average(t => t.FcMs)
        );

        var obsEvo = (
            vsr: originalTargetMeans.Average(t => t.EvoVsr),
            csr: originalTargetMeans.Average(t => t.EvoCsr),
            esr: originalTargetMeans.Average(t => t.EvoEsr),
            sts: originalTargetMeans.Average(t => t.EvoSts),
            bc: originalTargetMeans.Average(t => t.EvoBc),
            ms: originalTargetMeans.Average(t => t.EvoMs)
        );

        var obsDiff = (
            vsr: obsFc.vsr - obsEvo.vsr,
            csr: obsFc.csr - obsEvo.csr,
            esr: obsFc.esr - obsEvo.esr,
            sts: obsFc.sts - obsEvo.sts,
            bc: obsFc.bc - obsEvo.bc,
            ms: obsFc.ms - obsEvo.ms
        );

        // 2. Run B hierarchical cluster bootstrap iterations
        var bootDiffs = new Dictionary<string, List<double>>();
        foreach (var m in metrics) bootDiffs[m] = new List<double>(_resamples);

        for (int b = 0; b < _resamples; b++)
        {
            var resampledTargetDiffs = new List<(double vsr, double csr, double esr, double sts, double bc, double ms)>();

            // Sample projects with replacement
            for (int pIdx = 0; pIdx < projectsList.Count; pIdx++)
            {
                var randProject = projectsList[rng.Next(projectsList.Count)];
                var targetDict = projectGroups[randProject];
                var targetKeys = targetDict.Keys.ToList();

                // Sample targets with replacement within selected project
                for (int tIdx = 0; tIdx < targetKeys.Count; tIdx++)
                {
                    var randTarget = targetKeys[rng.Next(targetKeys.Count)];
                    var repList = targetDict[randTarget];

                    // Sample repetitions with replacement
                    double sumDiffVsr = 0, sumDiffCsr = 0, sumDiffEsr = 0, sumDiffSts = 0, sumDiffBc = 0, sumDiffMs = 0;
                    int rCount = repList.Count;
                    for (int rIdx = 0; rIdx < rCount; rIdx++)
                    {
                        var randRep = repList[rng.Next(rCount)];
                        var m = metricExtractor(randRep);
                        sumDiffVsr += (m.fc.vsr - m.evo.vsr);
                        sumDiffCsr += (m.fc.csr - m.evo.csr);
                        sumDiffEsr += (m.fc.esr - m.evo.esr);
                        sumDiffSts += (m.fc.sts - m.evo.sts);
                        sumDiffBc += (m.fc.bc - m.evo.bc);
                        sumDiffMs += (m.fc.ms - m.evo.ms);
                    }

                    resampledTargetDiffs.Add((
                        sumDiffVsr / rCount,
                        sumDiffCsr / rCount,
                        sumDiffEsr / rCount,
                        sumDiffSts / rCount,
                        sumDiffBc / rCount,
                        sumDiffMs / rCount
                    ));
                }
            }

            // Average across all targets in resample
            bootDiffs["VSR"].Add(resampledTargetDiffs.Average(d => d.vsr));
            bootDiffs["CSR"].Add(resampledTargetDiffs.Average(d => d.csr));
            bootDiffs["ESR"].Add(resampledTargetDiffs.Average(d => d.esr));
            bootDiffs["STS"].Add(resampledTargetDiffs.Average(d => d.sts));
            bootDiffs["BC"].Add(resampledTargetDiffs.Average(d => d.bc));
            bootDiffs["MS"].Add(resampledTargetDiffs.Average(d => d.ms));
        }

        // 3. Compute Percentile CI and Standard Error
        var results = new Dictionary<string, BootstrapMetricResult>();
        foreach (var m in metrics)
        {
            var diffs = bootDiffs[m];
            diffs.Sort();

            double se = Math.Sqrt(diffs.Select(d => Math.Pow(d - diffs.Average(), 2)).Sum() / (_resamples - 1));
            int lowerIdx = (int)Math.Floor(0.025 * _resamples);
            int upperIdx = (int)Math.Min(_resamples - 1, Math.Ceiling(0.975 * _resamples));

            double pointDiff = m switch
            {
                "VSR" => obsDiff.vsr,
                "CSR" => obsDiff.csr,
                "ESR" => obsDiff.esr,
                "STS" => obsDiff.sts,
                "BC" => obsDiff.bc,
                "MS" => obsDiff.ms,
                _ => 0
            };

            double fcMean = m switch
            {
                "VSR" => obsFc.vsr,
                "CSR" => obsFc.csr,
                "ESR" => obsFc.esr,
                "STS" => obsFc.sts,
                "BC" => obsFc.bc,
                "MS" => obsFc.ms,
                _ => 0
            };

            double evoMean = m switch
            {
                "VSR" => obsEvo.vsr,
                "CSR" => obsEvo.csr,
                "ESR" => obsEvo.esr,
                "STS" => obsEvo.sts,
                "BC" => obsEvo.bc,
                "MS" => obsEvo.ms,
                _ => 0
            };

            results[m] = new BootstrapMetricResult(
                Metric: m,
                FullChainMean: fcMean,
                EvoSuiteMean: evoMean,
                PointEstimateDifference: pointDiff,
                StandardError: se,
                CiLower95: diffs[lowerIdx],
                CiUpper95: diffs[upperIdx]
            );
        }

        return results;
    }
}

