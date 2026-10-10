using RQ2Experiment.Infrastructure.Diagnostics;
using RQ2Experiment.Core.Engine;
using RQ2Experiment.Infrastructure.Benchmark;
using RQ2Experiment.Infrastructure.Persistence;

var developmentManifestIndex = Array.FindIndex(
    args,
    argument => argument.Equals("--create-development-manifest", StringComparison.OrdinalIgnoreCase));
if (developmentManifestIndex >= 0)
{
    if (developmentManifestIndex + 3 >= args.Length)
    {
        Console.Error.WriteLine(
            "Usage: RQ2Experiment.Runner --create-development-manifest <candidate.json> <held-out.json> <output.json> [maximum-tasks]");
        Environment.ExitCode = 2;
        return;
    }

    int? maximumTasks = developmentManifestIndex + 4 < args.Length &&
                        int.TryParse(args[developmentManifestIndex + 4], out var parsedMaximum)
        ? parsedMaximum
        : null;
    var development = await DevelopmentManifestBuilder.BuildAsync(
        args[developmentManifestIndex + 1],
        args[developmentManifestIndex + 2],
        args[developmentManifestIndex + 3],
        maximumTasks,
        CancellationToken.None);
    Console.WriteLine(
        $"Development candidate manifest: {development.DevelopmentCandidates} disjoint task(s) " +
        $"from {development.CandidateTasks} candidate(s), excluding {development.HeldOutTasks} held-out task(s) " +
        $"and {development.StructurallyExcludedCandidates} structurally invalid candidate(s).");
    Console.WriteLine($"Output: {development.OutputManifestPath}");
    Console.WriteLine("Run --preflight on this candidate manifest before using it in the WPF experiment runner.");
    return;
}

var filterReadyManifestIndex = Array.FindIndex(
    args,
    argument => argument.Equals("--filter-ready-manifest", StringComparison.OrdinalIgnoreCase));
if (filterReadyManifestIndex >= 0)
{
    if (filterReadyManifestIndex + 3 >= args.Length)
    {
        Console.Error.WriteLine(
            "Usage: RQ2Experiment.Runner --filter-ready-manifest <candidate.json> <task-readiness.csv> <output.json>");
        Environment.ExitCode = 2;
        return;
    }

    var readyManifest = await ReadyManifestFilter.FilterAsync(
        args[filterReadyManifestIndex + 1],
        args[filterReadyManifestIndex + 2],
        args[filterReadyManifestIndex + 3],
        CancellationToken.None);
    Console.WriteLine($"READY manifest: {readyManifest.ReadyTasks}/{readyManifest.CandidateTasks} task(s).");
    Console.WriteLine($"Output: {readyManifest.OutputManifestPath}");
    return;
}

var postDevelopmentManifestIndex = Array.FindIndex(
    args,
    argument => argument.Equals("--prepare-postdevelopment-evaluation", StringComparison.OrdinalIgnoreCase));
if (postDevelopmentManifestIndex >= 0)
{
    if (postDevelopmentManifestIndex + 2 >= args.Length)
    {
        Console.Error.WriteLine(
            "Usage: RQ2Experiment.Runner --prepare-postdevelopment-evaluation <ready-manifest.json> <output.json>");
        Environment.ExitCode = 2;
        return;
    }

    var outputPath = await PostDevelopmentEvaluationManifestBuilder.BuildAsync(
        args[postDevelopmentManifestIndex + 1],
        args[postDevelopmentManifestIndex + 2],
        CancellationToken.None);
    Console.WriteLine("Post-development exploratory manifest prepared (not an independent confirmatory holdout).");
    Console.WriteLine($"Output: {outputPath}");
    return;
}

var planCampaignIndex = Array.FindIndex(
    args,
    argument => argument.Equals("--plan-campaign", StringComparison.OrdinalIgnoreCase));
if (planCampaignIndex >= 0)
{
    if (planCampaignIndex + 3 >= args.Length)
    {
        Console.Error.WriteLine(
            "Usage: RQ2Experiment.Runner --plan-campaign <sampling-frame.json> <target-independent-units> <output-directory> [selection-seed]");
        Environment.ExitCode = 2;
        return;
    }

    if (!int.TryParse(args[planCampaignIndex + 2], out var targetIndependentUnits) || targetIndependentUnits <= 0)
    {
        Console.Error.WriteLine("Target independent units must be a positive integer.");
        Environment.ExitCode = 2;
        return;
    }

    var selectionSeed = planCampaignIndex + 4 < args.Length &&
                        int.TryParse(args[planCampaignIndex + 4], out var parsedSeed)
        ? parsedSeed
        : 20260814;
    var plan = await CampaignCapacityPlanner.BuildAsync(
        args[planCampaignIndex + 1],
        targetIndependentUnits,
        selectionSeed,
        Path.GetFullPath(args[planCampaignIndex + 3]),
        CancellationToken.None);
    Console.WriteLine("RQ2 campaign capacity plan");
    Console.WriteLine(
        $"Target={plan.TargetIndependentUnits}; available={plan.AvailableIndependentUnits}; " +
        $"selected={plan.SelectedIndependentUnits}; missing={plan.AdditionalIndependentUnitsRequired}; " +
        $"projects={plan.AvailableProjects}; runnable={plan.IsRunnable}.");
    Console.WriteLine($"Selected candidates CSV: {plan.CandidateCsvPath}");
    Console.WriteLine($"Capacity plan JSON: {plan.PlanPath}");
    Console.WriteLine($"Capacity report: {plan.ReportPath}");
    if (!plan.IsRunnable)
    {
        Console.WriteLine("WARNING: Do not duplicate existing bugs to fill the shortfall. Add new independently reconstructed bugs/projects, then rebuild this plan.");
        Environment.ExitCode = 3;
    }
    return;
}

var buildPilotFrameIndex = Array.FindIndex(
    args,
    argument => argument.Equals("--build-pilot-frame", StringComparison.OrdinalIgnoreCase));
if (buildPilotFrameIndex >= 0)
{
    if (buildPilotFrameIndex + 3 >= args.Length)
    {
        Console.Error.WriteLine(
            "Usage: RQ2Experiment.Runner --build-pilot-frame <libro-root> <existing-manifest.json> <output-directory> [target-size] [selection-seed]");
        Environment.ExitCode = 2;
        return;
    }

    var libroRoot = Path.GetFullPath(args[buildPilotFrameIndex + 1]);
    var existingManifest = Path.GetFullPath(args[buildPilotFrameIndex + 2]);
    var outputDirectory = Path.GetFullPath(args[buildPilotFrameIndex + 3]);
    var targetSize = buildPilotFrameIndex + 4 < args.Length &&
                     int.TryParse(args[buildPilotFrameIndex + 4], out var parsedTarget)
        ? parsedTarget
        : 18;
    var selectionSeed = buildPilotFrameIndex + 5 < args.Length &&
                        int.TryParse(args[buildPilotFrameIndex + 5], out var parsedSeed)
        ? parsedSeed
        : 20260814;
    var frame = await PilotSamplingFrameBuilder.BuildAsync(
        libroRoot,
        existingManifest,
        outputDirectory,
        targetSize,
        selectionSeed,
        CancellationToken.None);
    Console.WriteLine("RQ2 Phase 7.8 pilot sampling frame");
    Console.WriteLine(
        $"Verified={frame.VerifiedBugs}; eligible={frame.EligibleBugs}; selected={frame.SelectedBugs}; " +
        $"projects={frame.SelectedProjects}; ready-to-reconstruct={frame.ReadyToReconstruct}.");
    Console.WriteLine($"Candidate frame CSV: {frame.CandidateFramePath}");
    Console.WriteLine($"Sampling summary CSV: {frame.SummaryPath}");
    Console.WriteLine($"Frozen frame JSON: {frame.JsonPath}");
    if (frame.ReadyToReconstruct < frame.SelectedBugs)
    {
        Console.WriteLine(
            "WARNING: Selected rows are sampling-frame candidates only. Download the official generated-test and repository archives before authoring runnable manifest tasks.");
    }
    return;
}

var analyzeIndex = Array.FindIndex(
    args,
    argument => argument.Equals("--analyze", StringComparison.OrdinalIgnoreCase));
if (analyzeIndex >= 0)
{
    if (analyzeIndex + 1 >= args.Length)
    {
        Console.Error.WriteLine("Usage: RQ2Experiment.Runner --analyze <runs.jsonl> [output-directory]");
        Environment.ExitCode = 2;
        return;
    }

    var runsPath = Path.GetFullPath(args[analyzeIndex + 1]);
    var outputDirectory = analyzeIndex + 2 < args.Length
        ? Path.GetFullPath(args[analyzeIndex + 2])
        : Path.GetDirectoryName(runsPath)!;
    var allResults = await JsonLinesRunReader.ReadAsync(runsPath, CancellationToken.None);
    var results = ExperimentResultBatchSelector.SelectLatest(allResults);
    var analysis = Rq2StatisticalAnalyzer.Analyze(results);
    var claim = Rq2ClaimEvaluator.Evaluate(results, analysis);
    var export = await Rq2AnalysisExporter.ExportAsync(
        results,
        analysis,
        outputDirectory,
        CancellationToken.None);
    var reportPath = await Rq2NarrativeReportExporter.ExportAsync(
        results,
        analysis,
        outputDirectory,
        CancellationToken.None);
    var tuningTriage = await Rq2TuningTriageExporter.ExportAsync(
        results,
        outputDirectory,
        CancellationToken.None);
    Console.WriteLine($"RQ2 analysis: {analysis.Readiness.DisplayStatus}");
    Console.WriteLine(analysis.OverallConclusion);
    foreach (var issue in analysis.Readiness.Issues) Console.WriteLine($"WARNING: {issue}");
    foreach (var comparison in analysis.Comparisons)
    {
        Console.WriteLine(
            $"{comparison.Comparison}: delta={comparison.SuccessRateDeltaDisplay}; " +
            $"W/T/L={comparison.TaskWinTieLossDisplay}; Holm p={comparison.HolmAdjustedPValueDisplay}");
    }
    Console.WriteLine($"Claim gate: {claim.Status}");
    foreach (var projection in claim.PilotPowerProjections)
        Console.WriteLine($"Power projection {projection.Comparison}: required={projection.RequiredDisplay}; power@n=100={projection.PowerAt100Display}");
    Console.WriteLine($"Summary CSV: {export.SummaryPath}");
    Console.WriteLine($"Pairwise CSV: {export.PairwisePath}");
    Console.WriteLine($"Task-level CSV: {export.TaskLevelPath}");
    Console.WriteLine($"Project-level CSV: {export.ProjectLevelPath}");
    Console.WriteLine($"Outcomes CSV: {export.OutcomesPath}");
    Console.WriteLine($"RQ2 report: {reportPath}");
    Console.WriteLine($"TGSLR tuning candidates: {tuningTriage.CandidatesPath}");
    Console.WriteLine($"TGSLR tuning guide: {tuningTriage.SummaryPath}");
    return;
}

var preflightIndex = Array.FindIndex(
    args,
    argument => argument.Equals("--preflight", StringComparison.OrdinalIgnoreCase));
if (preflightIndex >= 0)
{
    if (preflightIndex + 1 >= args.Length)
    {
        Console.Error.WriteLine("Usage: RQ2Experiment.Runner --preflight <benchmark.json>");
        Environment.ExitCode = 2;
        return;
    }

    Console.WriteLine("RQ2 benchmark preflight");
    var preflightMessages = await BenchmarkPreflight.RunAsync(
        args[preflightIndex + 1],
        CancellationToken.None);
    foreach (var message in preflightMessages) Console.WriteLine(message);
    return;
}

if (args.Contains("--diagnostics", StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine("RQ2 infrastructure diagnostics");
    var diagnosticMessages = await InfrastructureDiagnostics.RunAsync(CancellationToken.None);
    foreach (var message in diagnosticMessages) Console.WriteLine(message);
    return;
}

Console.Error.WriteLine("Supported options: --diagnostics, --preflight <benchmark.json>, --analyze <runs.jsonl> [output-directory], --create-development-manifest <candidate.json> <held-out.json> <output.json> [maximum-tasks], --filter-ready-manifest <candidate.json> <task-readiness.csv> <output.json>, --prepare-postdevelopment-evaluation <ready-manifest.json> <output.json>, --plan-campaign <sampling-frame.json> <target-independent-units> <output-directory> [selection-seed], or --build-pilot-frame <libro-root> <existing-manifest.json> <output-directory> [target-size] [selection-seed].");
Console.Error.WriteLine("Run real experiments from RQ2Experiment.Wpf.");
Environment.ExitCode = 2;
