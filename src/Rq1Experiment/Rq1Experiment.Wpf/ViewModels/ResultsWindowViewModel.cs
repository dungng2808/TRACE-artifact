using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using Rq1Experiment.Contracts.Experiments;
using Rq1Experiment.Contracts.Generation;
using Rq1Experiment.Contracts.Subjects;
using Rq1Experiment.Contracts.Targets;
using Rq1Experiment.Contracts.Prompts;
using Rq1Experiment.Core.Abstractions;
using Rq1Experiment.Infrastructure.Persistence;

namespace Rq1Experiment.Wpf.ViewModels;

public sealed class ResultsWindowViewModel : ObservableObject
{
    private readonly IMetricsLedgerService _ledgerService;
    private readonly IRunManagementService _runManagementService;
    private readonly MetricsLedgerRequest _request;
    private readonly Func<bool> _isPipelineRunning;
    private int _refreshing;
    private string _summary = "No metrics loaded.";
    private string _runsCsvPath = string.Empty;
    private string _detailTitle = "Chọn Plan, Tests hoặc Logs ở cột Action.";
    private string _detailContent = "Artifact content will be displayed here.";

    public ResultsWindowViewModel(
        IMetricsLedgerService ledgerService,
        MetricsLedgerRequest request,
        Func<bool>? isPipelineRunning = null,
        IRunManagementService? runManagementService = null)
    {
        _ledgerService = ledgerService;
        _runManagementService = runManagementService ?? new RunManagementService();
        _request = request;
        _isPipelineRunning = isPipelineRunning ?? (() => false);
        RebuildCommand = new AsyncRelayCommand(RebuildAsync, onError: exception => Summary = exception.Message);
    }

    public ObservableCollection<ResultMetricRow> Metrics { get; } = [];
    public ObservableCollection<ResultMeanRow> Means { get; } = [];
    public ObservableCollection<ResultMeanRow> ConditionalMeans { get; } = [];
    public string Summary { get => _summary; set => SetProperty(ref _summary, value); }
    public string RunsCsvPath { get => _runsCsvPath; private set => SetProperty(ref _runsCsvPath, value); }
    public string DetailTitle { get => _detailTitle; private set => SetProperty(ref _detailTitle, value); }
    public string DetailContent { get => _detailContent; private set => SetProperty(ref _detailContent, value); }
    public AsyncRelayCommand RebuildCommand { get; }
    public bool IsPipelineRunning => _isPipelineRunning();

    public async Task RebuildAsync()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        try
        {
            if (IsPipelineRunning)
            {
                await LoadLiveArtifactResultsAsync();
                return;
            }
            await RebuildAuthoritativeResultsAsync();
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    private async Task RebuildAuthoritativeResultsAsync()
    {
        Metrics.Clear();
        Means.Clear();
        ConditionalMeans.Clear();
        RunsCsvPath = string.Empty;
        var experimentRunsRoot = Path.Combine(
            _request.ExperimentOutputRoot, "runs", _request.ExperimentId);
        var hasRunArtifacts = Directory.Exists(experimentRunsRoot)
                              && Directory.EnumerateFiles(
                                  experimentRunsRoot, "run.json", SearchOption.AllDirectories).Any();
        if (!hasRunArtifacts)
        {
            await LoadPreGenerationResultsAsync();
            return;
        }

        var report = await _ledgerService.RebuildAsync(_request);
        RunsCsvPath = report.RunsCsvPath;
        var subjects = await LoadSubjectsAsync();
        var metricRecords = new List<RunMetricsRecord>();
        if (File.Exists(report.MetricsJsonlPath))
        {
            foreach (var line in await File.ReadAllLinesAsync(report.MetricsJsonlPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var metric = JsonSerializer.Deserialize<RunMetricsRecord>(line,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (metric is null) continue;
                metricRecords.Add(metric);
                subjects.TryGetValue(metric.Key.SubjectId, out var subject);
                RepositoryContext? runContext = null;
                var runContextPath = Path.Combine(metric.RunDirectory, "input", "common-context.json");
                if (File.Exists(runContextPath))
                    runContext = JsonSerializer.Deserialize<RepositoryContext>(
                        await File.ReadAllTextAsync(runContextPath),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                var planPath = Path.Combine(metric.RunDirectory, "planner", "scenario-plan.json");
                var testDirectory = metric.Key.Approach.Equals("PLANNED", StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(metric.RunDirectory, "generator")
                    : Path.Combine(metric.RunDirectory, "generation");
                var testFiles = Directory.Exists(testDirectory)
                    ? Directory.EnumerateFiles(testDirectory, "*.java", SearchOption.TopDirectoryOnly)
                        .OrderBy(path => path, StringComparer.Ordinal).ToList()
                    : [];
                var failure = await ReadFailureAsync(metric.RunDirectory);
                Metrics.Add(new ResultMetricRow(
                    metric.Key.SubjectId, metric.Key.Approach, metric.Key.Repetition, metric.Status,
                    failure,
                    runContext?.FocalClass ?? subject?.FocalClass ?? metric.ProjectId,
                    runContext?.FocalMethodSignature ?? subject?.FocalMethodSignature ?? "UNKNOWN",
                    FormatBool(metric.CompileSuccess), FormatBool(metric.ExecutionSuccess),
                    FormatRatio(metric.EndToEndTargetSatisfaction), FormatRatio(metric.EndToEndBranchCoverage),
                    FormatRatio(metric.EndToEndMutationScore), FormatRatio(metric.PlanFidelity),
                    FormatRatio(metric.PlanObligationCoverage), FormatRatio(metric.ScenarioValidity),
                    FormatRatio(metric.PassRate), metric.Cost.InputTokens,
                    metric.Cost.OutputTokens, metric.Cost.TotalTokens,
                    FormatDuration(metric.Cost.WallClockSeconds),
                    metric.RunDirectory, planPath, testFiles));
            }
        }
        foreach (var aggregate in report.Approaches)
        {
            var reportedTokens = metricRecords
                .Where(item => item.Key.Approach.Equals(aggregate.Approach, StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Cost.TotalTokens)
                .Where(value => value.HasValue)
                .Select(value => value!.Value)
                .ToList();
            var totalTimes = metricRecords
                .Where(item => item.Key.Approach.Equals(aggregate.Approach, StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Cost.WallClockSeconds)
                .ToList();
            Means.Add(new ResultMeanRow(
                aggregate.Approach,
                aggregate.RunCount,
                FormatRatio(aggregate.CompilationSuccessRate),
                FormatRatio(aggregate.ExecutionSuccessRate),
                FormatRatio(aggregate.MeanEndToEndTargetSatisfaction),
                FormatRatio(aggregate.MeanEndToEndBranchCoverage),
                FormatRatio(aggregate.MeanEndToEndMutationScore),
                FormatRatio(aggregate.MeanPlanFidelity),
                FormatRatio(aggregate.MeanPlanObligationCoverage),
                FormatRatio(aggregate.MeanScenarioValidity),
                FormatRatio(aggregate.MeanPassRate),
                totalTimes.Count == 0 ? "N/A" : FormatDuration(totalTimes.Average()),
                reportedTokens.Count == 0 ? "N/A" : reportedTokens.Average().ToString("N0"),
                FormatTokenTotal(aggregate.TotalInputTokens),
                FormatTokenTotal(aggregate.TotalOutputTokens),
                FormatTokenTotal(aggregate.TotalReportedTokens),
                aggregate.TokenSampleCount,
                aggregate.ToolErrorCount));
        }
        // Paired conditional mean: (subjectId, repetition) pairs where BOTH approaches passed.
        // This avoids selection-bias from comparing different subject populations per approach.
        var passedKeys = metricRecords
            .Where(r => r.ExecutionSuccess == true)
            .Select(r => (r.Key.SubjectId, r.Key.Repetition, r.Key.Approach))
            .ToHashSet();
        var allApproaches = report.Approaches.Select(a => a.Approach).ToList();
        var bothPassedKeys = metricRecords
            .Where(r => r.ExecutionSuccess == true)
            .Select(r => (r.Key.SubjectId, r.Key.Repetition))
            .Where(k => allApproaches.All(a => passedKeys.Contains((k.SubjectId, k.Repetition, a))))
            .ToHashSet();
        foreach (var aggregate in report.Approaches)
        {
            var pairedPassedRecords = metricRecords
                .Where(r => r.Key.Approach.Equals(aggregate.Approach, StringComparison.OrdinalIgnoreCase)
                            && bothPassedKeys.Contains((r.Key.SubjectId, r.Key.Repetition)))
                .ToList();
            var condReportedTokens = pairedPassedRecords
                .Select(item => item.Cost.TotalTokens)
                .Where(value => value.HasValue)
                .Select(value => value!.Value)
                .ToList();
            var condTotalTimes = pairedPassedRecords
                .Select(item => item.Cost.WallClockSeconds)
                .ToList();
            ConditionalMeans.Add(new ResultMeanRow(
                aggregate.Approach,
                pairedPassedRecords.Count,
                FormatRatio(aggregate.CompilationSuccessRate),
                FormatRatio(aggregate.ExecutionSuccessRate),
                pairedPassedRecords.Count == 0 ? "N/A" : FormatRatio(pairedPassedRecords.Average(r => r.EndToEndTargetSatisfaction ?? 0)),
                pairedPassedRecords.Count == 0 ? "N/A" : FormatRatio(pairedPassedRecords.Average(r => r.EndToEndBranchCoverage ?? 0)),
                pairedPassedRecords.Count == 0 ? "N/A" : FormatRatio(pairedPassedRecords.Average(r => r.EndToEndMutationScore ?? 0)),
                pairedPassedRecords.Count == 0 ? "N/A" : FormatRatio(pairedPassedRecords.Where(r => r.PlanFidelity.HasValue).Select(r => r.PlanFidelity!.Value) is var pf && pf.Any() ? pf.Average() : (double?)null),
                pairedPassedRecords.Count == 0 ? "N/A" : FormatRatio(pairedPassedRecords.Where(r => r.PlanObligationCoverage.HasValue).Select(r => r.PlanObligationCoverage!.Value) is var po && po.Any() ? po.Average() : (double?)null),
                pairedPassedRecords.Count == 0 ? "N/A" : FormatRatio(pairedPassedRecords.Where(r => r.ScenarioValidity.HasValue).Select(r => r.ScenarioValidity!.Value) is var sv && sv.Any() ? sv.Average() : (double?)null),
                pairedPassedRecords.Count == 0 ? "N/A" : FormatRatio(pairedPassedRecords.Where(r => r.PassRate.HasValue).Select(r => r.PassRate!.Value) is var pr && pr.Any() ? pr.Average() : (double?)null),
                condTotalTimes.Count == 0 ? "N/A" : FormatDuration(condTotalTimes.Average()),
                condReportedTokens.Count == 0 ? "N/A" : condReportedTokens.Average().ToString("N0"),
                FormatTokenTotal(SumTokenValues(pairedPassedRecords.Select(r => r.Cost.InputTokens))),
                FormatTokenTotal(SumTokenValues(pairedPassedRecords.Select(r => r.Cost.OutputTokens))),
                FormatTokenTotal(SumTokenValues(pairedPassedRecords.Select(r => r.Cost.TotalTokens))),
                condReportedTokens.Count,
                0));
        }
        var allInputTokens = report.Approaches.Where(item => item.TotalInputTokens.HasValue)
            .Sum(item => item.TotalInputTokens!.Value);
        var allOutputTokens = report.Approaches.Where(item => item.TotalOutputTokens.HasValue)
            .Sum(item => item.TotalOutputTokens!.Value);
        var allReportedTokens = report.Approaches.Where(item => item.TotalReportedTokens.HasValue)
            .Sum(item => item.TotalReportedTokens!.Value);
        var tokenSummary = $"Tokens — input: {allInputTokens:N0}; output: {allOutputTokens:N0}; total: {allReportedTokens:N0}.";
        Summary = report.Complete
            ? $"Ledger valid: {report.RunCount} run(s), {report.InvalidRowCount} invalid. {tokenSummary} SHA-256 {report.LedgerSha256}."
            : $"Ledger rejected: {report.InvalidRowCount} invalid row(s). {tokenSummary} See {report.DataQualityReportPath}.";
    }

    private async Task LoadLiveArtifactResultsAsync()
    {
        Metrics.Clear();
        Means.Clear();
        ConditionalMeans.Clear();
        RunsCsvPath = string.Empty;
        var runsRoot = Path.Combine(_request.ExperimentOutputRoot, "runs", _request.ExperimentId);
        var subjects = await LoadSubjectsAsync();
        var live = new List<(string Approach, string SubjectId, int Repetition, int? Input, int? Output, int? Total, double Seconds,
            double? Fidelity, double? ObligationCoverage, double? ScenarioValidity, double? PassRate, bool Passed)>();
        if (Directory.Exists(runsRoot))
        {
            foreach (var path in Directory.EnumerateFiles(runsRoot, "run.json", SearchOption.AllDirectories)
                         .OrderBy(value => value, StringComparer.Ordinal))
            {
                try
                {
                    using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
                    var root = document.RootElement;
                    var approach = JsonString(root, "approach");
                    if (approach is not ("DIRECT" or "PLANNED")) continue;
                    var status = JsonString(root, "status");
                    if (string.IsNullOrWhiteSpace(status)) continue;
                    var subjectId = JsonString(root, "subjectId");
                    var repetition = JsonInt(root, "repetition") ?? 0;
                    var runDirectory = Path.GetDirectoryName(path)!;
                    subjects.TryGetValue(subjectId, out var subject);
                    RepositoryContext? context = null;
                    var contextPath = Path.Combine(runDirectory, "input", "common-context.json");
                    if (File.Exists(contextPath))
                        context = JsonSerializer.Deserialize<RepositoryContext>(
                            await File.ReadAllTextAsync(contextPath),
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    var input = approach == "DIRECT"
                        ? JsonInt(root, "inputTokens")
                        : SumNullable(JsonInt(root, "plannerInputTokens"), JsonInt(root, "generatorInputTokens"));
                    var output = approach == "DIRECT"
                        ? JsonInt(root, "outputTokens")
                        : SumNullable(JsonInt(root, "plannerOutputTokens"), JsonInt(root, "generatorOutputTokens"));
                    var total = JsonInt(root, "totalTokens");
                    var seconds = JsonDurationSeconds(root);
                    var planPath = Path.Combine(runDirectory, "planner", "scenario-plan.json");
                    var planEvidence = ReadLivePlanEvidence(runDirectory, approach);
                    var junit = ReadLiveJunitEvidence(runDirectory);
                    var passedTests = junit is null
                        ? 0
                        : Math.Max(0, junit.Value.Tests - junit.Value.Failures - junit.Value.Errors - junit.Value.Skipped);
                    double? passRate = junit is { Tests: > 0 }
                        ? (double)passedTests / junit.Value.Tests
                        : status == "INVALID" ? 0 : null;
                    var scenarioCount = JsonInt(root, "scenarioCount");
                    var mappingValid = JsonBool(root, "bijectionValid");
                    double? scenarioValidity = approach != "PLANNED"
                        ? null
                        : junit is { Tests: > 0 } && scenarioCount is > 0 && mappingValid == true
                            ? Math.Min(1d, (double)passedTests / scenarioCount.Value)
                            : status == "INVALID" ? 0 : null;
                    var testDirectory = approach == "PLANNED"
                        ? Path.Combine(runDirectory, "generator")
                        : Path.Combine(runDirectory, "generation");
                    var testFiles = Directory.Exists(testDirectory)
                        ? Directory.EnumerateFiles(testDirectory, "*.java", SearchOption.TopDirectoryOnly)
                            .OrderBy(value => value, StringComparer.Ordinal).ToList()
                        : [];
                    Metrics.Add(new ResultMetricRow(
                        subjectId, approach, repetition, status,
                        await ReadFailureAsync(runDirectory),
                        context?.FocalClass ?? subject?.FocalClass ?? "UNKNOWN",
                        context?.FocalMethodSignature ?? subject?.FocalMethodSignature ?? "UNKNOWN",
                        "PENDING", "PENDING", "N/A", "N/A", "N/A",
                        FormatRatio(planEvidence.Fidelity), FormatRatio(planEvidence.ObligationCoverage),
                        FormatRatio(scenarioValidity), FormatRatio(passRate),
                        input, output, total, FormatDuration(seconds),
                        runDirectory, planPath, testFiles));
                    var passed = passRate >= 1.0;
                    live.Add((approach, subjectId, repetition, input, output, total, seconds, planEvidence.Fidelity,
                        planEvidence.ObligationCoverage, scenarioValidity, passRate, passed));
                }
                catch (Exception exception) when (exception is IOException or JsonException)
                {
                    // A just-committed artifact can briefly be unavailable to the reader.
                    // The next live refresh will pick it up without mutating pipeline state.
                }
            }
        }

        // Paired conditional mean for live: (subjectId, repetition) pairs where ALL approaches passed.
        var liveAllApproaches = live.Select(r => r.Approach).Distinct().ToList();
        var liveBothPassedKeys = live
            .Where(r => r.Passed)
            .Select(r => (r.SubjectId, r.Repetition))
            .Where(k => liveAllApproaches.All(a => live.Any(r => r.Approach == a && r.SubjectId == k.SubjectId && r.Repetition == k.Repetition && r.Passed)))
            .ToHashSet();

        foreach (var group in live.GroupBy(item => item.Approach, StringComparer.Ordinal)
                     .OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var totals = group.Where(item => item.Total.HasValue).Select(item => item.Total!.Value).ToList();
            Means.Add(new ResultMeanRow(
                group.Key, group.Count(), "N/A", "N/A", "N/A", "N/A", "N/A",
                FormatRatio(MeanNullable(group.Select(item => item.Fidelity))),
                FormatRatio(MeanNullable(group.Select(item => item.ObligationCoverage))),
                FormatRatio(MeanNullable(group.Select(item => item.ScenarioValidity))),
                FormatRatio(MeanNullable(group.Select(item => item.PassRate))),
                FormatDuration(group.Average(item => item.Seconds)),
                totals.Count == 0 ? "N/A" : totals.Average().ToString("N0"),
                FormatTokenTotal(SumTokenValues(group.Select(item => item.Input))),
                FormatTokenTotal(SumTokenValues(group.Select(item => item.Output))),
                FormatTokenTotal(SumTokenValues(group.Select(item => item.Total))),
                totals.Count, 0));

            // Paired conditional: only (subjectId, repetition) pairs where both approaches passed.
            var pairedItems = group.Where(item => liveBothPassedKeys.Contains((item.SubjectId, item.Repetition))).ToList();
            var condTotals = pairedItems.Where(item => item.Total.HasValue).Select(item => item.Total!.Value).ToList();
            var condFidelity = pairedItems.Select(item => item.Fidelity).Where(v => v.HasValue).Select(v => v!.Value);
            var condObligation = pairedItems.Select(item => item.ObligationCoverage).Where(v => v.HasValue).Select(v => v!.Value);
            var condValidity = pairedItems.Select(item => item.ScenarioValidity).Where(v => v.HasValue).Select(v => v!.Value);
            var condPassRate = pairedItems.Select(item => item.PassRate).Where(v => v.HasValue).Select(v => v!.Value);

            ConditionalMeans.Add(new ResultMeanRow(
                group.Key, pairedItems.Count, "N/A", "N/A", "N/A", "N/A", "N/A",
                FormatRatio(condFidelity.Any() ? condFidelity.Average() : (double?)null),
                FormatRatio(condObligation.Any() ? condObligation.Average() : (double?)null),
                FormatRatio(condValidity.Any() ? condValidity.Average() : (double?)null),
                FormatRatio(condPassRate.Any() ? condPassRate.Average() : (double?)null),
                pairedItems.Count == 0 ? "N/A" : FormatDuration(pairedItems.Average(item => item.Seconds)),
                condTotals.Count == 0 ? "N/A" : condTotals.Average().ToString("N0"),
                FormatTokenTotal(SumTokenValues(pairedItems.Select(item => item.Input))),
                FormatTokenTotal(SumTokenValues(pairedItems.Select(item => item.Output))),
                FormatTokenTotal(SumTokenValues(pairedItems.Select(item => item.Total))),
                condTotals.Count, 0));
        }
        Summary = $"LIVE snapshot: {live.Count} completed generation run(s). "
                  + "Auto-refresh every 5 seconds; the in-flight request appears after run.json is committed.";
    }

    private async Task LoadPreGenerationResultsAsync()
    {
        Metrics.Clear();
        Means.Clear();
        ConditionalMeans.Clear();
        var subjects = await LoadSubjectsAsync();
        var reviewPath = Path.Combine(_request.ExperimentOutputRoot, "subjects", "manual-review.csv");
        var reviewStatuses = await LoadManualReviewStatusesAsync(reviewPath);
        foreach (var subject in subjects.Values
                     .OrderBy(item => item.Split, StringComparer.Ordinal)
                     .ThenBy(item => item.SubjectId, StringComparer.Ordinal))
        {
            var reviewStatus = reviewStatuses.GetValueOrDefault(subject.SubjectId, "PENDING");
            Metrics.Add(new ResultMetricRow(
                subject.SubjectId,
                subject.Split,
                1,
                reviewStatus == "APPROVED" ? "APPROVED_NOT_GENERATED" : reviewStatus + "_REVIEW",
                string.Empty,
                subject.FocalClass,
                subject.FocalMethodSignature,
                "N/A", "N/A", "N/A", "N/A", "N/A", "N/A", "N/A", "N/A", "N/A",
                null, null, null, "N/A",
                string.Empty, string.Empty, [], true));
        }

        var approved = reviewStatuses.Values.Count(value => value == "APPROVED");
        var rejected = reviewStatuses.Values.Count(value => value is "REJECTED" or "EXCLUDED");
        var pending = Math.Max(0, subjects.Count - approved - rejected);
        DetailTitle = "Pre-generation result · Manual review required";
        DetailContent =
            "Pipeline chưa chạy DIRECT/PLANNED nên chưa có run.json, generated tests hoặc metrics.\n\n"
            + $"Manual review: approved={approved}, pending={pending}, rejected={rejected}.\n"
            + $"Review file: {reviewPath}\n\n"
            + "Cập nhật cột review_status thành APPROVED hoặc REJECTED, lưu CSV, "
            + "sau đó quay lại dashboard và nhấn Resume.";
        Summary = $"Pre-generation: {subjects.Count} focal method(s); approved={approved}, "
                  + $"pending={pending}, rejected={rejected}. No DIRECT/PLANNED artifacts yet.";
    }

    public async Task SetReviewStatusAsync(
        IEnumerable<ResultMetricRow> selectedRows,
        string requestedStatus)
    {
        var status = requestedStatus.Trim().ToUpperInvariant();
        if (status is not ("APPROVED" or "REJECTED"))
            throw new ArgumentOutOfRangeException(nameof(requestedStatus), "Review status must be APPROVED or REJECTED.");

        var subjectIds = selectedRows
            .Where(row => row.CanReview)
            .Select(row => row.SubjectId)
            .ToHashSet(StringComparer.Ordinal);
        if (subjectIds.Count == 0)
        {
            Summary = "Select at least one pre-generation focal-method row to review.";
            return;
        }

        var reviewPath = Path.Combine(_request.ExperimentOutputRoot, "subjects", "manual-review.csv");
        if (!File.Exists(reviewPath))
            throw new FileNotFoundException("Manual review ledger was not found.", reviewPath);

        var lines = await File.ReadAllLinesAsync(reviewPath);
        if (lines.Length == 0) throw new InvalidDataException("Manual review ledger is empty.");
        var table = lines.Select(ParseCsvLine).ToList();
        var updated = 0;
        foreach (var fields in table.Skip(1))
        {
            if (fields.Count < 9 || !subjectIds.Contains(fields[0])) continue;
            while (fields.Count < 11) fields.Add(string.Empty);
            fields[8] = status;
            fields[9] = "WPF";
            updated++;
        }
        if (updated != subjectIds.Count)
            throw new InvalidDataException(
                $"Only {updated}/{subjectIds.Count} selected subject IDs exist in manual-review.csv; no partial update was saved.");

        var content = string.Join(Environment.NewLine,
            table.Select(fields => string.Join(',', fields.Select(Csv)))) + Environment.NewLine;
        var temporaryPath = reviewPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, content);
        File.Move(temporaryPath, reviewPath, overwrite: true);

        await LoadPreGenerationResultsAsync();
        Summary += $" Updated {updated} row(s) to {status} from WPF.";
    }

    public async Task ResetFailedRunsAsync(bool onlyTokenFailures = false)
    {
        var outputRoot = _request.ExperimentOutputRoot;
        var report = await _runManagementService.ResetFailedRunsAsync(
            new ResetRunsRequest(outputRoot, _request.ExperimentId, OnlyTokenFailures: onlyTokenFailures));
        Summary = $"Đã reset {report.ResetRunCount} run(s) bị lỗi. Hãy quay lại Dashboard và nhấn 'Retry failed runs' hoặc 'Resume' để chạy lại với budget mới.";
        await RebuildAsync();
    }

    public async Task ResetSelectedRowsAsync(IEnumerable<ResultMetricRow> selectedRows)
    {
        var rows = selectedRows.Where(r => !string.IsNullOrWhiteSpace(r.RunDirectory)).ToList();
        if (rows.Count == 0)
        {
            Summary = "Chọn ít nhất một run để reset.";
            return;
        }

        var count = 0;
        foreach (var row in rows)
        {
            if (await _runManagementService.ResetSpecificRunAsync(row.RunDirectory))
                count++;
        }

        Summary = $"Đã reset {count} run đã chọn. Nhấn 'Retry failed runs' hoặc 'Resume' trên Dashboard để chạy lại.";
        await RebuildAsync();
    }

    public async Task ResetSingleRowAsync(ResultMetricRow row)
    {
        if (string.IsNullOrWhiteSpace(row.RunDirectory)) return;
        await _runManagementService.ResetSpecificRunAsync(row.RunDirectory);
        Summary = $"Đã reset {row.SubjectId} {row.Approach} R{row.Repetition:00}. Nhấn 'Retry failed runs' hoặc 'Resume' trên Dashboard.";
        await RebuildAsync();
    }

    public async Task ShowPlanAsync(ResultMetricRow row)
    {
        DetailTitle = $"Plan · {row.SubjectId} · {row.Approach} · R{row.Repetition:00}";
        DetailContent = File.Exists(row.PlanPath)
            ? await File.ReadAllTextAsync(row.PlanPath)
            : row.Approach.Equals("DIRECT", StringComparison.OrdinalIgnoreCase)
                ? "DIRECT baseline không có planning stage, vì vậy run này không có scenario-plan.json."
                : "Không tìm thấy scenario-plan.json. Kiểm tra generation status và run directory:\n" + row.RunDirectory;
    }

    public async Task ShowTestsAsync(ResultMetricRow row)
    {
        DetailTitle = $"Generated tests · {row.SubjectId} · {row.Approach} · R{row.Repetition:00}";
        if (row.TestFiles.Count == 0)
        {
            DetailContent = "Không có Java test artifact. Run có thể đã fail ở generation.\n" + row.RunDirectory;
            return;
        }
        var sections = new List<string>();
        foreach (var path in row.TestFiles)
            sections.Add($"// ===== {Path.GetFileName(path)} =====\n" + await File.ReadAllTextAsync(path));
        DetailContent = string.Join("\n\n", sections);
    }

    public async Task ShowLogsAsync(ResultMetricRow row)
    {
        DetailTitle = $"Run logs · {row.SubjectId} · {row.Approach} · R{row.Repetition:00}";
        var files = CollectDiagnosticFiles(row.RunDirectory);
        if (files.Count == 0)
        {
            DetailContent = "Không tìm thấy log/evidence cho run này.\n" + row.RunDirectory;
            return;
        }

        const int maxCharactersPerFile = 200_000;
        var sections = new List<string>();
        foreach (var path in files)
        {
            var content = await File.ReadAllTextAsync(path);
            if (content.Length > maxCharactersPerFile)
                content = content[..maxCharactersPerFile]
                          + $"\n... [truncated in viewer; full file retained at {path}]";
            sections.Add($"===== {Path.GetRelativePath(row.RunDirectory, path)} =====\n{content}");
        }
        DetailContent = string.Join("\n\n", sections);
    }

    private static IReadOnlyList<string> CollectDiagnosticFiles(string runDirectory)
    {
        if (!Directory.Exists(runDirectory)) return [];
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string path) { if (File.Exists(path)) result.Add(path); }

        Add(Path.Combine(runDirectory, "run.json"));
        var tools = Path.Combine(runDirectory, "tools");
        if (Directory.Exists(tools))
        {
            foreach (var path in Directory.EnumerateFiles(tools, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(path);
                if (path.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
                    || name is "tool-run.json" or "process.json" or "junit-summary.json"
                        or "focal-coverage.json" or "focal-mutations.json")
                    result.Add(path);
            }
        }

        // Generation failures have no tool process. Retain the raw model output
        // in the same viewer so INVALID_JSON/schema failures remain diagnosable.
        foreach (var stage in new[] { "generation", "planner", "generator" })
        {
            Add(Path.Combine(runDirectory, stage, "assistant-content.raw.txt"));
            Add(Path.Combine(runDirectory, stage, "provider-response.raw.json"));
        }
        return result.OrderBy(path => Path.GetRelativePath(runDirectory, path), StringComparer.Ordinal).ToList();
    }

    private async Task<Dictionary<string, FocalMethodSubject>> LoadSubjectsAsync()
    {
        var result = new Dictionary<string, FocalMethodSubject>(StringComparer.Ordinal);
        var splits = _request.Mode.Equals("Smoke", StringComparison.OrdinalIgnoreCase)
            ? new[] { "smoke" }
            // Subject selection reviews and freezes the disjoint Pilot and
            // Official manifests together. Do not leak a stale Smoke subject
            // into this 5 + 150 manual-review gate.
            : new[] { "pilot", "official" };
        foreach (var split in splits)
        {
            var path = Path.Combine(_request.ExperimentOutputRoot, "subjects", $"subjects-{split}.jsonl");
            if (!File.Exists(path)) continue;
            foreach (var line in await File.ReadAllLinesAsync(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var subject = JsonSerializer.Deserialize<FocalMethodSubject>(line,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (subject is not null) result[subject.SubjectId] = subject;
            }
        }
        return result;
    }

    private static async Task<Dictionary<string, string>> LoadManualReviewStatusesAsync(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return result;
        var lines = await File.ReadAllLinesAsync(path);
        foreach (var line in lines.Skip(1).Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            var fields = ParseCsvLine(line);
            if (fields.Count < 9 || string.IsNullOrWhiteSpace(fields[0])) continue;
            result[fields[0]] = fields[8].Trim().ToUpperInvariant();
        }
        return result;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    current.Append('"');
                    index++;
                }
                else quoted = !quoted;
            }
            else if (character == ',' && !quoted)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else current.Append(character);
        }
        fields.Add(current.ToString());
        return fields;
    }

    private static string Csv(string value)
    {
        if (!value.Contains(',') && !value.Contains('"')
                                 && !value.Contains('\r') && !value.Contains('\n')) return value;
        return '"' + value.Replace("\"", "\"\"") + '"';
    }

    private static string FormatBool(bool? value) => value is null ? "N/A" : value.Value ? "PASS" : "FAIL";
    private static string FormatRatio(double? value) => value is null ? "N/A" : value.Value.ToString("P2");
    private static string FormatTokenTotal(long? value) => value is null ? "N/A" : value.Value.ToString("N0");
    private static int? SumNullable(int? first, int? second) =>
        first is null && second is null ? null : (first ?? 0) + (second ?? 0);
    private static long? SumTokenValues(IEnumerable<int?> values)
    {
        var present = values.Where(value => value.HasValue).Select(value => (long)value!.Value).ToList();
        return present.Count == 0 ? null : present.Sum();
    }
    private static string JsonString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty : string.Empty;
    private static int? JsonInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;
    private static bool? JsonBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : null;
    private static double? MeanNullable(IEnumerable<double?> values)
    {
        var present = values.Where(value => value.HasValue).Select(value => value!.Value).ToList();
        return present.Count == 0 ? null : present.Average();
    }
    private static LivePlanEvidence ReadLivePlanEvidence(string runDirectory, string approach)
    {
        if (approach != "PLANNED") return new LivePlanEvidence(null, null);
        var planPath = Path.Combine(runDirectory, "planner", "scenario-plan.json");
        var inventoryPath = Path.Combine(runDirectory, "input", "structural-target-inventory.json");
        if (!File.Exists(planPath) || !File.Exists(inventoryPath)) return new LivePlanEvidence(0, 0);
        try
        {
            var plan = JsonSerializer.Deserialize<ScenarioPlan>(File.ReadAllText(planPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var inventory = JsonSerializer.Deserialize<StructuralTargetInventory>(File.ReadAllText(inventoryPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (plan is null || inventory is null || plan.Scenarios.Count == 0 || inventory.Targets.Count == 0)
                return new LivePlanEvidence(0, 0);
            var targets = inventory.Targets.ToDictionary(target => target.TargetId, StringComparer.Ordinal);
            var links = plan.Scenarios
                .SelectMany(scenario => scenario.TargetIds.Select(targetId => (Scenario: scenario, TargetId: targetId)))
                .ToList();
            var faithful = links.Count(link => targets.TryGetValue(link.TargetId, out var target)
                                                       && link.Scenario.OracleProvenance.SourceLines.Contains(target.Line));
            var covered = links.Select(link => link.TargetId).Where(targets.ContainsKey)
                .Distinct(StringComparer.Ordinal).Count();
            return new LivePlanEvidence(
                links.Count == 0 ? 0 : (double)faithful / links.Count,
                (double)covered / inventory.Targets.Count);
        }
        catch (JsonException)
        {
            return new LivePlanEvidence(0, 0);
        }
    }
    private static LiveJunitEvidence? ReadLiveJunitEvidence(string runDirectory)
    {
        var path = Path.Combine(runDirectory, "tools", "junit-summary.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            return new LiveJunitEvidence(JsonInt(root, "tests") ?? 0, JsonInt(root, "failures") ?? 0,
                JsonInt(root, "errors") ?? 0, JsonInt(root, "skipped") ?? 0);
        }
        catch (JsonException)
        {
            return null;
        }
    }
    private static double JsonDurationSeconds(JsonElement root)
    {
        if (!root.TryGetProperty("startedAt", out var startedValue)
            || !root.TryGetProperty("completedAt", out var completedValue)
            || !startedValue.TryGetDateTimeOffset(out var started)
            || !completedValue.TryGetDateTimeOffset(out var completed)
            || completed < started)
            return 0;
        return (completed - started).TotalSeconds;
    }
    private static string FormatDuration(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return "N/A";
        var duration = TimeSpan.FromSeconds(Math.Round(seconds));
        return $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
    }

    private static async Task<string> ReadFailureAsync(string runDirectory)
    {
        var path = Path.Combine(runDirectory, "run.json");
        try
        {
            if (File.Exists(path))
            {
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
                var root = document.RootElement;
                var stage = root.TryGetProperty("failureStage", out var stageValue)
                    ? stageValue.GetString() : null;
                var code = root.TryGetProperty("failureCode", out var codeValue)
                    ? codeValue.GetString() : null;
                var finish = root.TryGetProperty("finishReason", out var finishValue)
                    ? finishValue.GetString() : null;
                var generationFailure = string.Join(": ", new[] { stage, code }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
                if (!string.IsNullOrWhiteSpace(generationFailure))
                    return string.IsNullOrWhiteSpace(finish)
                        ? generationFailure : $"{generationFailure} ({finish})";
            }

            var toolPath = Path.Combine(runDirectory, "tools", "tool-run.json");
            if (!File.Exists(toolPath)) return string.Empty;
            using var toolDocument = JsonDocument.Parse(await File.ReadAllTextAsync(toolPath));
            var toolRoot = toolDocument.RootElement;
            var gate = toolRoot.TryGetProperty("failureGate", out var gateValue)
                ? gateValue.GetString() : null;
            var reason = toolRoot.TryGetProperty("failureReason", out var reasonValue)
                ? reasonValue.GetString() : null;
            return string.Join(": ", new[] { gate, reason }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        }
        catch
        {
            return "UNREADABLE_RUN_METADATA";
        }
    }

    private readonly record struct LivePlanEvidence(double? Fidelity, double? ObligationCoverage);
    private readonly record struct LiveJunitEvidence(int Tests, int Failures, int Errors, int Skipped);
}

public sealed record ResultMetricRow(
    string SubjectId,
    string Approach,
    int Repetition,
    string Status,
    string Failure,
    string FocalClass,
    string FocalMethod,
    string Compile,
    string Execution,
    string TargetSatisfaction,
    string BranchCoverage,
    string MutationScore,
    string PlanFidelity,
    string ObligationCoverage,
    string ScenarioValidity,
    string PassRate,
    int? InputTokens,
    int? OutputTokens,
    int? TotalTokens,
    string TotalTime,
    string RunDirectory,
    string PlanPath,
    IReadOnlyList<string> TestFiles,
    bool CanReview = false)
{
    public bool HasPlan => File.Exists(PlanPath);
    public bool HasTests => TestFiles.Count > 0;
    public bool HasLogs => Directory.Exists(RunDirectory);
    public bool CanRerun => !CanReview && (!string.IsNullOrWhiteSpace(Failure) || Status != "COMPLETED" || Compile == "FAIL" || Execution == "FAIL");
}

public sealed record ResultMeanRow(
    string Approach,
    int RunCount,
    string CompilationSuccessRate,
    string ExecutionSuccessRate,
    string MeanTargetSatisfaction,
    string MeanBranchCoverage,
    string MeanMutationScore,
    string MeanPlanFidelity,
    string MeanObligationCoverage,
    string MeanScenarioValidity,
    string MeanPassRate,
    string MeanTotalTime,
    string MeanReportedTokens,
    string TotalInputTokens,
    string TotalOutputTokens,
    string TotalReportedTokens,
    int TokenSampleCount,
    int ToolErrorCount);
