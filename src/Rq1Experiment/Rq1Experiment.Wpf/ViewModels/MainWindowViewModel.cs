using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Rq1Experiment.Contracts.Config;
using Rq1Experiment.Contracts.Experiments;
using Rq1Experiment.Contracts.Progress;
using Rq1Experiment.Contracts.Subjects;
using Rq1Experiment.Core.Abstractions;
using Rq1Experiment.Core.Config;
using Rq1Experiment.Infrastructure.Prompts;

namespace Rq1Experiment.Wpf.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private static readonly Regex ArtifactRunRegex = new(
        @"(?<subject>[A-Za-z0-9]+(?:-[A-Za-z0-9]+)+)-(?<approach>DIRECT|PLANNED)-R(?<repetition>\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ToolRunRegex = new(
        @"(?<subject>[A-Za-z0-9]+(?:-[A-Za-z0-9]+)+)\s+(?<approach>DIRECT|PLANNED)\s+R(?<repetition>\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GenerationRunRegex = new(
        @"for\s+(?<subject>[A-Za-z0-9]+(?:-[A-Za-z0-9]+)+),\s+repetition\s+(?<repetition>\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private readonly IExperimentConfigStore _configStore;
    private readonly IApiKeyStore _apiKeyStore;
    private readonly IApiKeyPool _apiKeyPool;
    private readonly IModelCatalogService _modelCatalog;
    private readonly IPhaseProgressStore _phaseProgressStore;
    private readonly IExperimentOrchestrator _orchestrator;
    private readonly ILlmClientFactory _llmClientFactory;
    private readonly IOfficialConfigLockService _officialConfigLock;
    private readonly ISmokeSubjectService _smokeSubjectService;
    private readonly string _workspaceRoot;
    private ExperimentConfig _config = new();
    private CancellationTokenSource? _runCancellation;
    private string _experimentId = "rq1-draft";
    private string _selectedMode = "Smoke";
    private string _repositoryRoot = string.Empty;
    private string _outputRoot = "experiments/rq1";
    private string _javaHome = string.Empty;
    private string _modulePath = ".";
    private int _repetitions = 1;
    private int _maxOutputTokensPerCall = 65_536;
    private int _totalTokenCeilingPerRun = 65_536;
    private int _maxParallelLlmCalls = 4;
    private int _maxParallelToolRuns = 2;
    private int _requestTimeoutSeconds = 180;
    private string _deepSeekThinkingMode = "enabled";
    private string _baseUrl = string.Empty;
    private string _selectedProviderId = ProviderIds.Gemini;
    private string _selectedModelId = string.Empty;
    private string _apiKeysText = string.Empty;
    private string _apiKeyStatus = "No keys loaded";
    private string _selectedExistingExperimentId = string.Empty;
    private string _statusMessage = "Ready";
    private string _logText = string.Empty;
    private double _overallProgress;
    private bool _isBusy;
    private string _selectedSmokeClass = string.Empty;
    private SubjectCandidate? _selectedSmokeMethod;
    private List<SubjectCandidate> _discoveredSmokeMethods = [];
    private string _smokeDiscoverySummary = "Chưa phân tích repository.";
    private PromptFileViewModel? _selectedPromptFile;
    private string _promptCatalogLocation = string.Empty;
    private string _promptCatalogStatus = "Prompt catalog chưa được load.";

    public MainWindowViewModel(
        IExperimentConfigStore configStore,
        IApiKeyStore apiKeyStore,
        IApiKeyPool apiKeyPool,
        IModelCatalogService modelCatalog,
        IPhaseProgressStore phaseProgressStore,
        IExperimentOrchestrator orchestrator,
        ILlmClientFactory llmClientFactory,
        IOfficialConfigLockService officialConfigLock,
        ISmokeSubjectService smokeSubjectService,
        string workspaceRoot)
    {
        _configStore = configStore;
        _apiKeyStore = apiKeyStore;
        _apiKeyPool = apiKeyPool;
        _modelCatalog = modelCatalog;
        _phaseProgressStore = phaseProgressStore;
        _orchestrator = orchestrator;
        _llmClientFactory = llmClientFactory;
        _officialConfigLock = officialConfigLock;
        _smokeSubjectService = smokeSubjectService;
        _workspaceRoot = workspaceRoot;

        PipelineSteps = new ObservableCollection<PipelineStepViewModel>(
        [
            new("preflight", "Preflight"),
            new("subjects", "Subjects"),
            new("targets", "Targets"),
            new("context-prompts", "Context / Prompts"),
            new("direct", "DIRECT"),
            new("planned", "PLANNED"),
            new("compile-test", "Compile / JUnit"),
            new("adequacy", "JaCoCo / PIT"),
            new("ledger", "Ledger"),
            new("analysis", "Analysis")
        ]);
        Runs.Add(new RunItemViewModel());

        LoadCommand = new AsyncRelayCommand(InitializeAsync, CanEdit, HandleError);
        SaveCommand = new AsyncRelayCommand(SaveAsync, CanEdit, HandleError);
        ValidateCommand = new AsyncRelayCommand(ValidateAsync, CanEdit, HandleError);
        SaveApiKeysCommand = new AsyncRelayCommand(SaveApiKeysAsync, CanEdit, HandleError);
        ValidateApiKeysCommand = new RelayCommand(ValidateApiKeys, CanEdit);
        RefreshModelsCommand = new AsyncRelayCommand(RefreshModelsAsync, CanEdit, HandleError);
        TestProviderConnectionCommand = new AsyncRelayCommand(TestProviderSetupAsync, CanEdit, HandleError);
        RunFullPipelineCommand = new AsyncRelayCommand(RunFullPipelineAsync, () => CanStartPipeline, HandleError);
        PauseCommand = new RelayCommand(Pause, () => IsBusy);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        ResumeCommand = new AsyncRelayCommand(ResumeAsync, () => !IsBusy && _orchestrator.CanResume, HandleError);
        BrowseRepositoryCommand = new RelayCommand(() => BrowseRepositoryRequested?.Invoke(this, EventArgs.Empty), CanEdit);
        BrowseJavaHomeCommand = new RelayCommand(() => BrowseJavaHomeRequested?.Invoke(this, EventArgs.Empty), CanEdit);
        OpenResultsWindowCommand = new RelayCommand(() => OpenResultsRequested?.Invoke(this, EventArgs.Empty));
        SavePhaseProgressCommand = new AsyncRelayCommand(SavePhaseProgressAsync, () => !IsBusy, HandleError);
        CreateNewExperimentCommand = new RelayCommand(
            () => CreateNewExperimentRequested?.Invoke(this, EventArgs.Empty), () => !IsBusy);
        OpenExperimentCommand = new AsyncRelayCommand(OpenExperimentAsync,
            () => !IsBusy && !string.IsNullOrWhiteSpace(SelectedExistingExperimentId), HandleError);
        DeleteExperimentCommand = new RelayCommand(
            () => DeleteExperimentRequested?.Invoke(this, EventArgs.Empty),
            () => !IsBusy && !string.IsNullOrWhiteSpace(SelectedExistingExperimentId));
        AnalyzeSmokeRepositoryCommand = new AsyncRelayCommand(AnalyzeSmokeRepositoryAsync,
            () => IsConfigurationEditable && IsSmokeMode && Directory.Exists(RepositoryRoot), HandleError);
        RetryFailedRunsCommand = new AsyncRelayCommand(RetryFailedRunsAsync, () => !IsBusy, HandleError);
        UnlockConfigurationCommand = new RelayCommand(UnlockConfiguration, () => !IsBusy);
        ReloadPromptsCommand = new RelayCommand(LoadPromptCatalog, () => !IsBusy);
    }

    public event EventHandler? BrowseRepositoryRequested;
    public event EventHandler? BrowseJavaHomeRequested;
    public event EventHandler? OpenResultsRequested;
    public event EventHandler? CreateNewExperimentRequested;
    public event EventHandler? DeleteExperimentRequested;

    public IReadOnlyList<ProviderOption> Providers { get; } =
    [
        new(ProviderIds.Gemini, "Gemini"),
        new(ProviderIds.DeepSeek, "DeepSeek"),
        new(ProviderIds.OpenRouter, "OpenRouter"),
        new(ProviderIds.TeamoRouter, "Teamo Router"),
        new(ProviderIds.JustWoker, "JustWoker")
    ];
    public IReadOnlyList<string> Modes { get; } = ["Smoke", "Pilot", "Official"];
    public IReadOnlyList<string> DeepSeekThinkingModes { get; } = ["enabled", "disabled"];
    public ObservableCollection<string> AvailableModels { get; } = [];
    public ObservableCollection<PipelineStepViewModel> PipelineSteps { get; }
    public ObservableCollection<RunItemViewModel> Runs { get; } = [];
    public ObservableCollection<PhaseProgressItemViewModel> Phases { get; } = [];
    public ObservableCollection<string> KnownExperimentIds { get; } = [];
    public ObservableCollection<string> SmokeFocalClasses { get; } = [];
    public ObservableCollection<SubjectCandidate> SmokeFocalMethods { get; } = [];
    public ObservableCollection<PromptFileViewModel> PromptFiles { get; } = [];

    public ICommand LoadCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand ValidateCommand { get; }
    public ICommand SaveApiKeysCommand { get; }
    public ICommand ValidateApiKeysCommand { get; }
    public ICommand RefreshModelsCommand { get; }
    public ICommand TestProviderConnectionCommand { get; }
    public ICommand RunFullPipelineCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand BrowseRepositoryCommand { get; }
    public ICommand BrowseJavaHomeCommand { get; }
    public ICommand OpenResultsWindowCommand { get; }
    public ICommand SavePhaseProgressCommand { get; }
    public ICommand CreateNewExperimentCommand { get; }
    public ICommand OpenExperimentCommand { get; }
    public ICommand DeleteExperimentCommand { get; }
    public ICommand AnalyzeSmokeRepositoryCommand { get; }
    public ICommand RetryFailedRunsCommand { get; }
    public ICommand UnlockConfigurationCommand { get; }
    public ICommand ReloadPromptsCommand { get; }

    public PromptFileViewModel? SelectedPromptFile
    {
        get => _selectedPromptFile;
        set => SetProperty(ref _selectedPromptFile, value);
    }
    public string PromptCatalogLocation
    {
        get => _promptCatalogLocation;
        private set => SetProperty(ref _promptCatalogLocation, value);
    }
    public string PromptCatalogStatus
    {
        get => _promptCatalogStatus;
        private set => SetProperty(ref _promptCatalogStatus, value);
    }

    public string ExperimentId { get => _experimentId; set => SetProperty(ref _experimentId, value); }
    public string SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (!SetProperty(ref _selectedMode, value)) return;
            OnPropertyChanged(nameof(IsSmokeMode));
            RaisePipelinePrerequisites();
            RaiseCommandStates();
            _ = LoadRunScheduleSafeAsync();
        }
    }
    public string RepositoryRoot
    {
        get => _repositoryRoot;
        set
        {
            if (!SetProperty(ref _repositoryRoot, value)) return;
            if (!string.Equals(value, _config.RepositoryRoot, StringComparison.OrdinalIgnoreCase))
                ClearSmokeSelection();
            RaisePipelinePrerequisites();
        }
    }
    public string OutputRoot
    {
        get => _outputRoot;
        set
        {
            if (!SetProperty(ref _outputRoot, value)) return;
            _ = LoadRunScheduleSafeAsync();
        }
    }
    public string JavaHome { get => _javaHome; set => SetProperty(ref _javaHome, value); }
    public string ModulePath { get => _modulePath; set => SetProperty(ref _modulePath, value); }
    public int Repetitions
    {
        get => _repetitions;
        set
        {
            if (!SetProperty(ref _repetitions, Math.Max(1, value))) return;
            _ = LoadRunScheduleSafeAsync();
        }
    }
    public int MaxOutputTokensPerCall
    {
        get => _maxOutputTokensPerCall;
        set => SetProperty(ref _maxOutputTokensPerCall, value);
    }
    public int TotalTokenCeilingPerRun
    {
        get => _totalTokenCeilingPerRun;
        set => SetProperty(ref _totalTokenCeilingPerRun, value);
    }
    public int MaxParallelLlmCalls
    {
        get => _maxParallelLlmCalls;
        set => SetProperty(ref _maxParallelLlmCalls, Math.Clamp(value, 1, 32));
    }
    public int MaxParallelToolRuns
    {
        get => _maxParallelToolRuns;
        set => SetProperty(ref _maxParallelToolRuns, Math.Clamp(value, 1, 8));
    }
    public int RequestTimeoutSeconds
    {
        get => _requestTimeoutSeconds;
        set => SetProperty(ref _requestTimeoutSeconds, value);
    }
    public string DeepSeekThinkingMode
    {
        get => _deepSeekThinkingMode;
        set => SetProperty(ref _deepSeekThinkingMode,
            string.Equals(value, "disabled", StringComparison.OrdinalIgnoreCase) ? "disabled" : "enabled");
    }
    public string BaseUrl
    {
        get => _baseUrl;
        set => SetProperty(ref _baseUrl, value);
    }
    public string SelectedProviderId
    {
        get => _selectedProviderId;
        set
        {
            if (!SetProperty(ref _selectedProviderId, value)) return;
            OnPropertyChanged(nameof(IsDeepSeekProvider));
            _ = LoadProviderDataSafeAsync();
        }
    }
    public string SelectedModelId
    {
        get => _selectedModelId;
        set
        {
            if (!SetProperty(ref _selectedModelId, value)) return;
            RaisePipelinePrerequisites();
        }
    }
    public string ApiKeysText
    {
        get => _apiKeysText;
        set
        {
            if (!SetProperty(ref _apiKeysText, value)) return;
            RaisePipelinePrerequisites();
        }
    }
    public string ApiKeyStatus { get => _apiKeyStatus; set => SetProperty(ref _apiKeyStatus, value); }
    public bool IsSmokeMode => SelectedMode.Equals("Smoke", StringComparison.OrdinalIgnoreCase);
    public bool IsDeepSeekProvider => SelectedProviderId.Equals(ProviderIds.DeepSeek, StringComparison.OrdinalIgnoreCase)
                                      || SelectedProviderId.Equals(ProviderIds.TeamoRouter, StringComparison.OrdinalIgnoreCase);
    public string SelectedSmokeClass
    {
        get => _selectedSmokeClass;
        set
        {
            if (!SetProperty(ref _selectedSmokeClass, value)) return;
            SmokeFocalMethods.Clear();
            foreach (var method in _discoveredSmokeMethods.Where(item => item.FocalClass == value)
                         .OrderBy(item => item.FocalMethodSignature, StringComparer.Ordinal).ThenBy(item => item.StartLine))
                SmokeFocalMethods.Add(method);
            SelectedSmokeMethod = SmokeFocalMethods.FirstOrDefault(item =>
                item.CandidateKey == _config.Subjects.SmokeCandidateKey);
            if (SelectedSmokeMethod is null && SmokeFocalMethods.Count == 1)
                SelectedSmokeMethod = SmokeFocalMethods[0];
        }
    }
    public SubjectCandidate? SelectedSmokeMethod
    {
        get => _selectedSmokeMethod;
        set
        {
            if (!SetProperty(ref _selectedSmokeMethod, value)) return;
            _config.Subjects.SmokeCandidateKey = value?.CandidateKey ?? string.Empty;
            _config.Subjects.SmokeFocalClass = value?.FocalClass ?? string.Empty;
            _config.Subjects.SmokeFocalMethodSignature = value?.FocalMethodSignature ?? string.Empty;
            RefreshSmokeRunPreview(value);
            OnPropertyChanged(nameof(SmokeSelectionSummary));
            RaisePipelinePrerequisites();
        }
    }
    public string SmokeSelectionSummary => SelectedSmokeMethod is null
        ? string.IsNullOrWhiteSpace(_config.Subjects.SmokeCandidateKey)
            ? SmokeDiscoverySummary
            : $"Saved: {_config.Subjects.SmokeFocalClass}.{_config.Subjects.SmokeFocalMethodSignature} (sẽ revalidate khi chạy)"
        : SelectedSmokeMethod.Eligible
            ? $"Eligible: {SelectedSmokeMethod.SourceFile}:{SelectedSmokeMethod.StartLine} · complexity={SelectedSmokeMethod.CyclomaticComplexity}"
            : $"Diagnostic Smoke only — không được đưa vào Pilot/Official: {FriendlyExclusions(SelectedSmokeMethod.ExclusionReasons)}";
    public string SmokeDiscoverySummary
    {
        get => _smokeDiscoverySummary;
        private set
        {
            if (!SetProperty(ref _smokeDiscoverySummary, value)) return;
            OnPropertyChanged(nameof(SmokeSelectionSummary));
        }
    }
    public string SelectedExistingExperimentId
    {
        get => _selectedExistingExperimentId;
        set
        {
            if (!SetProperty(ref _selectedExistingExperimentId, value)) return;
            (OpenExperimentCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (DeleteExperimentCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }
    public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }
    public string LogText { get => _logText; set => SetProperty(ref _logText, value); }
    public double OverallProgress { get => _overallProgress; set => SetProperty(ref _overallProgress, value); }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(IsConfigurationEditable));
            RaiseCommandStates();
        }
    }
    public bool IsFrozen => _config.IsFrozen;
    public bool IsConfigurationEditable => !IsBusy && !_config.IsFrozen;
    public string ConfigurationLockStatus => _config.IsFrozen
        ? "OFFICIAL CONFIGURATION LOCKED"
        : "Configuration editable";

    private void UnlockConfiguration()
    {
        _config.IsFrozen = false;
        OnPropertyChanged(nameof(IsFrozen));
        OnPropertyChanged(nameof(IsConfigurationEditable));
        OnPropertyChanged(nameof(ConfigurationLockStatus));
        RaiseCommandStates();
        StatusMessage = "Đã mở khóa cấu hình. Bạn có thể sửa Total token ceiling, Max output tokens, v.v.";
        AppendLog(StatusMessage);
    }
    public bool CanStartPipeline =>
        !IsBusy
        && Directory.Exists(RepositoryRoot)
        && (!IsSmokeMode || !string.IsNullOrWhiteSpace(_config.Subjects.SmokeCandidateKey))
        && !string.IsNullOrWhiteSpace(SelectedModelId)
        && ApiKeyParser.Parse(ApiKeysText).Count > 0;
    public string RunPrerequisiteMessage
    {
        get
        {
            var missing = new List<string>();
            if (!Directory.Exists(RepositoryRoot)) missing.Add("Java repository");
            if (IsSmokeMode && string.IsNullOrWhiteSpace(_config.Subjects.SmokeCandidateKey)) missing.Add("Smoke focal method");
            if (string.IsNullOrWhiteSpace(SelectedModelId)) missing.Add("exact model ID");
            if (ApiKeyParser.Parse(ApiKeysText).Count == 0) missing.Add("API key");
            return missing.Count == 0
                ? "Ready to run."
                : "Required before running: " + string.Join(", ", missing) + ".";
        }
    }

    public async Task InitializeAsync()
    {
        SetBusy(true);
        try
        {
            _config = await _configStore.LoadAsync();
            OnPropertyChanged(nameof(IsConfigurationEditable));
            OnPropertyChanged(nameof(ConfigurationLockStatus));
            ExperimentId = _config.ExperimentId;
            SelectedMode = _config.Mode;
            RepositoryRoot = _config.RepositoryRoot;
            OutputRoot = _config.OutputRoot;
            JavaHome = _config.Tools.JavaHome;
            ModulePath = _config.Tools.ModulePath;
            Repetitions = _config.Subjects.Repetitions;
            MaxOutputTokensPerCall = _config.Generation.MaxOutputTokensPerCall;
            TotalTokenCeilingPerRun = _config.Generation.TotalTokenCeilingPerRun;
            MaxParallelLlmCalls = _config.Generation.MaxParallelLlmCalls;
            MaxParallelToolRuns = _config.Tools.MaxParallelToolRuns;
            RequestTimeoutSeconds = _config.Llm.TimeoutSeconds;
            DeepSeekThinkingMode = _config.Llm.DeepSeekThinkingMode;
            BaseUrl = _config.Llm.BaseUrl;
            OnPropertyChanged(nameof(SmokeSelectionSummary));
            _selectedProviderId = _config.Llm.Provider;
            OnPropertyChanged(nameof(SelectedProviderId));
            OnPropertyChanged(nameof(IsDeepSeekProvider));
            SelectedModelId = _config.Llm.ModelId;
            await LoadProviderDataAsync(keepSelectedModel: true);
            await LoadPhaseProgressAsync();
            await RefreshKnownExperimentsAsync();
            LoadPromptCatalog();
            SelectedExistingExperimentId = ExperimentId;
            StatusMessage = "Configuration and local state loaded.";
            AppendLog("Loaded configuration, model catalog, API-key pool, and phase progress.");
        }
        finally
        {
            SetBusy(false);
        }
    }

    public void SetRepositoryRoot(string path) => RepositoryRoot = path;

    private void LoadPromptCatalog()
    {
        try
        {
            var previouslySelected = SelectedPromptFile?.RelativePath;
            var catalog = PromptTemplateCatalog.LoadAll(_workspaceRoot);
            PromptFiles.Clear();
            foreach (var file in catalog)
                PromptFiles.Add(new PromptFileViewModel(file.RelativePath, file.Kind, file.Content));
            SelectedPromptFile = PromptFiles.FirstOrDefault(file =>
                                     file.RelativePath.Equals(previouslySelected, StringComparison.Ordinal))
                                 ?? PromptFiles.FirstOrDefault();
            PromptCatalogLocation = PromptTemplateCatalog.DescribeLocation(_workspaceRoot);
            PromptCatalogStatus = $"Đã load {PromptFiles.Count} file; Phase 5 dùng trực tiếp catalog này.";
        }
        catch (Exception exception)
        {
            PromptCatalogStatus = "Không thể load prompt catalog: " + exception.Message;
            AppendLog(PromptCatalogStatus);
        }
    }

    private async Task AnalyzeSmokeRepositoryAsync()
    {
        UpdateConfigFromUi();
        SetBusy(true);
        try
        {
            var output = Path.Combine(Path.GetFullPath(Path.IsPathRooted(OutputRoot)
                ? OutputRoot : Path.Combine(_workspaceRoot, OutputRoot)), "subjects");
            var report = await _smokeSubjectService.DiscoverAsync(new SmokeMethodDiscoveryRequest(
                RepositoryRoot, output, _workspaceRoot, _config.Subjects.BenchmarkRevision, JavaHome, ModulePath),
                new Progress<string>(message => { StatusMessage = message; AppendLog(message); }));
            _discoveredSmokeMethods = report.Candidates;
            SmokeFocalClasses.Clear();
            foreach (var focalClass in _discoveredSmokeMethods.Select(item => item.FocalClass)
                         .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal))
                SmokeFocalClasses.Add(focalClass);
            var savedClass = _config.Subjects.SmokeFocalClass;
            SelectedSmokeClass = SmokeFocalClasses.Contains(savedClass)
                ? savedClass : SmokeFocalClasses.FirstOrDefault() ?? string.Empty;
            var exclusionCounts = report.Candidates.SelectMany(item => item.ExclusionReasons)
                .GroupBy(reason => reason, StringComparer.Ordinal)
                .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => $"{group.Key}={group.Count()}").ToList();
            SmokeDiscoverySummary = report.EligibleMethodCount > 0
                ? $"Tìm thấy {report.EligibleMethodCount}/{report.ParsedMethodCount} method hợp lệ. Chọn class và method."
                : $"0/{report.ParsedMethodCount} method đạt tiêu chí experiment. Bạn vẫn có thể chọn method có dấu ✕ để chạy Diagnostic Smoke. {string.Join("; ", exclusionCounts)}.";
            StatusMessage = $"Đã phân tích {report.ParsedMethodCount} method; có {report.EligibleMethodCount} focal method hợp lệ.";
            AppendLog(StatusMessage);
        }
        finally { SetBusy(false); }
    }

    private void ClearSmokeSelection()
    {
        _discoveredSmokeMethods = [];
        SmokeFocalClasses.Clear();
        SmokeFocalMethods.Clear();
        _selectedSmokeClass = string.Empty;
        _selectedSmokeMethod = null;
        SmokeDiscoverySummary = "Repository đã thay đổi. Nhấn Analyze repository để quét lại.";
        _config.Subjects.SmokeCandidateKey = string.Empty;
        _config.Subjects.SmokeFocalClass = string.Empty;
        _config.Subjects.SmokeFocalMethodSignature = string.Empty;
        OnPropertyChanged(nameof(SelectedSmokeClass));
        OnPropertyChanged(nameof(SelectedSmokeMethod));
        OnPropertyChanged(nameof(SmokeSelectionSummary));
        RefreshSmokeRunPreview(null);
    }

    private void RefreshSmokeRunPreview(SubjectCandidate? candidate)
    {
        if (!IsSmokeMode) return;
        Runs.Clear();
        if (candidate is null)
        {
            Runs.Add(new RunItemViewModel());
            return;
        }
        Runs.Add(new RunItemViewModel { SubjectId = "Smoke selection", FocalClass = candidate.FocalClass,
            FocalMethod = candidate.FocalMethodSignature, Approach = "DIRECT", Repetition = 1 });
        Runs.Add(new RunItemViewModel { SubjectId = "Smoke selection", FocalClass = candidate.FocalClass,
            FocalMethod = candidate.FocalMethodSignature, Approach = "PLANNED", Repetition = 1 });
    }

    private async Task LoadRunScheduleSafeAsync()
    {
        try
        {
            if (IsSmokeMode)
            {
                RefreshSmokeRunPreview(SelectedSmokeMethod);
                return;
            }
            var split = SelectedMode.Equals("Official", StringComparison.OrdinalIgnoreCase) ? "official" : "pilot";
            var output = Path.GetFullPath(Path.IsPathRooted(OutputRoot)
                ? OutputRoot : Path.Combine(_workspaceRoot, OutputRoot));
            var manifest = Path.Combine(output, "subjects", $"subjects-{split}.jsonl");
            if (!File.Exists(manifest))
            {
                Runs.Clear();
                Runs.Add(new RunItemViewModel
                {
                    SubjectId = $"No frozen {split} manifest",
                    Approach = "-",
                    Stage = "Run Subjects stage first"
                });
                return;
            }
            var subjects = new List<FocalMethodSubject>();
            foreach (var line in await File.ReadAllLinesAsync(manifest))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var subject = JsonSerializer.Deserialize<FocalMethodSubject>(line,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (subject is not null) subjects.Add(subject);
            }
            Runs.Clear();
            foreach (var subject in subjects.OrderBy(item => item.SubjectId, StringComparer.Ordinal))
            foreach (var approach in new[] { "DIRECT", "PLANNED" })
            for (var repetition = 1; repetition <= Repetitions; repetition++)
                Runs.Add(new RunItemViewModel
                {
                    SubjectId = subject.SubjectId,
                    FocalClass = subject.FocalClass,
                    FocalMethod = subject.FocalMethodSignature,
                    Approach = approach,
                    Repetition = repetition
                });
        }
        catch (Exception exception)
        {
            AppendLog("Unable to load focal-method schedule: " + exception.Message);
        }
    }

    private static string FriendlyExclusions(IEnumerable<string> reasons) => string.Join("; ", reasons.Select(reason => reason switch
    {
        "EXTERNAL_SERVICE_OR_GUI" => "phụ thuộc database/network/GUI/console I/O",
        "NONDETERMINISTIC_INPUT" => "input không deterministic",
        "MAIN_ENTRY_POINT" => "main entry point",
        "NO_STRUCTURAL_DECISION" => "không có branch/exception structural target",
        "TRIVIAL_ACCESSOR" => "getter/setter quá đơn giản",
        "NON_CALLABLE_VISIBILITY" => "method không thể gọi trực tiếp",
        "UNAVAILABLE_PARAMETER_TYPE" => "parameter type không truy cập được",
        "INACCESSIBLE_ENCLOSING_TYPE" => "enclosing class là private/protected",
        "LOCAL_OR_ANONYMOUS_TYPE" => "method thuộc local/anonymous class",
        "NO_ACCESSIBLE_CONSTRUCTION_PATH" => "không có đường khởi tạo instance hợp lệ",
        "NON_STATIC_INNER_WITHOUT_OUTER_FACTORY" => "non-static inner class không tạo được outer instance",
        "UNAVAILABLE_RUNTIME_CONTAINER" => "cần runtime/container không có trong unit-test environment",
        _ => reason
    }));

    private async Task SaveAsync()
    {
        UpdateConfigFromUi();
        await _configStore.SaveAsync(_config);
        await SaveApiKeysAsync();
        await SavePhaseProgressAsync();
        StatusMessage = "Configuration saved.";
        AppendLog("Saved rq1.json and local WPF state.");
    }

    public async Task CreateNewExperimentAsync(string requestedExperimentId)
    {
        if (IsBusy) return;
        var newId = requestedExperimentId.Trim();
        if (string.IsNullOrWhiteSpace(newId)
            || newId.Any(character => !(char.IsLetterOrDigit(character) || character is '-' or '_' or '.')))
            throw new ArgumentException(
                "Experiment name may contain only letters, digits, '.', '_' and '-'.");
        var existingIds = await _configStore.ListExperimentIdsAsync();
        if (existingIds.Contains(newId, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Experiment '{newId}' already exists. Enter another name or open the existing experiment.");

        UpdateConfigFromUi();
        await _configStore.SaveAsync(_config);
        _config.IsFrozen = false;
        ExperimentId = newId;
        SelectedMode = "Smoke";
        MaxOutputTokensPerCall = 65_536;
        TotalTokenCeilingPerRun = 65_536;
        MaxParallelLlmCalls = 4;
        MaxParallelToolRuns = 2;
        _config.ExperimentId = ExperimentId;
        _config.Mode = SelectedMode;
        // A new experiment is a new immutable treatment. Never carry an older
        // generation contract forward from the experiment that was open when
        // the user clicked "New experiment".
        _config.Generation.PipelineContractVersion = PipelineContractVersions.Current;
        _config.Generation.MaxOutputTokensPerCall = MaxOutputTokensPerCall;
        _config.Generation.TotalTokenCeilingPerRun = TotalTokenCeilingPerRun;
        _config.Generation.MaxParallelLlmCalls = MaxParallelLlmCalls;
        _config.Tools.MaxParallelToolRuns = MaxParallelToolRuns;
        _config.IsFrozen = false;
        await _configStore.SaveAsync(_config);
        await RefreshKnownExperimentsAsync();
        SelectedExistingExperimentId = ExperimentId;
        await LoadPhaseProgressAsync();
        ResetPipelineUi();
        OnPropertyChanged(nameof(IsConfigurationEditable));
        OnPropertyChanged(nameof(ConfigurationLockStatus));
        RaiseCommandStates();
        RaisePipelinePrerequisites();
        StatusMessage = "Created an editable experiment. Select Smoke, Pilot, or Official before running.";
        AppendLog($"Created new experiment {ExperimentId}; previous Official artifacts were preserved.");
    }

    public async Task DeleteSelectedExperimentAsync()
    {
        if (IsBusy || string.IsNullOrWhiteSpace(SelectedExistingExperimentId)) return;
        var deletingId = SelectedExistingExperimentId;
        if (deletingId.Equals(ExperimentId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Cannot delete the experiment currently open. Create or open another experiment first, then delete this one.");

        var deletingConfig = await _configStore.LoadExperimentAsync(deletingId)
                             ?? throw new InvalidOperationException("Experiment snapshot was not found: " + deletingId);
        var outputRoot = Path.GetFullPath(Path.IsPathRooted(deletingConfig.OutputRoot)
            ? deletingConfig.OutputRoot
            : Path.Combine(_workspaceRoot, deletingConfig.OutputRoot));
        DeleteExperimentDirectory(outputRoot, "runs", deletingId);
        DeleteExperimentDirectory(outputRoot, "ledger", deletingId);
        DeleteExperimentDirectory(outputRoot, "tool-workspaces", deletingId);

        var officialLock = ExperimentScopedLockPath("official-config-locks", deletingId);
        var phase5Lock = ExperimentScopedLockPath("phase5-prompt-context-locks", deletingId);
        if (File.Exists(officialLock)) File.Delete(officialLock);
        if (File.Exists(phase5Lock)) File.Delete(phase5Lock);
        await _configStore.DeleteExperimentAsync(deletingId);

        await RefreshKnownExperimentsAsync();
        SelectedExistingExperimentId = KnownExperimentIds.FirstOrDefault() ?? string.Empty;
        StatusMessage = $"Deleted experiment {deletingId} and its run, ledger, tool-workspace and lock artifacts.";
        AppendLog(StatusMessage);
        RaiseCommandStates();
    }

    private void DeleteExperimentDirectory(string outputRoot, string category, string experimentId)
    {
        var parent = Path.GetFullPath(Path.Combine(outputRoot, category));
        var target = Path.GetFullPath(Path.Combine(parent, experimentId));
        if (!string.Equals(Path.GetDirectoryName(target)?.TrimEnd(Path.DirectorySeparatorChar),
                parent.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(target), experimentId, StringComparison.Ordinal))
            throw new InvalidOperationException("Unsafe experiment deletion target: " + target);
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
    }

    private string ExperimentScopedLockPath(string directoryName, string experimentId)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(experimentId.Trim()))).ToLowerInvariant();
        return Path.Combine(_workspaceRoot, "experiments", "rq1", "state", directoryName, hash + ".json");
    }

    private async Task OpenExperimentAsync()
    {
        if (IsBusy || string.IsNullOrWhiteSpace(SelectedExistingExperimentId)) return;
        UpdateConfigFromUi();
        await _configStore.SaveAsync(_config);
        var selectedId = SelectedExistingExperimentId;
        var loaded = await _configStore.LoadExperimentAsync(selectedId)
                     ?? throw new InvalidOperationException("Experiment snapshot was not found: " + selectedId);
        _config = loaded;
        ExperimentId = loaded.ExperimentId;
        SelectedMode = loaded.Mode;
        RepositoryRoot = loaded.RepositoryRoot;
        OutputRoot = loaded.OutputRoot;
        JavaHome = loaded.Tools.JavaHome;
        ModulePath = loaded.Tools.ModulePath;
        Repetitions = loaded.Subjects.Repetitions;
        MaxOutputTokensPerCall = loaded.Generation.MaxOutputTokensPerCall;
        TotalTokenCeilingPerRun = loaded.Generation.TotalTokenCeilingPerRun;
        MaxParallelLlmCalls = loaded.Generation.MaxParallelLlmCalls;
        MaxParallelToolRuns = loaded.Tools.MaxParallelToolRuns;
        RequestTimeoutSeconds = loaded.Llm.TimeoutSeconds;
        DeepSeekThinkingMode = loaded.Llm.DeepSeekThinkingMode;
        BaseUrl = loaded.Llm.BaseUrl;
        OnPropertyChanged(nameof(SmokeSelectionSummary));
        _selectedProviderId = loaded.Llm.Provider;
        OnPropertyChanged(nameof(SelectedProviderId));
        OnPropertyChanged(nameof(IsDeepSeekProvider));
        SelectedModelId = loaded.Llm.ModelId;
        await LoadProviderDataAsync(keepSelectedModel: true);
        await _configStore.SaveAsync(loaded);
        await LoadPhaseProgressAsync();
        ResetPipelineUi();
        OnPropertyChanged(nameof(IsConfigurationEditable));
        OnPropertyChanged(nameof(ConfigurationLockStatus));
        RaiseCommandStates();
        RaisePipelinePrerequisites();
        StatusMessage = $"Opened experiment {loaded.ExperimentId} ({loaded.Mode}).";
        AppendLog(StatusMessage + " Existing immutable run artifacts will be reused.");
    }

    private async Task RefreshKnownExperimentsAsync()
    {
        var ids = await _configStore.ListExperimentIdsAsync();
        KnownExperimentIds.Clear();
        foreach (var id in ids) KnownExperimentIds.Add(id);
    }

    private async Task ValidateAsync()
    {
        UpdateConfigFromUi();
        var keys = ApiKeyParser.Parse(ApiKeysText);
        var result = ExperimentConfigValidator.Validate(_config, keys.Count);
        StatusMessage = result.IsValid
            ? "Configuration is valid for the selected provider/model."
            : string.Join(" ", result.Errors);
        AppendLog(result.IsValid ? "Configuration validation PASSED." : "Configuration validation FAILED: " + StatusMessage);
        await Task.CompletedTask;
    }

    private void ValidateApiKeys()
    {
        var keys = ApiKeyParser.Parse(ApiKeysText);
        ApiKeyStatus = $"Valid keys: {keys.Count}; provider: {SelectedProviderId}";
        AppendLog($"Parsed {keys.Count} unique non-empty keys for {SelectedProviderId}.");
    }

    private async Task SaveApiKeysAsync()
    {
        var keys = ApiKeyParser.Parse(ApiKeysText);
        await _apiKeyStore.SaveAsync(SelectedProviderId, keys);
        await _apiKeyPool.ResetProviderPoolAsync(SelectedProviderId);
        ApiKeyStatus = $"Saved {keys.Count} key(s) for {SelectedProviderId}.";
    }

    private async Task LoadProviderDataSafeAsync()
    {
        try
        {
            await LoadProviderDataAsync(keepSelectedModel: false);
        }
        catch (Exception exception)
        {
            HandleError(exception);
        }
    }

    private async Task LoadProviderDataAsync(bool keepSelectedModel)
    {
        var previousModel = keepSelectedModel ? SelectedModelId : string.Empty;
        await LoadModelsAsync();
        var keys = await _apiKeyStore.LoadAsync(SelectedProviderId);
        ApiKeysText = string.Join(Environment.NewLine, keys);
        ApiKeyStatus = $"Loaded {keys.Count} key(s) for {SelectedProviderId}.";
        if (keepSelectedModel || !string.IsNullOrWhiteSpace(previousModel))
            SelectedModelId = previousModel;
        else
            SelectedModelId = AvailableModels.FirstOrDefault() ?? string.Empty;
    }

    private async Task LoadModelsAsync()
    {
        var selected = SelectedModelId;
        var models = await _modelCatalog.GetModelsAsync(SelectedProviderId);
        AvailableModels.Clear();
        foreach (var model in models)
            AvailableModels.Add(model);
        if (!string.IsNullOrWhiteSpace(selected))
            SelectedModelId = selected;
        StatusMessage = $"Loaded {models.Count} model(s) for {SelectedProviderId}; exact model ID may be typed manually.";
    }

    private async Task RefreshModelsAsync()
    {
        await SaveApiKeysAsync();
        var lease = await _apiKeyPool.AcquireAsync(SelectedProviderId);
        if (lease is null)
        {
            StatusMessage = $"No usable API key for {SelectedProviderId}; add/save a key before reloading models.";
            AppendLog(StatusMessage);
            return;
        }

        UpdateConfigFromUi();
        StatusMessage = $"Loading models from {SelectedProviderId}...";
        var result = await _modelCatalog.RefreshAsync(
            SelectedProviderId,
            _config.Llm.BaseUrl,
            lease.Secret);
        AvailableModels.Clear();
        foreach (var model in result.Models)
            AvailableModels.Add(model);
        if (result.IsSuccess && (string.IsNullOrWhiteSpace(SelectedModelId) || !AvailableModels.Contains(SelectedModelId)))
            SelectedModelId = AvailableModels.First();
        StatusMessage = result.Message;
        AppendLog(result.Message);
    }

    private async Task TestProviderSetupAsync()
    {
        var client = _llmClientFactory.Create(SelectedProviderId);
        var lease = await _apiKeyPool.AcquireAsync(SelectedProviderId);
        StatusMessage = lease is null
            ? $"No usable API key for {client.ProviderId}."
            : $"Provider setup ready: {client.ProviderId}, {lease.SlotId}. Remote DIRECT generation is available in Phase 6.";
        AppendLog(StatusMessage);
    }

    private async Task RunFullPipelineAsync()
    {
        UpdateConfigFromUi();
        var keys = ApiKeyParser.Parse(ApiKeysText);
        var validation = ExperimentConfigValidator.Validate(_config, keys.Count);
        if (!validation.IsValid)
        {
            StatusMessage = string.Join(" ", validation.Errors);
            AppendLog("Pipeline did not start: " + StatusMessage);
            return;
        }

        await SaveAsync();
        if (_config.Mode.Equals("Official", StringComparison.OrdinalIgnoreCase))
        {
            if (!_config.IsFrozen)
            {
                await _officialConfigLock.UpdateLockAsync(_config);
                _config.IsFrozen = true;
                await _configStore.SaveAsync(_config);
                OnPropertyChanged(nameof(IsConfigurationEditable));
                OnPropertyChanged(nameof(ConfigurationLockStatus));
                RaiseCommandStates();
            }
            else
            {
                var lockResult = await _officialConfigLock.ValidateOrCreateAsync(_config);
                if (!lockResult.IsAccepted)
                {
                    StatusMessage = lockResult.Message;
                    AppendLog("Official lock REJECTED: " + lockResult.Message);
                    return;
                }
            }
        }
        ResetPipelineUi();
        AppendLog(EffectiveLlmSettingsSummary());
        IsBusy = true;
        _runCancellation = new CancellationTokenSource();
        var progress = new Progress<PipelineProgress>(ApplyProgress);
        var result = await _orchestrator.RunAsync(_config, progress, _runCancellation.Token);
        IsBusy = false;
        StatusMessage = result.Message;
        FinalizeVisibleRunStatuses(result);
        AppendLog(result.Message);
    }

    private async Task ResumeAsync()
    {
        UpdateConfigFromUi();
        if (_config.Mode.Equals("Official", StringComparison.OrdinalIgnoreCase))
        {
            if (!_config.IsFrozen)
            {
                await _officialConfigLock.UpdateLockAsync(_config);
                _config.IsFrozen = true;
                await _configStore.SaveAsync(_config);
                OnPropertyChanged(nameof(IsConfigurationEditable));
                OnPropertyChanged(nameof(ConfigurationLockStatus));
                RaiseCommandStates();
            }
            else
            {
                var lockResult = await _officialConfigLock.ValidateOrCreateAsync(_config);
                if (!lockResult.IsAccepted)
                {
                    StatusMessage = lockResult.Message;
                    AppendLog("Official resume REJECTED: " + lockResult.Message);
                    return;
                }
            }
        }
        AppendLog("Resuming with " + EffectiveLlmSettingsSummary());
        IsBusy = true;
        _runCancellation = new CancellationTokenSource();
        var result = await _orchestrator.ResumeAsync(
            _config,
            new Progress<PipelineProgress>(ApplyProgress),
            _runCancellation.Token);
        IsBusy = false;
        StatusMessage = result.Message;
        FinalizeVisibleRunStatuses(result);
        AppendLog(result.Message);
    }

    private async Task RetryFailedRunsAsync()
    {
        UpdateConfigFromUi();
        var keys = ApiKeyParser.Parse(ApiKeysText);
        var validation = ExperimentConfigValidator.Validate(_config, keys.Count);
        if (!validation.IsValid)
        {
            StatusMessage = string.Join(" ", validation.Errors);
            AppendLog("Retry did not start: " + StatusMessage);
            return;
        }

        await SaveAsync();
        if (_config.Mode.Equals("Official", StringComparison.OrdinalIgnoreCase))
        {
            await _officialConfigLock.UpdateLockAsync(_config);
            _config.IsFrozen = true;
            await _configStore.SaveAsync(_config);
            OnPropertyChanged(nameof(IsConfigurationEditable));
            OnPropertyChanged(nameof(ConfigurationLockStatus));
            RaiseCommandStates();
        }

        AppendLog("Retrying failed runs with updated budget: " + EffectiveLlmSettingsSummary());
        IsBusy = true;
        _runCancellation = new CancellationTokenSource();
        var progress = new Progress<PipelineProgress>(ApplyProgress);
        var result = await _orchestrator.RetryFailedRunsAsync(
            _config,
            progress,
            _runCancellation.Token);
        IsBusy = false;
        StatusMessage = result.Message;
        FinalizeVisibleRunStatuses(result);
        AppendLog(result.Message);
    }

    private void Pause()
    {
        _orchestrator.PauseAfterCurrentStage();
        StatusMessage = "Pause requested; the current stage will finish first.";
    }

    private void Cancel()
    {
        _runCancellation?.Cancel();
        StatusMessage = "Cancellation requested.";
    }

    private void ApplyProgress(PipelineProgress progress)
    {
        var step = PipelineSteps.FirstOrDefault(item => item.Id == progress.StageId);
        if (step is not null)
        {
            step.State = progress.State;
            step.Message = progress.Message;
        }

        OverallProgress = progress.TotalStages == 0
            ? 0
            : 100d * progress.CompletedStages / progress.TotalStages;
        ApplyRunLevelProgress(progress);
        StatusMessage = progress.Message;
        AppendLog($"{progress.Timestamp:HH:mm:ss} [{progress.StageId}] {progress.State}: {progress.Message}");
        if (progress.StageId == "preflight" && progress.State == PipelineStageState.Passed)
        {
            var phase = Phases.FirstOrDefault(item => item.PhaseId == 2);
            phase?.MarkCompletedFromEvidence("Automated preflight passed; see subjects/preflight-report.json and retained tool evidence.");
        }
        if (progress.StageId == "subjects" && progress.State == PipelineStageState.Passed)
        {
            var phase = Phases.FirstOrDefault(item => item.PhaseId == 3);
            phase?.MarkCompletedFromEvidence(IsSmokeMode
                ? "Selected Smoke focal method was revalidated and frozen with exact signature/source hash; see subjects/subjects-smoke.jsonl and smoke-subject-report.json."
                : "AST extraction, deterministic stratified selection, source hashing, disjointness checks, and manual review passed; see experiments/rq1/subjects/subject-selection-report.json.");
        }
        if (progress.StageId == "targets" && progress.State == PipelineStageState.Passed)
        {
            var phase = Phases.FirstOrDefault(item => item.PhaseId == 4);
            phase?.MarkCompletedFromEvidence(
                "Three-run deterministic AST target extraction passed; target IDs/source ranges and frozen FULL_BRANCH_SITE_COVERAGE semantics verified; see experiments/rq1/targets/target-extraction-report.json.");
        }
        if (progress.StageId == "context-prompts" && progress.State == PipelineStageState.Passed)
        {
            var phase = Phases.FirstOrDefault(item => item.PhaseId == 5);
            phase?.MarkCompletedFromEvidence(
                "Common contexts, strict output schemas, treatment isolation, prompt hashes, and official lock passed; see experiments/rq1/phase5-context-prompt-report.json.");
        }
        if (progress.StageId == "direct" && progress.State == PipelineStageState.Passed)
        {
            var phase = Phases.FirstOrDefault(item => item.PhaseId == 6);
            phase?.MarkCompletedFromEvidence(
                "DIRECT zero-shot completed with one semantic call/run, no Planner/Repair trace, strict output validation, token/test limits, and retained invalid artifacts; see experiments/rq1/direct-baseline-report.json.");
        }
        if (progress.StageId == "planned" && progress.State == PipelineStageState.Passed)
        {
            var phase = Phases.FirstOrDefault(item => item.PhaseId == 7);
            phase?.MarkCompletedFromEvidence(
                "Planner/Generator strict contracts, target validation, scenario-test bijection, parameterized-test rejection, token cap, and zero Repair/Reviewer calls passed; see experiments/rq1/planned-pipeline-report.json.");
        }
        if (progress.StageId == "adequacy" && progress.State == PipelineStageState.Passed)
        {
            var phase = Phases.FirstOrDefault(item => item.PhaseId == 8);
            phase?.MarkCompletedFromEvidence(
                "Isolated gated Compile/JUnit/JaCoCo/PIT execution passed; generated-test filtering and focal-method mutation filtering verified; see experiments/rq1/compile-test-report.json and adequacy-report.json.");
        }
        if (progress.StageId == "ledger" && progress.State == PipelineStageState.Passed)
        {
            var phase = Phases.FirstOrDefault(item => item.PhaseId == 9);
            phase?.MarkCompletedFromEvidence(
                "Authoritative runs.csv and per-run metrics were rebuilt from immutable artifacts with unique-key, required-hash, failed-run retention and treatment-contract validation; see experiments/rq1/metrics-ledger-report.json.");
        }
    }

    private static string ResolveRunStatus(PipelineRunResult result) =>
        result.IsCompleted
            ? "COMPLETED"
            : result.IsCancelled
                ? "CANCELLED"
                : result.Message.Contains("FAILED", StringComparison.OrdinalIgnoreCase)
                    ? "FAILED"
                    : result.Message.Contains("WAITING_FOR_APPROVAL", StringComparison.OrdinalIgnoreCase)
                        ? "WAITING_FOR_APPROVAL"
                        : "PAUSED";

    private void FinalizeVisibleRunStatuses(PipelineRunResult result)
    {
        if (result.IsCompleted)
        {
            foreach (var run in Runs)
            {
                run.Stage = "Pipeline completed";
                run.Status = "COMPLETED";
            }
            return;
        }

        // A stage-level failure/cancellation must not make every not-yet-run
        // schedule row look as though it executed and failed.
        var terminalStatus = ResolveRunStatus(result);
        foreach (var run in Runs.Where(item => item.Status == "RUNNING"))
            run.Status = terminalStatus;
    }

    private void ApplyRunLevelProgress(PipelineProgress progress)
    {
        // Preflight, subject selection, target extraction, and prompt building
        // are shared preparation stages. Their state belongs in Pipeline status,
        // not on every individual DIRECT/PLANNED run row.
        if (!TryResolveRun(progress, out var subjectId, out var approach, out var repetition))
        {
            if (progress.State is PipelineStageState.Passed or PipelineStageState.Failed
                or PipelineStageState.Cancelled or PipelineStageState.WaitingForApiKey)
            {
                var terminal = progress.State switch
                {
                    PipelineStageState.Passed => "PROCESSED",
                    PipelineStageState.WaitingForApiKey => "WAITING_FOR_API_KEY",
                    _ => progress.State.ToString().ToUpperInvariant()
                };
                foreach (var run in Runs.Where(item => item.Status == "RUNNING"))
                    run.Status = terminal;
            }
            return;
        }

        foreach (var previous in Runs.Where(item => item.Status == "RUNNING"
                                                    && (item.SubjectId != subjectId
                                                        || item.Approach != approach
                                                        || item.Repetition != repetition)))
            previous.Status = "PROCESSED";

        var current = Runs.FirstOrDefault(item => item.SubjectId == subjectId
                                                 && item.Approach == approach
                                                 && item.Repetition == repetition);
        if (current is null) return;
        current.Stage = progress.StageName;
        current.Status = progress.State == PipelineStageState.Running
            ? "RUNNING"
            : progress.State.ToString().ToUpperInvariant();
    }

    private static bool TryResolveRun(
        PipelineProgress progress,
        out string subjectId,
        out string approach,
        out int repetition)
    {
        var match = ArtifactRunRegex.Match(progress.Message);
        if (!match.Success) match = ToolRunRegex.Match(progress.Message);
        if (match.Success)
        {
            subjectId = match.Groups["subject"].Value;
            approach = match.Groups["approach"].Value.ToUpperInvariant();
            repetition = int.Parse(match.Groups["repetition"].Value);
            return true;
        }

        match = GenerationRunRegex.Match(progress.Message);
        if (match.Success && progress.StageId is "direct" or "planned")
        {
            subjectId = match.Groups["subject"].Value;
            approach = progress.StageId == "direct" ? "DIRECT" : "PLANNED";
            repetition = int.Parse(match.Groups["repetition"].Value);
            return true;
        }

        subjectId = string.Empty;
        approach = string.Empty;
        repetition = 0;
        return false;
    }

    private void ResetPipelineUi()
    {
        foreach (var step in PipelineSteps)
        {
            step.State = PipelineStageState.Pending;
            step.Message = string.Empty;
        }
        OverallProgress = 0;
        foreach (var run in Runs)
        {
            run.Stage = "Waiting for run stage";
            run.Status = "PENDING";
        }
    }

    private async Task LoadPhaseProgressAsync()
    {
        var document = await _phaseProgressStore.LoadAsync(ExperimentId);
        Phases.Clear();
        foreach (var record in document.Phases)
        {
            var item = PhaseProgressItemViewModel.FromRecord(record);
            item.PropertyChanged += PhaseItemOnPropertyChanged;
            Phases.Add(item);
        }
    }

    private async void PhaseItemOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PhaseProgressItemViewModel.IsCompleted)
            or nameof(PhaseProgressItemViewModel.Notes))
        {
            try
            {
                await SavePhaseProgressAsync();
            }
            catch (Exception exception)
            {
                HandleError(exception);
            }
        }
    }

    private Task SavePhaseProgressAsync() => _phaseProgressStore.SaveAsync(new PhaseProgressDocument
    {
        ExperimentId = ExperimentId,
        Phases = Phases.Select(item => item.ToRecord()).ToList()
    });

    private void UpdateConfigFromUi()
    {
        _config.ExperimentId = ExperimentId.Trim();
        _config.Mode = SelectedMode;
        _config.RepositoryRoot = RepositoryRoot.Trim();
        _config.OutputRoot = OutputRoot.Trim();
        _config.Tools.JavaHome = JavaHome.Trim();
        _config.Tools.ModulePath = string.IsNullOrWhiteSpace(ModulePath) ? "." : ModulePath.Trim();
        _config.Subjects.Repetitions = Repetitions;
        _config.Generation.MaxOutputTokensPerCall = MaxOutputTokensPerCall;
        _config.Generation.TotalTokenCeilingPerRun = TotalTokenCeilingPerRun;
        _config.Generation.MaxParallelLlmCalls = MaxParallelLlmCalls;
        _config.Tools.MaxParallelToolRuns = MaxParallelToolRuns;
        _config.Llm.Provider = SelectedProviderId;
        _config.Llm.ModelId = SelectedModelId.Trim();
        _config.Llm.BaseUrl = BaseUrl.Trim();
        _config.Llm.TimeoutSeconds = RequestTimeoutSeconds;
        _config.Llm.DeepSeekThinkingMode = DeepSeekThinkingMode;
        _config.Llm.ApiKeyStrategy = "round-robin-failover";
    }

    private string EffectiveLlmSettingsSummary() =>
        $"LLM settings: provider={SelectedProviderId}, model={SelectedModelId}, " +
        $"timeout={RequestTimeoutSeconds}s, thinking=" +
        (IsDeepSeekProvider ? DeepSeekThinkingMode : "provider-specific/default") +
        $", parallel LLM={MaxParallelLlmCalls}, parallel tools={MaxParallelToolRuns}.";

    private bool CanEdit() => !IsBusy && !_config.IsFrozen;

    private void SetBusy(bool value) => IsBusy = value;

    private void RaiseCommandStates()
    {
        foreach (var command in new ICommand[]
        {
            LoadCommand, SaveCommand, ValidateCommand, SaveApiKeysCommand,
            RefreshModelsCommand, TestProviderConnectionCommand, RunFullPipelineCommand,
            PauseCommand, CancelCommand, ResumeCommand, BrowseRepositoryCommand,
            BrowseJavaHomeCommand,
            OpenResultsWindowCommand, SavePhaseProgressCommand,
            CreateNewExperimentCommand, OpenExperimentCommand, DeleteExperimentCommand,
            AnalyzeSmokeRepositoryCommand, RetryFailedRunsCommand, UnlockConfigurationCommand,
            ReloadPromptsCommand
        })
        {
            switch (command)
            {
                case RelayCommand relay: relay.RaiseCanExecuteChanged(); break;
                case AsyncRelayCommand asyncRelay: asyncRelay.RaiseCanExecuteChanged(); break;
            }
        }
    }

    private void RaisePipelinePrerequisites()
    {
        OnPropertyChanged(nameof(CanStartPipeline));
        OnPropertyChanged(nameof(RunPrerequisiteMessage));
        (RunFullPipelineCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private void AppendLog(string message) =>
        LogText += $"{DateTimeOffset.Now:HH:mm:ss} {message}{Environment.NewLine}";

    private void HandleError(Exception exception)
    {
        IsBusy = false;
        foreach (var step in PipelineSteps.Where(item => item.State == PipelineStageState.Running))
        {
            step.State = PipelineStageState.Failed;
            step.Message = exception.Message;
        }
        foreach (var run in Runs) run.Status = "FAILED";
        StatusMessage = exception.Message;
        AppendLog("ERROR: " + exception);
    }
}
