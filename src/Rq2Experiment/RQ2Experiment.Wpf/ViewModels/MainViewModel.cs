using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using RQ2Experiment.Core.Abstractions;
using RQ2Experiment.Core.Domain;
using RQ2Experiment.Core.Engine;
using RQ2Experiment.Infrastructure.Benchmark;
using RQ2Experiment.Infrastructure.Diagnostics;
using RQ2Experiment.Infrastructure.LLM;
using RQ2Experiment.Infrastructure.OpenAI;
using RQ2Experiment.Infrastructure.Persistence;
using RQ2Experiment.Infrastructure.Real;
using RQ2Experiment.Wpf.Commands;

namespace RQ2Experiment.Wpf.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private string repositoryPath = string.Empty;
    private string benchmarkManifestPath = ResolveDefaultPath(
        Path.Combine("..", "..", "datasets", "rq2", "benchmark.ready-100.json"),
        ResolveDefaultPath(
            Path.Combine("..", "..", "datasets", "rq2", "benchmark.postdevelopment-eval-100.json"),
            Path.Combine(Environment.CurrentDirectory, "benchmark.sample.json")));
    private string outputDirectory = ResolveDefaultPath(
        Path.Combine("outputs", "rq2-libro-ghrb-pilot"),
        Path.Combine(Environment.CurrentDirectory, "experiment-output"),
        requireExistingTarget: false);
    private LlmProviderPreset selectedProvider = LlmProviderCatalog.All[1];
    private string apiBaseUrl = LlmProviderCatalog.All[1].BaseUrl;
    private LlmApiProtocol apiProtocol = LlmProviderCatalog.All[1].Protocol;
    private bool useJsonMode = LlmProviderCatalog.All[1].UseJsonMode;
    private ApiKeyEntryViewModel? selectedApiKey;
    private string modelName = string.Empty;
    private string reasoningEffort = "medium";
    private int maxIterations = 4;
    private int tokenBudget = 80_000;
    private int repetitions = 3;
    private int maxParallelRuns = 2;
    private int minimumLlmRequestIntervalSeconds = 5;
    private int scheduleSeed = 20260814;
    private string batchId = string.Empty;
    private bool resumeExistingBatch = true;
    private bool retryInfrastructureFailures;
    private bool isConfirmatoryBatch;
    private string batchEstimate = "Chưa lập dự toán batch. Dự toán không gọi LLM và không dùng token.";
    private bool isTgslrSelected = true;
    private bool isFtmrSelected = true;
    private bool isFcrSelected = true;
    private bool isRunning;
    private bool isLoadingModels;
    private int nextApiKeySequence = 1;
    private string statusText = "Chọn nhà cung cấp, nhập API key và model, sau đó chọn benchmark manifest.";
    private string apiConnectionStatus = "Chưa kiểm tra kết nối. Bạn có thể tải model hoặc nhập model ID thủ công.";
    private string datasetReadinessStatus =
        "Hãy tiền kiểm dataset trước khi chạy. Manifest chỉ được chấp nhận khi mọi task đã chọn đều READY.";
    private string logText = string.Empty;
    private string runSummary = "Chưa có lượt chạy";
    private int experimentProgressTotal;
    private int experimentProgressCompleted;
    private string experimentProgressText = "Chưa lập kế hoạch chạy";
    private int statisticsTotalRuns;
    private double statisticsSuccessRate;
    private double statisticsAnalysisSuccessRate;
    private int statisticsInfrastructureFailures;
    private double? statisticsMeasurementCompletionRate;
    private double? statisticsTokenAccountingCompletionRate;
    private int statisticsTotalTokens;
    private double statisticsAverageIterations;
    private int statisticsTotalRegressions;
    private double? statisticsAverageMutationScore;
    private string statisticsCaveat = "Chưa có kết quả để thống kê.";
    private string statisticsBatchLabel = "Chưa có batch";
    private string statisticsAnalysisStatus = "Chưa phân tích";
    private string statisticsOverallConclusion = "Chưa có dữ liệu để trả lời RQ2.";
    private string statisticsClaimStatus = "Claim gate chưa được đánh giá";
    private string statisticsClaimDetail = "Chưa có dữ liệu.";
    private string statisticsPowerProjection = "Chưa có power projection.";
    private int statisticsIndependentTasks;
    private int statisticsRequiredIndependentUnits;
    private int statisticsProjects;
    private int statisticsRequiredProjectClusters;
    private CancellationTokenSource? cancellationTokenSource;

    public MainViewModel()
    {
        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsBusy);
        PreflightCommand = new AsyncRelayCommand(PreflightAsync, () => !IsBusy);
        EstimateBatchCommand = new AsyncRelayCommand(EstimateBatchAsync, () => !IsBusy);
        LoadModelsCommand = new AsyncRelayCommand(LoadModelsAsync, () => !IsBusy);
        CancelCommand = new RelayCommand(Cancel, () => IsRunning);
        ClearCommand = new RelayCommand(Clear, () => !IsBusy);
        LoadResultsCommand = new AsyncRelayCommand(LoadExistingResultsAsync, () => !IsBusy);
    }

    private static string ResolveDefaultPath(
        string workspaceRelativePath,
        string fallback,
        bool requireExistingTarget = true)
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(Path.GetFullPath(start));
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, workspaceRelativePath);
                var exists = File.Exists(candidate) || Directory.Exists(candidate);
                if (!requireExistingTarget || exists)
                {
                    var marker = Path.Combine(directory.FullName, "RQ2Experiment.sln");
                    if (File.Exists(marker)) return candidate;
                }

                directory = directory.Parent;
            }
        }

        return fallback;
    }

    public ObservableCollection<RunRowViewModel> Runs { get; } = [];
    public ObservableCollection<string> AvailableModels { get; } = [];
    public ObservableCollection<ApiKeyEntryViewModel> ApiKeys { get; } = [];
    public ObservableCollection<TreatmentStatisticsViewModel> TreatmentStatistics { get; } = [];
    public ObservableCollection<Rq2PairwiseComparison> PairwiseComparisons { get; } = [];
    public ObservableCollection<Rq2OutcomeEffect> OutcomeEffects { get; } = [];
    public ObservableCollection<StageFunnelStatisticsViewModel> StageFunnelStatistics { get; } = [];
    public IReadOnlyList<LlmProviderPreset> Providers => LlmProviderCatalog.All;
    public IReadOnlyList<LlmApiProtocol> ApiProtocols { get; } = Enum.GetValues<LlmApiProtocol>();
    public IReadOnlyList<string> ReasoningEfforts { get; } = ["none", "low", "medium", "high", "xhigh", "max"];
    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand PreflightCommand { get; }
    public AsyncRelayCommand EstimateBatchCommand { get; }
    public AsyncRelayCommand LoadModelsCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ClearCommand { get; }
    public AsyncRelayCommand LoadResultsCommand { get; }

    public string RepositoryPath { get => repositoryPath; set => SetProperty(ref repositoryPath, value); }
    public string BenchmarkManifestPath { get => benchmarkManifestPath; set => SetProperty(ref benchmarkManifestPath, value); }
    public string OutputDirectory { get => outputDirectory; set => SetProperty(ref outputDirectory, value); }

    public LlmProviderPreset SelectedProvider
    {
        get => selectedProvider;
        set
        {
            if (value is null || !SetProperty(ref selectedProvider, value)) return;
            apiBaseUrl = value.BaseUrl;
            apiProtocol = value.Protocol;
            useJsonMode = value.UseJsonMode;
            modelName = value.DefaultModel;
            ClearApiKeys();
            AvailableModels.Clear();
            OnPropertyChanged(nameof(ApiBaseUrl));
            OnPropertyChanged(nameof(ApiProtocol));
            OnPropertyChanged(nameof(UseJsonMode));
            OnPropertyChanged(nameof(ModelName));
            OnPropertyChanged(nameof(ApiKeyLabel));
            OnPropertyChanged(nameof(IsResponsesProtocol));
            OnPropertyChanged(nameof(ModeBadgeText));
            OnPropertyChanged(nameof(ConfigurationHint));
            ApiConnectionStatus = $"Đã chọn {value.DisplayName}. Có thể tải model hoặc nhập model ID thủ công.";
        }
    }

    public string ApiBaseUrl
    {
        get => apiBaseUrl;
        set
        {
            if (!SetProperty(ref apiBaseUrl, value)) return;
            AvailableModels.Clear();
            ApiConnectionStatus = "API URL đã thay đổi; hãy tải lại danh sách model hoặc nhập model ID thủ công.";
        }
    }

    public LlmApiProtocol ApiProtocol
    {
        get => apiProtocol;
        set
        {
            if (!SetProperty(ref apiProtocol, value)) return;
            OnPropertyChanged(nameof(IsResponsesProtocol));
            ApiConnectionStatus = "Giao thức đã thay đổi. Hãy kiểm tra endpoint trước khi chạy.";
        }
    }

    public bool UseJsonMode { get => useJsonMode; set => SetProperty(ref useJsonMode, value); }
    public bool IsResponsesProtocol => ApiProtocol == LlmApiProtocol.Responses;
    public string ApiKeyLabel => SelectedProvider.RequiresApiKey ? "Thêm API key (bắt buộc)" : "Thêm API key (tùy chọn)";
    public ApiKeyEntryViewModel? SelectedApiKey
    {
        get => selectedApiKey;
        set
        {
            if (!SetProperty(ref selectedApiKey, value)) return;
            AvailableModels.Clear();
            if (value is not null)
            {
                ApiConnectionStatus = $"Đã chọn {value.DisplayText}. Hãy tải model để xác thực key này.";
            }
        }
    }
    public string ModelName { get => modelName; set => SetProperty(ref modelName, value); }
    public string ReasoningEffort { get => reasoningEffort; set => SetProperty(ref reasoningEffort, value); }
    public int MaxIterations { get => maxIterations; set => SetProperty(ref maxIterations, value); }
    public int TokenBudget { get => tokenBudget; set => SetProperty(ref tokenBudget, value); }
    public int Repetitions { get => repetitions; set => SetProperty(ref repetitions, value); }
    public int MaxParallelRuns { get => maxParallelRuns; set => SetProperty(ref maxParallelRuns, value); }
    public int MinimumLlmRequestIntervalSeconds
    {
        get => minimumLlmRequestIntervalSeconds;
        set => SetProperty(ref minimumLlmRequestIntervalSeconds, value);
    }
    public int ScheduleSeed { get => scheduleSeed; set => SetProperty(ref scheduleSeed, value); }
    public string BatchId { get => batchId; set => SetProperty(ref batchId, value); }
    public bool ResumeExistingBatch { get => resumeExistingBatch; set => SetProperty(ref resumeExistingBatch, value); }
    public bool RetryInfrastructureFailures { get => retryInfrastructureFailures; set => SetProperty(ref retryInfrastructureFailures, value); }
    public bool IsConfirmatoryBatch { get => isConfirmatoryBatch; set => SetProperty(ref isConfirmatoryBatch, value); }
    public string BatchEstimate { get => batchEstimate; private set => SetProperty(ref batchEstimate, value); }
    public bool IsTgslrSelected { get => isTgslrSelected; set => SetProperty(ref isTgslrSelected, value); }
    public bool IsFtmrSelected { get => isFtmrSelected; set => SetProperty(ref isFtmrSelected, value); }
    public bool IsFcrSelected { get => isFcrSelected; set => SetProperty(ref isFcrSelected, value); }
    public string StatusText { get => statusText; private set => SetProperty(ref statusText, value); }
    public string ApiConnectionStatus { get => apiConnectionStatus; private set => SetProperty(ref apiConnectionStatus, value); }
    public string DatasetReadinessStatus { get => datasetReadinessStatus; private set => SetProperty(ref datasetReadinessStatus, value); }
    public string LogText { get => logText; private set => SetProperty(ref logText, value); }
    public string RunSummary { get => runSummary; private set => SetProperty(ref runSummary, value); }
    public int ExperimentProgressTotal { get => experimentProgressTotal; private set => SetProperty(ref experimentProgressTotal, value); }
    public int ExperimentProgressCompleted { get => experimentProgressCompleted; private set => SetProperty(ref experimentProgressCompleted, value); }
    public string ExperimentProgressText { get => experimentProgressText; private set => SetProperty(ref experimentProgressText, value); }
    public int StatisticsTotalRuns { get => statisticsTotalRuns; private set => SetProperty(ref statisticsTotalRuns, value); }
    public double StatisticsSuccessRate { get => statisticsSuccessRate; private set => SetProperty(ref statisticsSuccessRate, value); }
    public double StatisticsAnalysisSuccessRate { get => statisticsAnalysisSuccessRate; private set => SetProperty(ref statisticsAnalysisSuccessRate, value); }
    public int StatisticsInfrastructureFailures { get => statisticsInfrastructureFailures; private set => SetProperty(ref statisticsInfrastructureFailures, value); }
    public double? StatisticsMeasurementCompletionRate
    {
        get => statisticsMeasurementCompletionRate;
        private set
        {
            if (SetProperty(ref statisticsMeasurementCompletionRate, value))
            {
                OnPropertyChanged(nameof(StatisticsMeasurementCompletionDisplay));
            }
        }
    }
    public string StatisticsMeasurementCompletionDisplay => StatisticsMeasurementCompletionRate.HasValue
        ? $"{StatisticsMeasurementCompletionRate.Value:P1}"
        : "N/A";
    public double? StatisticsTokenAccountingCompletionRate
    {
        get => statisticsTokenAccountingCompletionRate;
        private set
        {
            if (SetProperty(ref statisticsTokenAccountingCompletionRate, value))
            {
                OnPropertyChanged(nameof(StatisticsTokenAccountingCompletionDisplay));
            }
        }
    }
    public string StatisticsTokenAccountingCompletionDisplay => StatisticsTokenAccountingCompletionRate.HasValue
        ? $"{StatisticsTokenAccountingCompletionRate.Value:P1}"
        : "N/A";
    public int StatisticsTotalTokens { get => statisticsTotalTokens; private set => SetProperty(ref statisticsTotalTokens, value); }
    public double StatisticsAverageIterations { get => statisticsAverageIterations; private set => SetProperty(ref statisticsAverageIterations, value); }
    public int StatisticsTotalRegressions { get => statisticsTotalRegressions; private set => SetProperty(ref statisticsTotalRegressions, value); }
    public double? StatisticsAverageMutationScore
    {
        get => statisticsAverageMutationScore;
        private set
        {
            if (SetProperty(ref statisticsAverageMutationScore, value))
            {
                OnPropertyChanged(nameof(StatisticsAverageMutationScoreDisplay));
            }
        }
    }
    public string StatisticsAverageMutationScoreDisplay => StatisticsAverageMutationScore.HasValue
        ? $"{StatisticsAverageMutationScore.Value:P1}"
        : "N/A";
    public string StatisticsCaveat { get => statisticsCaveat; private set => SetProperty(ref statisticsCaveat, value); }
    public string StatisticsBatchLabel { get => statisticsBatchLabel; private set => SetProperty(ref statisticsBatchLabel, value); }
    public string StatisticsAnalysisStatus { get => statisticsAnalysisStatus; private set => SetProperty(ref statisticsAnalysisStatus, value); }
    public string StatisticsOverallConclusion { get => statisticsOverallConclusion; private set => SetProperty(ref statisticsOverallConclusion, value); }
    public string StatisticsClaimStatus { get => statisticsClaimStatus; private set => SetProperty(ref statisticsClaimStatus, value); }
    public string StatisticsClaimDetail { get => statisticsClaimDetail; private set => SetProperty(ref statisticsClaimDetail, value); }
    public string StatisticsPowerProjection { get => statisticsPowerProjection; private set => SetProperty(ref statisticsPowerProjection, value); }
    public int StatisticsIndependentTasks
    {
        get => statisticsIndependentTasks;
        private set
        {
            if (SetProperty(ref statisticsIndependentTasks, value))
                OnPropertyChanged(nameof(StatisticsIndependentUnitDisplay));
        }
    }
    public int StatisticsRequiredIndependentUnits
    {
        get => statisticsRequiredIndependentUnits;
        private set
        {
            if (SetProperty(ref statisticsRequiredIndependentUnits, value))
                OnPropertyChanged(nameof(StatisticsIndependentUnitDisplay));
        }
    }
    public string StatisticsIndependentUnitDisplay => StatisticsRequiredIndependentUnits > 0
        ? $"{StatisticsIndependentTasks}/{StatisticsRequiredIndependentUnits}"
        : StatisticsIndependentTasks.ToString();
    public int StatisticsProjects { get => statisticsProjects; private set => SetProperty(ref statisticsProjects, value); }
    public int StatisticsRequiredProjectClusters
    {
        get => statisticsRequiredProjectClusters;
        private set
        {
            if (SetProperty(ref statisticsRequiredProjectClusters, value))
                OnPropertyChanged(nameof(StatisticsProjectClusterDisplay));
        }
    }
    public string StatisticsProjectClusterDisplay => StatisticsRequiredProjectClusters > 0
        ? $"{StatisticsProjects}/{StatisticsRequiredProjectClusters}"
        : StatisticsProjects.ToString();
    public string ModeBadgeText => $"REAL EXPERIMENT — {SelectedProvider.DisplayName} + Java tools";
    public string ConfigurationHint =>
        "API key chỉ được giữ trong bộ nhớ. Mỗi run giữ một key; 429 mới xoay key, 503 retry cùng key. " +
        "Chỉ fingerprint và lịch sử retry/failover được ghi để tái lập. " +
        "Treatment được counterbalance theo block task–repetition và seed lịch chạy. " +
        "Reasoning effort chỉ gửi khi dùng Responses với model OpenAI hỗ trợ; Chat Completions sẽ bỏ qua trường này.";

    private bool IsBusy => IsRunning || isLoadingModels;

    private bool IsRunning
    {
        get => isRunning;
        set
        {
            if (!SetProperty(ref isRunning, value)) return;
            RefreshCommands();
        }
    }

    public void AddApiKeys(string value)
    {
        var secrets = value.Split(
                ['\r', '\n', ',', ';'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(secret => !string.IsNullOrWhiteSpace(secret))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (secrets.Length == 0)
        {
            ApiConnectionStatus = "Không tìm thấy API key nào. Hãy nhập mỗi key trên một dòng.";
            return;
        }

        var addedCount = 0;
        var duplicateCount = 0;
        ApiKeyEntryViewModel? lastEntry = null;
        foreach (var secret in secrets)
        {
            var existing = ApiKeys.FirstOrDefault(item =>
                string.Equals(item.Secret, secret, StringComparison.Ordinal));
            if (existing is not null)
            {
                duplicateCount++;
                lastEntry = existing;
                continue;
            }

            lastEntry = new ApiKeyEntryViewModel(secret, nextApiKeySequence++);
            ApiKeys.Add(lastEntry);
            addedCount++;
        }

        SelectedApiKey = lastEntry;
        AvailableModels.Clear();
        ApiConnectionStatus =
            $"Đã thêm {addedCount} key cho {SelectedProvider.DisplayName}; bỏ qua {duplicateCount} key trùng. " +
            $"Tổng cộng {ApiKeys.Count} key, đang chọn {SelectedApiKey?.DisplayText ?? "không có"}.";
    }

    public void RemoveSelectedApiKey()
    {
        if (SelectedApiKey is null) return;
        var index = ApiKeys.IndexOf(SelectedApiKey);
        ApiKeys.Remove(SelectedApiKey);
        SelectedApiKey = ApiKeys.Count == 0 ? null : ApiKeys[Math.Min(index, ApiKeys.Count - 1)];
        AvailableModels.Clear();
        ApiConnectionStatus = ApiKeys.Count == 0
            ? "Không còn API key trong bộ nhớ."
            : $"Đã xóa key; đang chọn {SelectedApiKey!.DisplayText}.";
    }

    public void ClearApiKeys()
    {
        ApiKeys.Clear();
        selectedApiKey = null;
        nextApiKeySequence = 1;
        OnPropertyChanged(nameof(SelectedApiKey));
        AvailableModels.Clear();
        ApiConnectionStatus = "Danh sách API key đã được xóa khỏi bộ nhớ.";
    }

    private string ActiveApiKey => SelectedApiKey?.Secret ?? string.Empty;

    private CompatibleLlmClient CreateValidationClient() => new(
        ActiveApiKey,
        ApiBaseUrl,
        ApiProtocol,
        SelectedProvider.RequiresApiKey,
        UseJsonMode);

    private ILlmRepairClient CreateExperimentClient() => new ResilientLlmClient(
        ApiKeys.Select(entry => entry.Secret).ToArray(),
        ApiBaseUrl,
        ApiProtocol,
        SelectedProvider.RequiresApiKey,
        UseJsonMode,
        new LlmResilienceOptions(
            MinimumRequestInterval: TimeSpan.FromSeconds(Math.Max(0, MinimumLlmRequestIntervalSeconds))));

    private async Task LoadModelsAsync()
    {
        try
        {
            if (SelectedProvider.RequiresApiKey && SelectedApiKey is null)
            {
                throw new InvalidOperationException($"{SelectedProvider.DisplayName} yêu cầu chọn một API key trong danh sách.");
            }

            isLoadingModels = true;
            RefreshCommands();
            ApiConnectionStatus = "Đang xác thực API key và tải danh sách model...";
            using var client = CreateValidationClient();
            await client.ValidateCredentialsAsync(
                SelectedProvider.CredentialValidationPath,
                CancellationToken.None);
            var models = await client.ListTextModelsAsync(CancellationToken.None);
            AvailableModels.Clear();
            foreach (var model in models) AvailableModels.Add(model);
            if (models.Count == 0)
            {
                throw new InvalidOperationException("Endpoint không trả về text model. Hãy nhập model ID thủ công.");
            }

            if (string.IsNullOrWhiteSpace(ModelName) || !models.Contains(ModelName, StringComparer.OrdinalIgnoreCase))
            {
                ModelName = models[0];
            }

            ApiConnectionStatus = SelectedProvider.RequiresApiKey
                ? $"API key đang chọn hợp lệ. Tìm thấy {models.Count} model; pool có {ApiKeys.Count} key; đang chọn {ModelName}."
                : $"Kết nối endpoint thành công. Tìm thấy {models.Count} model; đang chọn {ModelName}.";
        }
        catch (Exception exception)
        {
            ApiConnectionStatus = $"Xác thực hoặc tải model thất bại: {exception.Message}";
        }
        finally
        {
            isLoadingModels = false;
            RefreshCommands();
        }
    }

    private async Task StartAsync()
    {
        ILlmRepairClient? llmClient = null;
        try
        {
            var configuration = BuildConfiguration();
            if (string.IsNullOrWhiteSpace(configuration.BatchId))
            {
                BatchId = $"rq2-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}"[..30];
                configuration = configuration with { BatchId = BatchId };
            }
            configuration.Validate();

            if (SelectedProvider.RequiresApiKey && SelectedApiKey is null)
            {
                throw new InvalidOperationException($"{SelectedProvider.DisplayName} yêu cầu chọn một API key trong danh sách.");
            }

            Runs.Clear();
            LogText = string.Empty;
            IsRunning = true;
            cancellationTokenSource = new CancellationTokenSource();
            StatusText = "Đang tiền kiểm manifest, repository và lịch chạy; chưa gọi LLM...";
            var preflightMessages = await BenchmarkPreflight.RunAsync(
                BenchmarkManifestPath,
                string.IsNullOrWhiteSpace(RepositoryPath) ? null : RepositoryPath,
                configuration,
                cancellationTokenSource.Token);
            foreach (var message in preflightMessages) AppendLog($"PREFLIGHT {message}");
            UpdateDatasetReadiness(preflightMessages);

            var benchmark = await BenchmarkManifestLoader.LoadDefinitionAsync(
                BenchmarkManifestPath,
                string.IsNullOrWhiteSpace(RepositoryPath) ? null : RepositoryPath,
                cancellationTokenSource.Token);
            var studyPlan = StudyDesignPlanner.Assess(benchmark.Tasks, benchmark.StudyDesign);
            var manifestHash = Convert.ToHexString(SHA256.HashData(
                await File.ReadAllBytesAsync(Path.GetFullPath(BenchmarkManifestPath), cancellationTokenSource.Token)));
            configuration = configuration with
            {
                StudyPlan = studyPlan.Evidence,
                RunPlanFingerprint = BatchResumePlanner.CreateFingerprint(benchmark.Tasks, configuration, manifestHash)
            };
            if (IsConfirmatoryBatch)
            {
                StudyDesignPlanner.EnsureConfirmatoryReady(benchmark.Tasks, studyPlan.Evidence);
            }
            var tasks = benchmark.Tasks;

            var runsPath = Path.Combine(Path.GetFullPath(OutputDirectory), "runs.jsonl");
            var existingResults = File.Exists(runsPath)
                ? await JsonLinesRunReader.ReadAsync(runsPath, cancellationTokenSource.Token)
                : [];
            var sameBatchExists = existingResults.Any(result =>
                result.BatchId.Equals(configuration.BatchId, StringComparison.Ordinal));
            if (sameBatchExists && !ResumeExistingBatch)
            {
                throw new InvalidOperationException(
                    "Batch ID này đã có kết quả. Bật ‘Tiếp tục batch’ hoặc nhập Batch ID mới để không ghi trộn quan sát.");
            }
            var matchingPlanExists = existingResults.Any(result =>
                result.BatchId.Equals(configuration.BatchId, StringComparison.Ordinal) &&
                result.RunPlanFingerprint.Equals(configuration.RunPlanFingerprint, StringComparison.Ordinal));
            if (sameBatchExists && ResumeExistingBatch && !matchingPlanExists)
            {
                throw new InvalidOperationException(
                    "Batch ID này thuộc cấu hình/manifest khác hoặc được tạo trước Phase 7.9 nên không có fingerprint tương thích. Hãy tạo Batch ID mới; không tái dùng kết quả không chứng minh được cùng cấu hình.");
            }

            var resumePlan = BatchResumePlanner.Build(
                tasks,
                configuration,
                ResumeExistingBatch ? existingResults : [],
                RetryInfrastructureFailures);
            SetExperimentProgress(resumePlan.RunsToExecute, 0, "Đang tiền kiểm trước khi chạy LLM");
            await BatchCheckpointStore.SavePlanAsync(
                Path.GetFullPath(OutputDirectory), resumePlan, cancellationTokenSource.Token);
            AppendLog($"CHECKPOINT {resumePlan.Summary} fingerprint={configuration.RunPlanFingerprint[..12]}");
            if (resumePlan.RunsToExecute == 0)
            {
                var reused = SelectCurrentBatch(existingResults, configuration);
                PopulateRunRows(reused);
                UpdateStatistics(reused);
                RunSummary = $"{reused.Count} runs · resumed (nothing to execute)";
                StatusText = "Batch đã hoàn tất từ checkpoint; không gọi LLM và không phát sinh token mới.";
                return;
            }

            llmClient = CreateExperimentClient();
            IRepairExecutor executor = new RealRepairExecutor(llmClient, Path.GetFullPath(OutputDirectory));

            StatusText = $"Đang chạy với {SelectedProvider.DisplayName} / {ModelName}; " +
                         $"song song={configuration.MaxParallelRuns}; giãn cách LLM={MinimumLlmRequestIntervalSeconds}s; " +
                         $"key pool={llmClient.KeyFingerprints.Count}...";

            var coordinator = new ExperimentCoordinator(
                executor,
                new JsonLinesRunRepository(Path.GetFullPath(OutputDirectory)));
            coordinator.ProgressChanged += OnProgressChanged;
            var executedResults = await coordinator.RunAsync(
                tasks,
                configuration,
                cancellationTokenSource.Token,
                resumePlan.SkippedSlots);
            var allResults = await JsonLinesRunReader.ReadAsync(runsPath, CancellationToken.None);
            var results = SelectCurrentBatch(allResults, configuration);

            var successCount = results.Count(result => result.Status == RunStatus.Success);
            UpdateStatistics(results);
            var csvExport = await CsvExperimentExporter.ExportAsync(
                results,
                Path.GetFullPath(OutputDirectory),
                CancellationToken.None);
            var statisticalAnalysis = Rq2StatisticalAnalyzer.Analyze(results);
            var analysisExport = await Rq2AnalysisExporter.ExportAsync(
                results,
                statisticalAnalysis,
                Path.GetFullPath(OutputDirectory),
                CancellationToken.None);
            var narrativeReportPath = await Rq2NarrativeReportExporter.ExportAsync(
                results,
                statisticalAnalysis,
                Path.GetFullPath(OutputDirectory),
                CancellationToken.None);
            var measurementDiagnosticsPath = await MeasurementDiagnosticsExporter.ExportAsync(
                results,
                Path.GetFullPath(OutputDirectory),
                CancellationToken.None);
            var tuningTriage = await Rq2TuningTriageExporter.ExportAsync(
                results,
                Path.GetFullPath(OutputDirectory),
                CancellationToken.None);
            AppendLog($"CSV runs: {csvExport.RunsPath}");
            AppendLog($"CSV iterations: {csvExport.IterationsPath}");
            AppendLog($"RQ2 summary: {analysisExport.SummaryPath}");
            AppendLog($"RQ2 pairwise: {analysisExport.PairwisePath}");
            AppendLog($"RQ2 task-level: {analysisExport.TaskLevelPath}");
            AppendLog($"RQ2 project-level: {analysisExport.ProjectLevelPath}");
            AppendLog($"RQ2 outcomes: {analysisExport.OutcomesPath}");
            AppendLog($"RQ2 report: {narrativeReportPath}");
            AppendLog($"Measurement triage: {measurementDiagnosticsPath}");
            AppendLog($"TGSLR tuning candidates: {tuningTriage.CandidatesPath}");
            AppendLog($"TGSLR tuning guide: {tuningTriage.SummaryPath}");
            StatusText = $"Hoàn tất batch {configuration.BatchId}: mới={executedResults.Count}, tái dùng={resumePlan.ReusedTerminalRuns}, success={successCount}/{results.Count}.";
            RunSummary = $"{results.Count} runs · {successCount} success";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Đã hủy thực nghiệm.";
        }
        catch (Exception exception)
        {
            StatusText = $"Lỗi: {exception.Message}";
            AppendLog($"ERROR {exception}");
        }
        finally
        {
            llmClient?.Dispose();
            cancellationTokenSource?.Dispose();
            cancellationTokenSource = null;
            IsRunning = false;
        }
    }

    private async Task PreflightAsync()
    {
        try
        {
            var configuration = BuildConfiguration();
            configuration.Validate();
            IsRunning = true;
            cancellationTokenSource = new CancellationTokenSource();
            StatusText = "Đang tiền kiểm dataset và thiết kế thực nghiệm; không gọi LLM...";
            var messages = await BenchmarkPreflight.RunAsync(
                BenchmarkManifestPath,
                string.IsNullOrWhiteSpace(RepositoryPath) ? null : RepositoryPath,
                configuration,
                cancellationTokenSource.Token);
            foreach (var message in messages) AppendLog($"PREFLIGHT {message}");
            UpdateDatasetReadiness(messages);
            StatusText = messages.Any(message => message.StartsWith("WARNING:", StringComparison.Ordinal))
                ? $"Tiền kiểm đạt nhưng còn cảnh báo phương pháp. {DatasetReadinessStatus}"
                : $"Tiền kiểm đạt; không dùng API/token. {DatasetReadinessStatus}";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Đã hủy tiền kiểm.";
        }
        catch (Exception exception)
        {
            StatusText = $"Tiền kiểm thất bại: {exception.Message}";
            AppendLog($"PREFLIGHT ERROR {exception}");
        }
        finally
        {
            cancellationTokenSource?.Dispose();
            cancellationTokenSource = null;
            IsRunning = false;
        }
    }

    private async Task EstimateBatchAsync()
    {
        try
        {
            var configuration = BuildConfiguration();
            configuration.Validate();
            IsRunning = true;
            cancellationTokenSource = new CancellationTokenSource();
            StatusText = "Đang lập dự toán batch; không gọi LLM...";

            var benchmark = await BenchmarkManifestLoader.LoadDefinitionAsync(
                BenchmarkManifestPath,
                string.IsNullOrWhiteSpace(RepositoryPath) ? null : RepositoryPath,
                cancellationTokenSource.Token);
            var effectiveBatchId = string.IsNullOrWhiteSpace(configuration.BatchId)
                ? "NEW-BATCH"
                : configuration.BatchId;
            var manifestHash = Convert.ToHexString(SHA256.HashData(
                await File.ReadAllBytesAsync(Path.GetFullPath(BenchmarkManifestPath), cancellationTokenSource.Token)));
            configuration = configuration with
            {
                BatchId = effectiveBatchId,
                RunPlanFingerprint = BatchResumePlanner.CreateFingerprint(benchmark.Tasks, configuration, manifestHash)
            };

            var runsPath = Path.Combine(Path.GetFullPath(OutputDirectory), "runs.jsonl");
            var existing = File.Exists(runsPath)
                ? await JsonLinesRunReader.ReadAsync(runsPath, cancellationTokenSource.Token)
                : [];
            var plan = BatchResumePlanner.Build(
                benchmark.Tasks,
                configuration,
                ResumeExistingBatch ? existing : [],
                RetryInfrastructureFailures);
            var tokenCeiling = checked((long)plan.RunsToExecute * TokenBudget);
            SetExperimentProgress(plan.RunsToExecute, 0,
                plan.RunsToExecute == 0 ? "Không còn slot cần chạy" : "Sẵn sàng chạy");
            BatchEstimate =
                $"Dự toán: {plan.PlannedRuns} slot; chạy mới {plan.RunsToExecute}; tái dùng {plan.ReusedTerminalRuns}; " +
                $"retry hạ tầng {plan.RetriedInfrastructureRuns}; trần token mới {tokenCeiling:N0}. " +
                $"Fingerprint {plan.Fingerprint[..12]}.";
            StatusText = "Đã lập dự toán batch; chưa gọi LLM và chưa dùng token.";
            AppendLog($"ESTIMATE {plan.Summary} token-ceiling={tokenCeiling}");
        }
        catch (OperationCanceledException)
        {
            StatusText = "Đã hủy dự toán batch.";
        }
        catch (Exception exception)
        {
            BatchEstimate = $"Không thể lập dự toán: {exception.Message}";
            StatusText = "Dự toán batch thất bại.";
            AppendLog($"ESTIMATE ERROR {exception}");
        }
        finally
        {
            cancellationTokenSource?.Dispose();
            cancellationTokenSource = null;
            IsRunning = false;
        }
    }

    private void UpdateDatasetReadiness(IReadOnlyList<string> messages)
    {
        var readiness = messages.LastOrDefault(message =>
            message.StartsWith("READINESS:", StringComparison.Ordinal));
        DatasetReadinessStatus = readiness is null
            ? "Tiền kiểm chưa trả về báo cáo readiness."
            : readiness["READINESS:".Length..].Trim();
    }

    private ExperimentConfiguration BuildConfiguration() => new(
        ModelName,
        MaxIterations,
        TokenBudget,
        Repetitions,
        SelectedStrategies(),
        ReasoningEffort,
        SelectedProvider.DisplayName,
        ApiProtocol.ToString(),
        BatchId: BatchId.Trim(),
        ScheduleSeed: ScheduleSeed,
        MaxParallelRuns: MaxParallelRuns);

    private static IReadOnlyList<ExperimentRunResult> SelectCurrentBatch(
        IReadOnlyList<ExperimentRunResult> results,
        ExperimentConfiguration configuration) => results
        .Where(result => result.BatchId.Equals(configuration.BatchId, StringComparison.Ordinal) &&
                         result.RunPlanFingerprint.Equals(configuration.RunPlanFingerprint, StringComparison.Ordinal))
        .GroupBy(BatchResumePlanner.ToSlot)
        .Select(group => group.OrderByDescending(result => result.CompletedAt).First())
        .OrderBy(result => result.Design.ScheduleIndex)
        .ToArray();

    private void OnProgressChanged(object? sender, ExperimentProgress progress)
    {
        var dispatcher = Application.Current.Dispatcher;
        if (!dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => ApplyProgress(progress));
            return;
        }

        ApplyProgress(progress);
    }

    private void ApplyProgress(ExperimentProgress progress)
    {
        var row = Runs.FirstOrDefault(item => item.RunId == progress.RunId);
        if (row is null)
        {
            row = new RunRowViewModel
            {
                RunId = progress.RunId,
                TaskId = progress.TaskId,
                ScenarioId = progress.ScenarioId,
                Strategy = progress.Strategy.ToDisplayName(),
                Repetition = progress.Repetition
            };
            Runs.Add(row);
        }

        row.Update(progress);
        var completed = Runs.Count(item => item.Status is "Success" or "Failed" or "BudgetExhausted" or "Cancelled");
        var succeeded = Runs.Count(item => item.Status == "Success");
        RunSummary = ExperimentProgressTotal > 0
            ? $"{completed}/{ExperimentProgressTotal} completed · {succeeded} success"
            : $"{completed}/{Runs.Count} completed · {succeeded} success";
        if (ExperimentProgressTotal > 0)
        {
            ExperimentProgressCompleted = Math.Min(ExperimentProgressTotal, completed);
            ExperimentProgressText = $"Tiến độ: {ExperimentProgressCompleted}/{ExperimentProgressTotal} slot " +
                                     $"({ExperimentProgressCompleted / (double)ExperimentProgressTotal:P0})";
        }
        AppendLog(
            $"{DateTime.Now:HH:mm:ss} {progress.TaskId} " +
            $"{progress.Strategy.ToDisplayName()} [{progress.Status}] " +
            $"iter={progress.Iteration} tokens={progress.TotalTokens} — {progress.Message}");
    }

    private IReadOnlyList<RepairStrategy> SelectedStrategies()
    {
        var strategies = new List<RepairStrategy>();
        if (IsTgslrSelected) strategies.Add(RepairStrategy.TraceabilityGuidedScenarioLocal);
        if (IsFtmrSelected) strategies.Add(RepairStrategy.FailingTestMethod);
        if (IsFcrSelected) strategies.Add(RepairStrategy.FullClassRegeneration);
        return strategies;
    }

    private async Task LoadExistingResultsAsync()
    {
        try
        {
            var path = Path.Combine(Path.GetFullPath(OutputDirectory), "runs.jsonl");
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Không tìm thấy runs.jsonl trong thư mục kết quả.", path);
            }

            var results = await JsonLinesRunReader.ReadAsync(path, CancellationToken.None);
            var selectedResults = ExperimentResultBatchSelector.SelectLatest(results);
            PopulateRunRows(selectedResults);
            UpdateStatistics(selectedResults);
            var successes = selectedResults.Count(result => result.Status == RunStatus.Success);
            RunSummary = $"{selectedResults.Count} runs · {successes} success";
            StatusText = selectedResults.Count == results.Count
                ? $"Đã nạp {selectedResults.Count} run từ runs.jsonl."
                : $"Đã nạp batch mới nhất gồm {selectedResults.Count}/{results.Count} run trong runs.jsonl.";
        }
        catch (Exception exception)
        {
            StatusText = $"Không thể nạp kết quả: {exception.Message}";
            AppendLog($"ERROR loading results: {exception}");
        }
    }

    private void PopulateRunRows(IReadOnlyList<ExperimentRunResult> results)
    {
        Runs.Clear();
        foreach (var result in results)
        {
            var row = new RunRowViewModel
            {
                RunId = result.RunId,
                TaskId = result.TaskId,
                ScenarioId = result.ScenarioId,
                Strategy = result.Strategy.ToDisplayName(),
                Repetition = result.Repetition
            };
            row.Update(new ExperimentProgress(
                result.RunId,
                result.TaskId,
                result.ScenarioId,
                result.Strategy,
                result.Repetition,
                result.Status,
                result.Iterations.Count,
                result.TotalTokens,
                result.TotalChangedLines,
                result.RegressedTests,
                result.StopReason));
            Runs.Add(row);
        }
    }

    private void UpdateStatistics(IReadOnlyList<ExperimentRunResult> results)
    {
        TreatmentStatistics.Clear();
        PairwiseComparisons.Clear();
        OutcomeEffects.Clear();
        StageFunnelStatistics.Clear();
        var rq2Analysis = Rq2StatisticalAnalyzer.Analyze(results);
        var claim = Rq2ClaimEvaluator.Evaluate(results, rq2Analysis);
        StatisticsAnalysisStatus = rq2Analysis.Readiness.DisplayStatus;
        StatisticsOverallConclusion = rq2Analysis.OverallConclusion;
        StatisticsClaimStatus = claim.Status;
        StatisticsClaimDetail = claim.Explanation + " " +
            string.Join(" ", claim.Criteria.Where(item => !item.Passed).Take(3)
                .Select(item => $"Chưa đạt: {item.Name}."));
        StatisticsPowerProjection = string.Join(" | ", claim.PilotPowerProjections.Select(item =>
            $"{item.Comparison}: required≈{item.RequiredDisplay}, power@n=100={item.PowerAt100Display}"));
        StatisticsIndependentTasks = rq2Analysis.Readiness.IndependentTasks;
        StatisticsRequiredIndependentUnits = rq2Analysis.Readiness.RequiredIndependentUnits;
        StatisticsProjects = rq2Analysis.Readiness.Projects;
        StatisticsRequiredProjectClusters = rq2Analysis.Readiness.RequiredProjectClusters;
        foreach (var comparison in rq2Analysis.Comparisons) PairwiseComparisons.Add(comparison);
        foreach (var outcome in rq2Analysis.Outcomes) OutcomeEffects.Add(outcome);
        StatisticsTotalRuns = results.Count;
        StatisticsSuccessRate = results.Count == 0
            ? 0
            : (double)results.Count(result => result.Status == RunStatus.Success) / results.Count;
        var analysisEligibleRuns = results.Where(ExperimentOutcomePolicy.IsAnalysisEligible).ToArray();
        StatisticsAnalysisSuccessRate = analysisEligibleRuns.Length == 0
            ? 0
            : (double)analysisEligibleRuns.Count(result => result.Status == RunStatus.Success) /
              analysisEligibleRuns.Length;
        StatisticsInfrastructureFailures = results.Count(ExperimentOutcomePolicy.IsInfrastructureFailure);
        StatisticsTotalTokens = results.Sum(result => result.TotalTokens);
        StatisticsTokenAccountingCompletionRate = results.Count == 0
            ? null
            : (double)results.Count(result => result.TokenAccountingComplete) / results.Count;
        StatisticsAverageIterations = results.Count == 0 ? 0 : results.Average(result => result.Iterations.Count);
        StatisticsTotalRegressions = results.Sum(result => result.RegressedTests);
        var successfulRuns = results.Where(result => result.Status == RunStatus.Success).ToArray();
        var completeMeasurements = successfulRuns.Count(result =>
            result.FinalCoverageStatus == MeasurementStatus.Succeeded &&
            result.FinalMutationStatus == MeasurementStatus.Succeeded);
        StatisticsMeasurementCompletionRate = successfulRuns.Length == 0
            ? null
            : (double)completeMeasurements / successfulRuns.Length;
        StatisticsBatchLabel = results.Count == 0
            ? "Chưa có batch"
            : BuildBatchLabel(results[0], results.Count);
        var measuredMutations = successfulRuns
            .Where(result =>
                result.FinalMutationStatus == MeasurementStatus.Succeeded &&
                result.FinalMutationScore.HasValue)
            .Select(result => result.FinalMutationScore!.Value)
            .ToArray();
        StatisticsAverageMutationScore = measuredMutations.Length == 0
            ? null
            : measuredMutations.Average();

        var grouped = results
            .GroupBy(result => result.Strategy)
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var runs = group.ToArray();
                var successes = runs.Where(result => result.Status == RunStatus.Success).ToArray();
                var eligibleRuns = runs.Where(ExperimentOutcomePolicy.IsAnalysisEligible).ToArray();
                var tokenAccountedEligibleRuns = eligibleRuns
                    .Where(result => result.TokenAccountingComplete)
                    .ToArray();
                var infrastructureFailures = runs.Count(ExperimentOutcomePolicy.IsInfrastructureFailure);
                var measuredCoverage = successes
                    .Where(result =>
                        result.FinalCoverageStatus == MeasurementStatus.Succeeded &&
                        result.FinalLineCoverage.HasValue)
                    .Select(result => result.FinalLineCoverage!.Value)
                    .ToArray();
                var measuredMutation = successes
                    .Where(result =>
                        result.FinalMutationStatus == MeasurementStatus.Succeeded &&
                        result.FinalMutationScore.HasValue)
                    .Select(result => result.FinalMutationScore!.Value)
                    .ToArray();
                return new
                {
                    Strategy = group.Key.ToDisplayName(),
                    Runs = runs.Length,
                    Successes = successes.Length,
                    SuccessRate = (double)successes.Length / runs.Length,
                    AnalysisEligibleRuns = eligibleRuns.Length,
                    AnalysisSuccessRate = eligibleRuns.Length == 0
                        ? 0
                        : (double)eligibleRuns.Count(result => result.Status == RunStatus.Success) / eligibleRuns.Length,
                    InfrastructureFailures = infrastructureFailures,
                    AverageTokens = tokenAccountedEligibleRuns.Length == 0
                        ? 0
                        : tokenAccountedEligibleRuns.Average(result => result.TotalTokens),
                    AverageApiCalls = eligibleRuns.Length == 0 ? 0 : eligibleRuns.Average(result => result.TotalApiCalls),
                    AverageIterations = eligibleRuns.Length == 0 ? 0 : eligibleRuns.Average(result => result.Iterations.Count),
                    AverageChangedLines = eligibleRuns.Length == 0 ? 0 : eligibleRuns.Average(result => result.TotalChangedLines),
                    TotalRegressions = eligibleRuns.Sum(result => result.RegressedTests),
                    MeasurementsComplete = successes.Count(result =>
                        result.FinalCoverageStatus == MeasurementStatus.Succeeded &&
                        result.FinalMutationStatus == MeasurementStatus.Succeeded),
                    CoverageMeasurementsComplete = successes.Count(result =>
                        result.FinalCoverageStatus == MeasurementStatus.Succeeded),
                    MutationMeasurementsComplete = successes.Count(result =>
                        result.FinalMutationStatus == MeasurementStatus.Succeeded),
                    TokenAccountedRuns = runs.Count(result => result.TokenAccountingComplete),
                    PromptAuditedRuns = runs.Count(result =>
                        result.PromptAudit.ContractSatisfied &&
                        !result.PromptAudit.InferredFromLegacy),
                    TotalApiRetries = runs.Sum(result => result.TotalApiRetries),
                    TotalKeyFailovers = runs.Sum(result => result.TotalKeyFailovers),
                    AverageLineCoverage = measuredCoverage.Length == 0 ? (double?)null : measuredCoverage.Average(),
                    AverageMutationScore = measuredMutation.Length == 0 ? (double?)null : measuredMutation.Average()
                };
            })
            .ToArray();
        var maximumAverageTokens = grouped.Length == 0 ? 0 : grouped.Max(row => row.AverageTokens);
        foreach (var row in grouped)
        {
            TreatmentStatistics.Add(new TreatmentStatisticsViewModel(
                row.Strategy,
                row.Runs,
                row.Successes,
                row.SuccessRate,
                row.AnalysisEligibleRuns,
                row.AnalysisSuccessRate,
                row.InfrastructureFailures,
                row.AverageTokens,
                row.AverageApiCalls,
                row.AverageIterations,
                row.AverageChangedLines,
                row.TotalRegressions,
                row.MeasurementsComplete,
                row.CoverageMeasurementsComplete,
                row.MutationMeasurementsComplete,
                row.TokenAccountedRuns,
                row.PromptAuditedRuns,
                row.TotalApiRetries,
                row.TotalKeyFailovers,
                row.AverageLineCoverage,
                row.AverageMutationScore,
                maximumAverageTokens == 0 ? 0 : row.AverageTokens / maximumAverageTokens * 100));
        }

        foreach (var stageGroup in results.GroupBy(result => result.Strategy).OrderBy(group => group.Key))
        {
            var runs = stageGroup.ToArray();
            StageFunnelStatistics.Add(new StageFunnelStatisticsViewModel(
                stageGroup.Key.ToDisplayName(),
                runs.Length,
                CountStage(runs, result => result.Stages.LlmOutput.Status),
                CountStage(runs, result => result.Stages.SourceApplication.Status),
                CountStage(runs, result => result.Stages.Compilation.Status),
                CountStage(runs, result => result.Stages.FocalTest.Status),
                CountStage(runs, result => result.Stages.BuggyOracle.Status, countSkippedAsCleared: true),
                CountStage(runs, result => result.Stages.Regression.Status),
                CountStage(runs, result => result.Stages.Coverage.Status),
                CountStage(runs, result => result.Stages.Mutation.Status),
                runs.Count(result => result.Stages.InferredFromLegacy)));
        }

        var treatmentCount = grouped.Length;
        var minimumRepetitions = results.Count == 0
            ? 0
            : results.GroupBy(result => new { result.TaskId, result.Strategy }).Min(group => group.Count());
        var caveats = new List<string>();
        if (results.Any(result => string.IsNullOrWhiteSpace(result.BatchId)))
        {
            caveats.Add("Dữ liệu LEGACY không có batch ID nên không thể tách chính xác các lần chạy cũ; không dùng nhóm này để kết luận RQ2.");
        }

        if (StatisticsInfrastructureFailures > 0)
        {
            caveats.Add($"Loại {StatisticsInfrastructureFailures} lỗi hạ tầng/API khỏi analysis success rate; raw success rate vẫn giữ toàn bộ run.");
        }

        var inferredStageRuns = results.Count(result => result.Stages.InferredFromLegacy);
        if (inferredStageRuns > 0)
        {
            caveats.Add($"{inferredStageRuns} run(s) dùng stage evidence suy luận từ schema cũ; chỉ batch schema 8 trở lên mới có bằng chứng stage được ghi trực tiếp khi chạy.");
        }

        var incompletePromptAudits = results.Count(result =>
            !result.PromptAudit.ContractSatisfied || result.PromptAudit.InferredFromLegacy);
        if (incompletePromptAudits > 0)
        {
            caveats.Add($"{incompletePromptAudits} run(s) không có prompt audit native hợp lệ; cần schema 9 trở lên để chứng minh treatment isolation.");
        }

        var incompleteTokenAccounting = results.Count(result => !result.TokenAccountingComplete);
        if (incompleteTokenAccounting > 0)
        {
            caveats.Add($"{incompleteTokenAccounting} run(s) thiếu usage token từ provider; tổng token chỉ là phần quan sát được và các run này bị loại khỏi Avg tokens.");
        }

        var expectedBatchRuns = results.Select(result => result.ExpectedBatchRuns).DefaultIfEmpty(0).Max();
        if (expectedBatchRuns > 0 && results.Count < expectedBatchRuns)
        {
            caveats.Add($"Batch chưa đầy đủ: mới có {results.Count}/{expectedBatchRuns} run; không dùng batch này để so sánh treatment.");
        }

        caveats.Add(treatmentCount < 3
            ? $"Mới có {treatmentCount}/3 treatment. Chưa thể so sánh đầy đủ TGSLR–FTMR–FCR."
            : minimumRepetitions < 3
                ? $"Mỗi task/treatment mới có tối thiểu {minimumRepetitions} repetition. Đây chỉ là thống kê mô tả, chưa đủ kết luận thống kê."
                : "Các số liệu đang là thống kê mô tả. Chỉ so sánh khi các run dùng cùng dataset, provider, model, budget và stop rule.");
        var incompleteMeasurements = successfulRuns.Count(result =>
            result.FinalCoverageStatus != MeasurementStatus.Succeeded ||
            result.FinalMutationStatus != MeasurementStatus.Succeeded);
        if (incompleteMeasurements > 0)
        {
            var coverageBreakdown = string.Join(", ", successfulRuns
                .Where(result => result.FinalCoverageStatus != MeasurementStatus.Succeeded)
                .GroupBy(result => result.FinalCoverageStatus)
                .OrderBy(group => group.Key)
                .Select(group => $"{group.Key}={group.Count()}"));
            var mutationBreakdown = string.Join(", ", successfulRuns
                .Where(result => result.FinalMutationStatus != MeasurementStatus.Succeeded)
                .GroupBy(result => result.FinalMutationStatus)
                .OrderBy(group => group.Key)
                .Select(group => $"{group.Key}={group.Count()}"));
            if (coverageBreakdown.Length > 0) caveats.Add($"Coverage incomplete: {coverageBreakdown}.");
            if (mutationBreakdown.Length > 0) caveats.Add($"Mutation incomplete: {mutationBreakdown}.");
            caveats.Add($"{incompleteMeasurements} successful run(s) thiếu coverage/mutation; giá trị thiếu hiển thị N/A.");
        }

        foreach (var issue in rq2Analysis.Readiness.Issues)
        {
            if (!caveats.Contains(issue, StringComparer.Ordinal)) caveats.Add(issue);
        }
        StatisticsCaveat = string.Join(' ', caveats);
    }

    private static string BuildBatchLabel(ExperimentRunResult result, int actualRuns)
    {
        var batch = string.IsNullOrWhiteSpace(result.BatchId) ? "LEGACY — chưa có batch ID" : result.BatchId;
        var provenance = string.IsNullOrWhiteSpace(result.ProviderName)
            ? string.Empty
            : $" · {result.ProviderName}/{result.ModelName}";
        var completeness = result.ExpectedBatchRuns > 0
            ? $" · {actualRuns}/{result.ExpectedBatchRuns} runs · seed {result.ScheduleSeed}"
            : string.Empty;
        return $"Batch: {batch}{provenance}{completeness}";
    }

    private void Cancel() => cancellationTokenSource?.Cancel();

    private void Clear()
    {
        Runs.Clear();
        TreatmentStatistics.Clear();
        PairwiseComparisons.Clear();
        OutcomeEffects.Clear();
        StageFunnelStatistics.Clear();
        StatisticsTotalRuns = 0;
        StatisticsSuccessRate = 0;
        StatisticsAnalysisSuccessRate = 0;
        StatisticsInfrastructureFailures = 0;
        StatisticsMeasurementCompletionRate = null;
        StatisticsTokenAccountingCompletionRate = null;
        StatisticsTotalTokens = 0;
        StatisticsAverageIterations = 0;
        StatisticsTotalRegressions = 0;
        StatisticsAverageMutationScore = null;
        StatisticsCaveat = "Chưa có kết quả để thống kê.";
        StatisticsBatchLabel = "Chưa có batch";
        StatisticsAnalysisStatus = "Chưa phân tích";
        StatisticsOverallConclusion = "Chưa có dữ liệu để trả lời RQ2.";
        StatisticsClaimStatus = "Claim gate chưa được đánh giá";
        StatisticsClaimDetail = "Chưa có dữ liệu.";
        StatisticsPowerProjection = "Chưa có power projection.";
        StatisticsIndependentTasks = 0;
        StatisticsRequiredIndependentUnits = 0;
        StatisticsProjects = 0;
        StatisticsRequiredProjectClusters = 0;
        LogText = string.Empty;
        RunSummary = "Chưa có lượt chạy";
        SetExperimentProgress(0, 0, "Chưa lập kế hoạch chạy");
        StatusText = "Sẵn sàng.";
    }

    private void SetExperimentProgress(int total, int completed, string state)
    {
        ExperimentProgressTotal = Math.Max(0, total);
        ExperimentProgressCompleted = Math.Clamp(completed, 0, ExperimentProgressTotal);
        ExperimentProgressText = ExperimentProgressTotal == 0
            ? state
            : $"{state}: {ExperimentProgressCompleted}/{ExperimentProgressTotal} slot " +
              $"({ExperimentProgressCompleted / (double)ExperimentProgressTotal:P0})";
    }

    private void AppendLog(string line)
    {
        LogText = string.IsNullOrEmpty(LogText) ? line : LogText + Environment.NewLine + line;
    }

    private static int CountStage(
        IEnumerable<ExperimentRunResult> results,
        Func<ExperimentRunResult, RepairStageStatus> selector,
        bool countSkippedAsCleared = false) =>
        results.Count(result => selector(result) is RepairStageStatus.Succeeded ||
                                countSkippedAsCleared && selector(result) is RepairStageStatus.Skipped);

    private void RefreshCommands()
    {
        StartCommand.RaiseCanExecuteChanged();
        PreflightCommand.RaiseCanExecuteChanged();
        LoadModelsCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        ClearCommand.RaiseCanExecuteChanged();
        LoadResultsCommand.RaiseCanExecuteChanged();
    }
}
