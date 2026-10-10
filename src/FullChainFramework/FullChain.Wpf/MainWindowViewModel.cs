using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using FullChain.Benchmark;
using FullChain.Contracts;
using FullChain.Core;
using FullChain.Core.Orchestration;
using FullChain.Core.Repair;
using FullChain.Core.Verification;
using FullChain.Infrastructure.Evaluation;
using FullChain.Infrastructure.Evidence;
using FullChain.Infrastructure.Java;
using FullChain.Infrastructure.LLM;
using FullChain.Infrastructure.Persistence;
using FullChain.Infrastructure.Processes;
using FullChain.Infrastructure.Rq1;
using FullChain.Infrastructure.Rq2;
using FullChain.Infrastructure.Rq3;
using FullChain.Infrastructure.Workspace;
using FullChain.Runner;

namespace FullChain.Wpf;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute == null || _canExecute();
    public void Execute(object? parameter) => _execute();
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class StageRowViewModel : ObservableObject
{
    private string _phase = string.Empty;
    private string _treatment = string.Empty;
    private string _targetId = string.Empty;
    private int _repetition;
    private string _status = string.Empty;
    private string _reason = string.Empty;
    private string _evidencePath = string.Empty;
    private string _duration = string.Empty;
    private string _details = string.Empty;

    public string Phase { get => _phase; set => SetProperty(ref _phase, value); }
    public string Treatment { get => _treatment; set => SetProperty(ref _treatment, value); }
    public string TargetId { get => _targetId; set => SetProperty(ref _targetId, value); }
    public int Repetition { get => _repetition; set => SetProperty(ref _repetition, value); }
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public string Reason { get => _reason; set => SetProperty(ref _reason, value); }
    public string EvidencePath { get => _evidencePath; set => SetProperty(ref _evidencePath, value); }
    public string Duration { get => _duration; set => SetProperty(ref _duration, value); }
    public string Details { get => _details; set => SetProperty(ref _details, value); }
}

/// <summary>
/// Read-only, operator-facing summary of one campaign directory.  The source of
/// truth remains raw-records.jsonl and availability-ledger.json in that directory;
/// this type deliberately does not maintain a separate UI database.
/// </summary>
public sealed class CampaignRunViewModel
{
    public required string CampaignId { get; init; }
    public required string RunDirectory { get; init; }
    public required DateTime LastUpdatedUtc { get; init; }
    public required string LastUpdated { get; init; }
    public required string PairProgress { get; init; }
    public required string FullChainB2 { get; init; }
    public required string EvoSuiteB2 { get; init; }
    public required string AverageBranchCoverage { get; init; }
    public required string Readiness { get; init; }
    public required int CompletedPairs { get; init; }
    public required int ExpectedPairs { get; init; }
    public required bool IsComplete { get; init; }
}

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly string _repoRoot;
    private FullChainOperatorSettings _settings;
    private CancellationTokenSource? _cts;
    private IDisposable? _activeLock;

    private bool _isRunning;
    private string _currentPhase = "Idle";
    private string _currentTargetNumber = "0 / 0";
    private int _currentRepairRound;
    private string _currentVerdict = "N/A";
    private string _currentTreatment = "N/A";
    private string _statusMessage = "Ready";
    private string _processFailureDetail = string.Empty;
    private StageRowViewModel? _selectedStageRow;
    private CampaignRunViewModel? _selectedRun;
    private string _campaignId = string.Empty;
    private string _logFilter = string.Empty;
    private bool _autoScroll = true;
    private readonly StringBuilder _logBuilder = new();
    private string _logText = string.Empty;

    public MainWindowViewModel(string? repoRoot = null)
    {
        _repoRoot = repoRoot ?? FindRepoRoot();
        _settings = LocalSettingsStore.LoadSettings(_repoRoot);

        StageRows = new ObservableCollection<StageRowViewModel>();
        Runs = new ObservableCollection<CampaignRunViewModel>();
        _campaignId = GenerateCampaignId("one");

        PreflightCommand = new RelayCommand(async () => await RunPreflightAsync(), () => !IsRunning);
        RunOneTargetCommand = new RelayCommand(async () => await RunOneTargetAsync(), () => !IsRunning);
        RunPilotCommand = new RelayCommand(async () => await RunPilotAsync(), () => !IsRunning);
        RunOfficialCommand = new RelayCommand(async () => await RunOfficialAsync(), () => !IsRunning);
        CancelCommand = new RelayCommand(CancelCurrentRun, () => IsRunning);
        ResumeCommand = new RelayCommand(async () => await ResumeCurrentRunAsync(), () => !IsRunning);
        OpenEvidenceCommand = new RelayCommand(OpenSelectedEvidence, () => SelectedStageRow != null && !string.IsNullOrEmpty(SelectedStageRow.EvidencePath));
        RefreshRunsCommand = new RelayCommand(RefreshRuns, () => !IsRunning);
        NewRunIdCommand = new RelayCommand(CreateNewCampaignId, () => !IsRunning);
        OpenRunFolderCommand = new RelayCommand(OpenSelectedRunFolder, () => SelectedRun != null);
        CopyLogCommand = new RelayCommand(CopyFullLog);

        AppendLog($"[FullChainFramework] Operator UI Initialized at {_repoRoot}");
        AppendLog($"[FullChainFramework] Model: {_settings.ModelId} | Endpoint: {_settings.Endpoint}");
        AppendLog($"[FullChainFramework] Tool paths resolved: Java={_settings.JavaHome}, Maven={_settings.MavenHome}");
        RefreshRuns();
    }

    public ObservableCollection<StageRowViewModel> StageRows { get; }
    public ObservableCollection<CampaignRunViewModel> Runs { get; }

    public string Endpoint
    {
        get => _settings.Endpoint;
        set
        {
            _settings = _settings with { Endpoint = value };
            OnPropertyChanged();
            SaveSettings();
        }
    }

    public string ModelId
    {
        get => _settings.ModelId;
        set
        {
            _settings = _settings with { ModelId = value };
            OnPropertyChanged();
            SaveSettings();
        }
    }

    public string ApiKey
    {
        get => _settings.ApiKey;
        set
        {
            _settings = _settings with { ApiKey = value };
            OnPropertyChanged();
            SaveSettings();
        }
    }

    public string JavaHome
    {
        get => _settings.JavaHome;
        set
        {
            _settings = _settings with { JavaHome = value };
            OnPropertyChanged();
            SaveSettings();
        }
    }

    public string MavenHome
    {
        get => _settings.MavenHome;
        set
        {
            _settings = _settings with { MavenHome = value };
            OnPropertyChanged();
            SaveSettings();
        }
    }

    public string EvosuiteJar
    {
        get => _settings.EvosuiteJar;
        set
        {
            _settings = _settings with { EvosuiteJar = value };
            OnPropertyChanged();
            SaveSettings();
        }
    }

    public string TargetManifestPath
    {
        get => _settings.TargetManifestPath;
        set
        {
            _settings = _settings with { TargetManifestPath = value };
            OnPropertyChanged();
        }
    }

    public string SelectedSubjectId
    {
        get => _settings.SelectedSubjectId;
        set
        {
            _settings = _settings with { SelectedSubjectId = value };
            OnPropertyChanged();
        }
    }

    public int SelectedRepetition
    {
        get => _settings.SelectedRepetition;
        set
        {
            _settings = _settings with { SelectedRepetition = value };
            OnPropertyChanged();
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                ((RelayCommand)PreflightCommand).RaiseCanExecuteChanged();
                ((RelayCommand)RunOneTargetCommand).RaiseCanExecuteChanged();
                ((RelayCommand)RunPilotCommand).RaiseCanExecuteChanged();
                ((RelayCommand)RunOfficialCommand).RaiseCanExecuteChanged();
                ((RelayCommand)CancelCommand).RaiseCanExecuteChanged();
                ((RelayCommand)ResumeCommand).RaiseCanExecuteChanged();
                ((RelayCommand)RefreshRunsCommand).RaiseCanExecuteChanged();
                ((RelayCommand)NewRunIdCommand).RaiseCanExecuteChanged();
                ((RelayCommand)OpenRunFolderCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public string CurrentPhase { get => _currentPhase; private set => SetProperty(ref _currentPhase, value); }
    public string CurrentTargetNumber { get => _currentTargetNumber; private set => SetProperty(ref _currentTargetNumber, value); }
    public int CurrentRepairRound { get => _currentRepairRound; private set => SetProperty(ref _currentRepairRound, value); }
    public string CurrentVerdict { get => _currentVerdict; private set => SetProperty(ref _currentVerdict, value); }
    public string CurrentTreatment { get => _currentTreatment; private set => SetProperty(ref _currentTreatment, value); }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public string ProcessFailureDetail { get => _processFailureDetail; private set => SetProperty(ref _processFailureDetail, value); }

    public StageRowViewModel? SelectedStageRow
    {
        get => _selectedStageRow;
        set
        {
            if (SetProperty(ref _selectedStageRow, value))
            {
                ((RelayCommand)OpenEvidenceCommand).RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// ID reserved for the next new campaign.  It is generated automatically at
    /// startup and after a successful run, but an operator may replace it with a
    /// meaningful, filesystem-safe identifier before starting the run.
    /// </summary>
    public string CampaignId
    {
        get => _campaignId;
        set => SetProperty(ref _campaignId, value);
    }

    public CampaignRunViewModel? SelectedRun
    {
        get => _selectedRun;
        set
        {
            if (SetProperty(ref _selectedRun, value))
            {
                ((RelayCommand)OpenRunFolderCommand).RaiseCanExecuteChanged();
                ((RelayCommand)ResumeCommand).RaiseCanExecuteChanged();
                if (value != null)
                {
                    LoadSelectedRun(value);
                }
            }
        }
    }

    public string LogFilter
    {
        get => _logFilter;
        set
        {
            if (SetProperty(ref _logFilter, value))
            {
                OnPropertyChanged(nameof(FilteredLogText));
            }
        }
    }

    public bool AutoScroll { get => _autoScroll; set => SetProperty(ref _autoScroll, value); }

    public string LogText => _logText;

    public string FilteredLogText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_logFilter)) return _logText;
            var lines = _logText.Split('\n');
            var filtered = lines.Where(l => l.Contains(_logFilter, StringComparison.OrdinalIgnoreCase));
            return string.Join('\n', filtered);
        }
    }

    public ICommand PreflightCommand { get; }
    public ICommand RunOneTargetCommand { get; }
    public ICommand RunPilotCommand { get; }
    public ICommand RunOfficialCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand OpenEvidenceCommand { get; }
    public ICommand RefreshRunsCommand { get; }
    public ICommand NewRunIdCommand { get; }
    public ICommand OpenRunFolderCommand { get; }
    public ICommand CopyLogCommand { get; }

    /// <summary>
    /// Rebuilds the historical-runs list from evidence already on disk.  This is
    /// intentionally read-only: reopening the operator console cannot alter a
    /// campaign or its scientific artifacts.
    /// </summary>
    public void RefreshRuns()
    {
        RefreshRuns(preferredCampaignId: null);
    }

    private void RefreshRuns(string? preferredCampaignId)
    {
        var selectedCampaignId = preferredCampaignId ?? SelectedRun?.CampaignId;
        var runsRoot = Path.Combine(_repoRoot, "experiments", "full-chain", "runs");
        var discovered = new List<CampaignRunViewModel>();

        if (Directory.Exists(runsRoot))
        {
            foreach (var directory in Directory.GetDirectories(runsRoot))
            {
                try
                {
                    var info = new DirectoryInfo(directory);
                    var records = LoadExistingRecords(Path.Combine(directory, "raw-records.jsonl"));
                    var ledger = TryReadLedger(Path.Combine(directory, "availability-ledger.json"));
                    discovered.Add(BuildRunSummary(info, records, ledger));
                }
                catch (Exception ex)
                {
                    // A partially written or manually copied run must not prevent
                    // the rest of the operator history from being viewable.
                    Debug.WriteLine($"Could not read campaign directory '{directory}': {ex.Message}");
                }
            }
        }

        Runs.Clear();
        foreach (var run in discovered.OrderByDescending(run => run.LastUpdatedUtc).ThenBy(run => run.CampaignId, StringComparer.Ordinal))
        {
            Runs.Add(run);
        }

        var selected = !string.IsNullOrWhiteSpace(selectedCampaignId)
            ? Runs.FirstOrDefault(run => string.Equals(run.CampaignId, selectedCampaignId, StringComparison.Ordinal))
            : Runs.FirstOrDefault();
        SelectedRun = selected;

        if (selected == null)
        {
            StageRows.Clear();
            SelectedStageRow = null;
        }
    }

    public void CreateNewCampaignId()
    {
        CampaignId = GenerateCampaignId("run");
        StatusMessage = $"New run ID prepared: {CampaignId}";
        AppendLog($"[Run Setup] New campaign ID prepared: {CampaignId}");
    }

    public void OpenSelectedRunFolder()
    {
        if (SelectedRun == null) return;

        try
        {
            OpenInFileBrowser(SelectedRun.RunDirectory);
            AppendLog($"[Open Run] Opened campaign directory: {SelectedRun.RunDirectory}");
        }
        catch (Exception ex)
        {
            AppendLog($"[Open Run Error] {ex.Message}");
        }
    }

    private string? TryGetNewCampaignId(string runKind)
    {
        var candidate = CampaignId.Trim();
        if (string.IsNullOrEmpty(candidate))
        {
            candidate = GenerateCampaignId(runKind);
            CampaignId = candidate;
        }

        if (candidate.Length > 120 ||
            candidate.Any(character => !(char.IsLetterOrDigit(character) || character is '-' or '_' or '.')))
        {
            StatusMessage = "Campaign ID may contain only letters, numbers, '-', '_' and '.'.";
            AppendLog($"[Run Setup] Invalid campaign ID rejected: '{candidate}'.");
            return null;
        }

        var runDirectory = Path.Combine(_repoRoot, "experiments", "full-chain", "runs", candidate);
        if (Directory.Exists(runDirectory))
        {
            StatusMessage = "That campaign ID already exists. Click New Run ID, or select it and Resume.";
            AppendLog($"[Run Setup] Existing campaign directory rejected for a new run: {runDirectory}");
            return null;
        }

        return candidate;
    }

    private string GenerateCampaignId(string runKind)
    {
        var safeKind = string.IsNullOrWhiteSpace(runKind) ? "run" : runKind;
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var baseId = $"wpf-{safeKind}-{timestamp}";
        var runsRoot = Path.Combine(_repoRoot, "experiments", "full-chain", "runs");
        var candidate = baseId;
        var suffix = 1;
        while (Directory.Exists(Path.Combine(runsRoot, candidate)))
        {
            candidate = $"{baseId}-{suffix++:D2}";
        }
        return candidate;
    }

    private void LoadSelectedRun(CampaignRunViewModel run)
    {
        var records = LoadExistingRecords(Path.Combine(run.RunDirectory, "raw-records.jsonl"));
        StageRows.Clear();
        foreach (var record in records.OrderBy(record => record.SubjectId).ThenBy(record => record.RepetitionId).ThenBy(record => record.Treatment, StringComparer.Ordinal))
        {
            AddRecordToUi(record, run.RunDirectory);
        }

        CurrentPhase = "Viewing saved run";
        CurrentTargetNumber = run.PairProgress;
        CurrentTreatment = "FullChain + EvoSuite";
        CurrentVerdict = run.Readiness;
        ProcessFailureDetail = $"Campaign: {run.CampaignId}\n" +
                               $"Directory: {run.RunDirectory}\n" +
                               $"Pairs: {run.PairProgress}\n" +
                               $"FullChain B2: {run.FullChainB2}\n" +
                               $"EvoSuite B2: {run.EvoSuiteB2}\n" +
                               $"Average branch coverage: {run.AverageBranchCoverage}\n" +
                               $"Readiness: {run.Readiness}";
        StatusMessage = $"Viewing run '{run.CampaignId}' ({run.PairProgress}).";
    }

    private static CampaignRunViewModel BuildRunSummary(
        DirectoryInfo directory,
        IReadOnlyList<FullChainRecord> records,
        LedgerSnapshot? ledger)
    {
        var pairs = records.GroupBy(record => (record.SubjectId, record.RepetitionId)).ToList();
        var inferredCompletedPairs = pairs.Count(pair =>
            pair.Any(record => string.Equals(record.Treatment, "FullChain", StringComparison.Ordinal)) &&
            pair.Any(record => string.Equals(record.Treatment, "EvoSuite", StringComparison.Ordinal)));
        var inferredExpectedPairs = pairs.Count;

        var expectedPairs = ledger?.ExpectedPairs ?? inferredExpectedPairs;
        // The runner rewrites the ledger after each pair, but a refresh can land
        // between the raw-record write and ledger replacement.  Never display a
        // lower completed count than the durable paired raw records already show.
        var completedPairs = Math.Max(ledger?.CompletedPairs ?? 0, inferredCompletedPairs);
        var fullChainRecords = records.Where(record => string.Equals(record.Treatment, "FullChain", StringComparison.Ordinal)).ToList();
        var evoSuiteRecords = records.Where(record => string.Equals(record.Treatment, "EvoSuite", StringComparison.Ordinal)).ToList();

        static int B2VerifiedCount(IEnumerable<FullChainRecord> treatmentRecords) =>
            treatmentRecords.Count(record => string.Equals(record.TerminalReason, "VerifiedByB2", StringComparison.Ordinal));
        static string AverageCoverage(IReadOnlyCollection<FullChainRecord> treatmentRecords) => treatmentRecords.Count == 0
            ? "n/a"
            : $"{treatmentRecords.Average(record => record.Outcome.BranchCoverage):F1}%";

        var lastUpdatedUtc = ledger?.TimestampUtc ?? directory.LastWriteTimeUtc;
        var isComplete = expectedPairs > 0 && completedPairs >= expectedPairs;
        var readiness = records.Count == 0
            ? "Awaiting results"
            : !isComplete
                ? $"In progress ({completedPairs}/{expectedPairs})"
                : ledger?.HeadlineEligible == true
                    ? "Ready for headline analysis"
                    : "Complete; review ledger";

        return new CampaignRunViewModel
        {
            CampaignId = directory.Name,
            RunDirectory = directory.FullName,
            LastUpdatedUtc = lastUpdatedUtc,
            LastUpdated = lastUpdatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            PairProgress = $"{completedPairs}/{expectedPairs}",
            FullChainB2 = $"{B2VerifiedCount(fullChainRecords)}/{fullChainRecords.Count}",
            EvoSuiteB2 = $"{B2VerifiedCount(evoSuiteRecords)}/{evoSuiteRecords.Count}",
            AverageBranchCoverage = $"FC {AverageCoverage(fullChainRecords)} | ES {AverageCoverage(evoSuiteRecords)}",
            Readiness = readiness,
            CompletedPairs = completedPairs,
            ExpectedPairs = expectedPairs,
            IsComplete = isComplete
        };
    }

    private static LedgerSnapshot? TryReadLedger(string ledgerPath)
    {
        if (!File.Exists(ledgerPath)) return null;

        try
        {
            var json = File.ReadAllText(ledgerPath).TrimStart('\uFEFF');
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var expectedPairs = ReadInt(root, "totalExpectedPairs");
            var completedPairs = ReadInt(root, "completedPairs");
            var headlineEligible = root.TryGetProperty("isHeadlineAnalysisEligible", out var eligibleElement) && eligibleElement.ValueKind == JsonValueKind.True;
            var timestamp = root.TryGetProperty("timestamp", out var timestampElement) &&
                            DateTimeOffset.TryParse(timestampElement.GetString(), out var parsedTimestamp)
                ? parsedTimestamp.UtcDateTime
                : File.GetLastWriteTimeUtc(ledgerPath);
            return new LedgerSnapshot(expectedPairs, completedPairs, headlineEligible, timestamp);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not read availability ledger '{ledgerPath}': {ex.Message}");
            return null;
        }
    }

    private static int ReadInt(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : 0;
    }

    private static void OpenInFileBrowser(string targetPath)
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{targetPath}\"",
                UseShellExecute = true
            });
            return;
        }

        var opener = OperatingSystem.IsMacOS() ? "open" : "xdg-open";
        var startInfo = new ProcessStartInfo
        {
            FileName = opener,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(targetPath);
        Process.Start(startInfo);
    }

    private sealed record LedgerSnapshot(int ExpectedPairs, int CompletedPairs, bool HeadlineEligible, DateTime TimestampUtc);

    public void SaveSettings()
    {
        LocalSettingsStore.SaveSettings(_repoRoot, _settings);
    }

    public void AppendLog(string message)
    {
        var redacted = LocalSettingsStore.Redact(message, _settings.ApiKey);
        var timestamped = $"[{DateTime.Now:HH:mm:ss}] {redacted}";
        _logBuilder.AppendLine(timestamped);
        _logText = _logBuilder.ToString();
        OnPropertyChanged(nameof(LogText));
        OnPropertyChanged(nameof(FilteredLogText));
    }

    public async Task RunPreflightAsync()
    {
        IsRunning = true;
        CurrentPhase = "Preflight";
        StatusMessage = "Running preflight validation...";
        AppendLog("[Preflight] Validating toolchain and environment...");

        try
        {
            using var lockToken = LocalSettingsStore.AcquireCampaignLock(_repoRoot, "Preflight");

            var config = BuildConfiguration();
            var valResult = ConfigurationValidator.ValidateConfiguration(config, _repoRoot);
            if (!valResult.IsValid)
            {
                throw new InvalidOperationException(string.Join("; ", valResult.Errors));
            }

            var toolchain = ToolchainResolver.Resolve(config.Tools, _repoRoot);
            AppendLog($"[Preflight] Toolchain resolved: Java={toolchain.JavaHome}, Maven={toolchain.MavenExecutable}");

            var procRunner = new ProcessRunner();
            var javaCheck = await procRunner.RunAsync(Path.Combine(toolchain.JavaHome, "bin", "java"), "-version", _repoRoot, null, TimeSpan.FromSeconds(10));
            if (!javaCheck.IsSuccess)
            {
                ReportProcessFailure("Preflight Java Check", javaCheck);
                StatusMessage = "Preflight Java Check failed!";
                return;
            }

            var env = new Dictionary<string, string> { ["JAVA_HOME"] = toolchain.JavaHome };
            var mvnCheck = await procRunner.RunAsync(toolchain.MavenExecutable, "-version", _repoRoot, env, TimeSpan.FromSeconds(10));
            if (!mvnCheck.IsSuccess)
            {
                ReportProcessFailure("Preflight Maven Check", mvnCheck);
                StatusMessage = "Preflight Maven Check failed!";
                return;
            }

            AppendLog("[Preflight] All toolchain components verified successfully.");
            StatusMessage = "Preflight completed successfully: PASS";
            CurrentPhase = "Preflight Succeeded";
        }
        catch (Exception ex)
        {
            AppendLog($"[Preflight Error] {ex.Message}");
            ProcessFailureDetail = $"Preflight Error: {ex.Message}\n{ex.StackTrace}";
            StatusMessage = "Preflight Failed!";
        }
        finally
        {
            IsRunning = false;
        }
    }

    public async Task RunOneTargetAsync()
    {
        var campaignId = TryGetNewCampaignId("one");
        if (campaignId == null) return;
        await ExecuteCampaignAsync(campaignId, new[] { SelectedSubjectId }, new[] { SelectedRepetition }, resumeIfPossible: false);
    }

    public async Task RunPilotAsync()
    {
        var pilotManifestPath = Path.Combine(_repoRoot, "experiments", "full-chain", "manifests", "full-chain-rq1-pilot-15.v1.json");
        if (!File.Exists(pilotManifestPath))
        {
            AppendLog($"[Error] Pilot manifest not found at: {pilotManifestPath}.");
            StatusMessage = "Pilot manifest not found";
            return;
        }

        using var manifestDoc = JsonDocument.Parse(await File.ReadAllTextAsync(pilotManifestPath));
        var root = manifestDoc.RootElement;
        var pilotTargets = root.GetProperty("targets").EnumerateArray()
            .Select(e => e.GetProperty("subjectId").GetString()!)
            .ToList();

        var reps = new List<int>();
        if (root.TryGetProperty("repetitions", out var repsElem) && repsElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in repsElem.EnumerateArray())
            {
                reps.Add(r.GetInt32());
            }
        }
        if (reps.Count == 0) reps.Add(42);

        AppendLog($"[Pilot] Starting Pilot campaign ({pilotTargets.Count} targets across {reps.Count} repetitions)...");
        var campaignId = TryGetNewCampaignId("pilot");
        if (campaignId == null) return;
        await ExecuteCampaignAsync(campaignId, pilotTargets, reps, pilotManifestPath, resumeIfPossible: false);
    }

    public async Task RunOfficialAsync()
    {
        var officialManifestPath = Path.Combine(_repoRoot, "experiments", "full-chain", "manifests", "full-chain-rq1-150.v1.json");
        if (!File.Exists(officialManifestPath))
        {
            AppendLog($"[Error] Official manifest not found at: {officialManifestPath}.");
            StatusMessage = "Official manifest not found";
            return;
        }

        using var manifestDoc = JsonDocument.Parse(await File.ReadAllTextAsync(officialManifestPath));
        var root = manifestDoc.RootElement;
        var officialTargets = root.GetProperty("targets").EnumerateArray()
            .Select(e => e.GetProperty("subjectId").GetString()!)
            .ToList();

        var reps = new List<int>();
        if (root.TryGetProperty("repetitions", out var repsElem) && repsElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in repsElem.EnumerateArray())
            {
                reps.Add(r.GetInt32());
            }
        }
        if (reps.Count == 0) reps.AddRange(new[] { 42, 43, 44 });

        AppendLog($"[Official] Starting Official campaign ({officialTargets.Count} targets across {reps.Count} repetitions)...");
        var campaignId = TryGetNewCampaignId("official");
        if (campaignId == null) return;
        await ExecuteCampaignAsync(campaignId, officialTargets, reps, officialManifestPath, resumeIfPossible: false);
    }

    public void CancelCurrentRun()
    {
        if (_cts != null && !_cts.IsCancellationRequested)
        {
            AppendLog("[Action] Cancellation requested by operator.");
            StatusMessage = "Cancelling current run...";
            _cts.Cancel();
        }
    }

    public async Task ResumeCurrentRunAsync()
    {
        if (SelectedRun == null)
        {
            StatusMessage = "Select a run to resume first.";
            AppendLog("[Resume] No historical run is selected.");
            return;
        }

        if (SelectedRun.IsComplete)
        {
            StatusMessage = "Selected run is complete; create a new run instead.";
            AppendLog($"[Resume] '{SelectedRun.CampaignId}' already has all expected pairs.");
            return;
        }

        var records = LoadExistingRecords(Path.Combine(SelectedRun.RunDirectory, "raw-records.jsonl"));
        var subjectIds = records.Select(record => record.SubjectId).Distinct(StringComparer.Ordinal).ToList();
        var repetitions = records.Select(record => record.RepetitionId).Distinct().OrderBy(value => value).ToList();
        if (subjectIds.Count == 0 || repetitions.Count == 0)
        {
            StatusMessage = "Cannot resume: this run has no recoverable target records.";
            AppendLog($"[Resume] '{SelectedRun.CampaignId}' has no raw records from which target scope can be reconstructed.");
            return;
        }

        AppendLog($"[Resume] Resuming '{SelectedRun.CampaignId}' ({subjectIds.Count} targets, {repetitions.Count} repetitions) from its checkpoints.");
        await ExecuteCampaignAsync(SelectedRun.CampaignId, subjectIds, repetitions, resumeIfPossible: true);
    }

    public async Task ExecuteCampaignAsync(
        string campaignId,
        IReadOnlyList<string> subjectIds,
        IReadOnlyList<int> repetitions,
        string? manifestPath = null,
        bool resumeIfPossible = true)
    {
        IsRunning = true;
        StatusMessage = $"Running campaign '{campaignId}'...";
        CurrentPhase = "Initializing";
        ProcessFailureDetail = string.Empty;
        _cts = new CancellationTokenSource();

        try
        {
            // Acquire single-instance cross-process lock
            _activeLock = LocalSettingsStore.AcquireCampaignLock(_repoRoot, $"Campaign_{campaignId}");
            AppendLog($"[Campaign] Single-instance cross-process campaign lock acquired.");

            var config = BuildConfiguration();
            manifestPath ??= Path.Combine(_repoRoot, "experiments", "full-chain", "manifests", "full-chain-rq1-150.v1.json");
            var resolvedManifestPath = Path.IsPathRooted(manifestPath)
                ? manifestPath
                : Path.Combine(_repoRoot, manifestPath);
            using var manifestDoc = JsonDocument.Parse(await File.ReadAllTextAsync(resolvedManifestPath));
            var allTargets = manifestDoc.RootElement.GetProperty("targets").EnumerateArray()
                .Select(e => JsonSerializer.Deserialize<TargetDescriptor>(e.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!)
                .Where(t => subjectIds.Contains(t.SubjectId))
                .ToList();

            if (allTargets.Count == 0)
            {
                throw new InvalidOperationException($"None of the requested subject IDs [{string.Join(", ", subjectIds)}] were found in {resolvedManifestPath}");
            }

            var runsDir = Path.Combine(_repoRoot, "experiments", "full-chain", "runs", campaignId);
            int totalPairs = allTargets.Count * repetitions.Count;
            CurrentTargetNumber = $"0 / {totalPairs}";
            CurrentTreatment = "FullChain + EvoSuite";

            var campaignService = new FullChainCampaignService(_repoRoot);
            campaignService.LogEmitted += AppendLog;
            campaignService.StageChanged += (stage, status) =>
            {
                CurrentPhase = stage;
                CurrentVerdict = status;
            };

            var toolLockPath = Path.Combine(_repoRoot, "experiments", "full-chain", "tool-lock.json");
            var toolLock = JsonSerializer.Deserialize<ToolLock>(
                await File.ReadAllTextAsync(toolLockPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("Tool lock could not be deserialized.");
            var campaignResult = await campaignService.RunAsync(
                new FullChainCampaignRequest(
                    config,
                    toolLock,
                    allTargets,
                    repetitions,
                    campaignId,
                    runsDir,
                    EvoSuiteSearchTimeoutSeconds: 420,
                    ResumeIfPossible: resumeIfPossible,
                    InMemoryApiKey: _settings.ApiKey),
                _cts.Token);

            StageRows.Clear();
            foreach (var record in campaignResult.Records.OrderBy(record => record.SubjectId).ThenBy(record => record.RepetitionId).ThenBy(record => record.Treatment))
            {
                AddRecordToUi(record, runsDir);
                AppendLog($"[Result] {record.Treatment} -> Status={record.TerminalStatus}, Reason={record.TerminalReason}, BC={record.Diagnostic.BranchCoverage:F1}%");
            }
            CurrentTargetNumber = $"{totalPairs} / {totalPairs}";

            StatusMessage = "Campaign run completed successfully!";
            CurrentPhase = "Completed";
            AppendLog($"[Campaign Complete] Finished all pairs. Check availability ledger and paired CSV.");
            RefreshRuns(campaignId);
            if (!resumeIfPossible)
            {
                CreateNewCampaignId();
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Run was cancelled by operator.";
            CurrentPhase = "Cancelled";
            AppendLog("[Campaign] Run cancelled. Checkpoints preserved for resume.");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Campaign error: {ex.Message}";
            ProcessFailureDetail = $"Campaign Failure: {ex.Message}\n{ex.StackTrace}";
            AppendLog($"[Campaign Error] {ex.Message}");
        }
        finally
        {
            _activeLock?.Dispose();
            _activeLock = null;
            IsRunning = false;
            if (!string.IsNullOrWhiteSpace(campaignId))
            {
                RefreshRuns(campaignId);
            }
        }
    }

    public void ReportProcessFailure(string stepName, ProcessResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[PROCESS FAILURE in {stepName}]");
        sb.AppendLine($"Command: {result.Command} {result.Arguments}");
        sb.AppendLine($"Working Directory: {result.WorkingDirectory}");
        sb.AppendLine($"Exit Code: {result.ExitCode}");
        sb.AppendLine($"Duration: {result.Duration.TotalSeconds:F2}s");
        sb.AppendLine("--- Standard Error ---");
        sb.AppendLine(string.IsNullOrWhiteSpace(result.Stderr) ? "(empty)" : result.Stderr.Trim());
        sb.AppendLine("--- Standard Output Tail ---");
        var stdoutLines = result.Stdout.Split('\n');
        var tail = stdoutLines.Skip(Math.Max(0, stdoutLines.Length - 30));
        sb.AppendLine(string.Join('\n', tail));

        ProcessFailureDetail = sb.ToString();
        AppendLog(sb.ToString());
    }

    public void OpenSelectedEvidence()
    {
        if (SelectedStageRow == null || string.IsNullOrEmpty(SelectedStageRow.EvidencePath)) return;

        try
        {
            var targetPath = SelectedStageRow.EvidencePath;
            if (!Directory.Exists(targetPath) && !File.Exists(targetPath))
            {
                // Prefer the selected campaign.  Without this scope, two campaigns
                // containing the same target could open evidence from the wrong run.
                var runsRoot = SelectedRun?.RunDirectory
                               ?? Path.Combine(_repoRoot, "experiments", "full-chain", "runs");
                if (Directory.Exists(runsRoot))
                {
                    var match = Directory.GetDirectories(runsRoot, SelectedStageRow.Treatment, SearchOption.AllDirectories)
                        .FirstOrDefault(d => d.Contains(SelectedStageRow.TargetId, StringComparison.OrdinalIgnoreCase) &&
                                             d.Contains($"rep_{SelectedStageRow.Repetition}", StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                    {
                        targetPath = match;
                    }
                }
            }

            if (Directory.Exists(targetPath) || File.Exists(targetPath))
            {
                OpenInFileBrowser(targetPath);
                AppendLog($"[Open Evidence] Opened folder: {targetPath}");
            }
            else
            {
                AppendLog($"[Open Evidence] Path does not exist on disk: {SelectedStageRow.EvidencePath}");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[Open Evidence Error] {ex.Message}");
        }
    }

    public void CopyFullLog()
    {
        try
        {
#if NET8_0_WINDOWS || WINDOWS
            System.Windows.Clipboard.SetText(LogText);
#endif
            StatusMessage = "Log copied to clipboard.";
        }
        catch (Exception ex)
        {
            AppendLog($"[Copy Error] Could not copy to clipboard: {ex.Message}");
        }
    }

    private FullChainConfiguration BuildConfiguration()
    {
        return LocalSettingsStore.BuildConfiguration(_repoRoot, _settings);
    }

    private static string FindRepoRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "docs", "FULL_CHAIN_REBUILD_PLAN.md")))
            {
                return dir;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }
        return Directory.GetCurrentDirectory();
    }

    private static List<FullChainRecord> LoadExistingRecords(string rawJsonlPath)
    {
        var records = new List<FullChainRecord>();
        if (!File.Exists(rawJsonlPath)) return records;

        try
        {
            var text = File.ReadAllText(rawJsonlPath);
            if (string.IsNullOrWhiteSpace(text)) return records;

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            // Current campaigns write one compact JSON object per line.  Parse that
            // format first so a complete campaign is not accidentally represented
            // by its first result only in the operator history.
            var jsonLines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var parsedAllLines = jsonLines.Length > 0;
            foreach (var line in jsonLines)
            {
                try
                {
                    var record = JsonSerializer.Deserialize<FullChainRecord>(line.Trim().TrimStart('\uFEFF'), options);
                    if (record == null)
                    {
                        parsedAllLines = false;
                        break;
                    }
                    records.Add(record);
                }
                catch (JsonException)
                {
                    parsedAllLines = false;
                    break;
                }
            }
            if (parsedAllLines)
            {
                return records;
            }

            // Legacy artifacts may contain pretty-printed concatenated objects.
            // Reset and use the token reader only for that compatibility path.
            records.Clear();
            var bytes = Encoding.UTF8.GetBytes(text.TrimStart('\uFEFF'));
            var reader = new Utf8JsonReader(bytes);

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    var doc = JsonDocument.ParseValue(ref reader);
                    var rec = JsonSerializer.Deserialize<FullChainRecord>(doc.RootElement.GetRawText(), options);
                    if (rec != null)
                    {
                        records.Add(rec);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error loading existing records: {ex.Message}");
        }

        return records;
    }

    private void AddRecordToUi(FullChainRecord rec, string runsDir)
    {
        var evidencePath = Path.Combine(runsDir, rec.SubjectId, $"rep_{rec.RepetitionId}", rec.Treatment);
        if (!Directory.Exists(evidencePath) && !string.IsNullOrEmpty(rec.WorkspaceDirectory))
        {
            var dir = new DirectoryInfo(rec.WorkspaceDirectory);
            while (dir != null && !dir.Name.Equals(rec.Treatment, StringComparison.OrdinalIgnoreCase))
            {
                dir = dir.Parent;
            }
            if (dir != null && dir.Exists)
            {
                evidencePath = dir.FullName;
            }
            else if (Directory.Exists(rec.WorkspaceDirectory))
            {
                evidencePath = rec.WorkspaceDirectory;
            }
        }

        var existing = StageRows.FirstOrDefault(r => r.TargetId == rec.SubjectId && r.Repetition == rec.RepetitionId && r.Treatment == rec.Treatment);
        if (existing != null)
        {
            StageRows.Remove(existing);
        }

        var row = new StageRowViewModel
        {
            Phase = "Delivered / Diagnostic",
            Treatment = rec.Treatment,
            TargetId = rec.SubjectId,
            Repetition = rec.RepetitionId,
            Status = rec.TerminalStatus,
            Reason = rec.TerminalReason ?? "None",
            EvidencePath = evidencePath,
            Duration = $"{rec.TotalWallClockSeconds:F1}s",
            Details = $"Terminal Status: {rec.TerminalStatus}\n" +
                      $"Reason: {rec.TerminalReason}\n" +
                      $"Delivered VSR: {rec.Outcome.VerifiedSuiteRate}\n" +
                      $"Delivered CSR: {rec.Outcome.CompilationSatisfactionRate}\n" +
                      $"Delivered ESR: {rec.Outcome.ExecutionSatisfactionRate}\n" +
                      $"Delivered BC: {rec.Outcome.BranchCoverage:F2}%\n" +
                      $"Candidate BC: {rec.Diagnostic.BranchCoverage:F2}%\n" +
                      $"Repair Rounds: {rec.TotalRepairRounds}\n" +
                      $"Tokens: {rec.TotalTokensUsed}\n" +
                      $"Workspace: {rec.WorkspaceDirectory}"
        };
        StageRows.Add(row);
        SelectedStageRow = row;
    }
}
