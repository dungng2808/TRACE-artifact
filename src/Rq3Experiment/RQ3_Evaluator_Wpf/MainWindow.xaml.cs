using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using RQ3Evaluator.Wpf.Models;
using RQ3Evaluator.Wpf.Services;
using Forms = System.Windows.Forms;
using WpfMessageBox = System.Windows.MessageBox;
using WpfClipboard = System.Windows.Clipboard;
using WpfColor = System.Windows.Media.Color;
using Win32OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace RQ3Evaluator.Wpf;

public partial class MainWindow : Window
{
    private readonly Phase6RunService _runService = new();
    private readonly ObservableCollection<PredictionDisplayRow> _joinedPredictions = new();
    private readonly ObservableCollection<StabilityRecordRow> _stabilityRecords = new();
    private readonly ObservableCollection<ArtifactSummary> _artifacts = new();

    private CancellationTokenSource? _cancellation;
    private DispatcherTimer? _elapsedTimer;
    private DateTime _runStartTime;
    private string? _currentRunFolder;
    private bool _isPreflightValid;
    private int _logLineCount;

    public MainWindow()
    {
        InitializeComponent();
        JoinedResultsGrid.ItemsSource = _joinedPredictions;
        StabilityGrid.ItemsSource = _stabilityRecords;
        ArtifactsGrid.ItemsSource = _artifacts;

        ApplyDefaults();
        TryLoadDefaultResults();
        _ = LoadModelsAsync();
    }

    private void ApplyDefaults()
    {
        var settings = Phase6PathResolver.GetDefaultSettings();
        WorkspacePathBox.Text = settings.WorkspaceRoot;
        DatasetPathBox.Text = settings.DatasetRoot;
        ManifestPathBox.Text = settings.ManifestPath;
        EvidenceBundlePathBox.Text = settings.EvidenceBundleRoot;
        ProjectionPathBox.Text = settings.ProjectionRoot;
        EvidenceFreezePathBox.Text = settings.EvidenceFreezePath;
        OutputPathBox.Text = settings.OutputRoot;
        SharedEndpointBox.Text = settings.Endpoint;
        B1EndpointBox.Text = settings.B1Endpoint;

        var initialModels = OpenRouterModelService.FallbackModels.Select(m => m.Id).ToList();
        B1ModelBox.ItemsSource = initialModels;
        B1ModelBox.Text = settings.B1Model;

        B2EndpointBox.Text = settings.B2Endpoint;
        B2ModelBox.ItemsSource = initialModels;
        B2ModelBox.Text = settings.B2Model;

        StabilityEndpointBox.Text = settings.StabilityEndpoint;
        StabilityModelBox.ItemsSource = initialModels;
        StabilityModelBox.Text = settings.StabilityModel;

        ApiKeyFileBox.Text = settings.ApiKeyFilePath;

        if (MaxTokensBox != null) MaxTokensBox.Text = settings.MaxTokens.ToString();
        if (MaxConcurrencyBox != null) MaxConcurrencyBox.Text = settings.MaxConcurrency.ToString();
        if (RateLimitRpmBox != null) RateLimitRpmBox.Text = settings.RateLimitRpm.ToString();
        if (MaxRetriesBox != null) MaxRetriesBox.Text = settings.MaxRetries.ToString();
        if (RequestTimeoutSecBox != null) RequestTimeoutSecBox.Text = settings.RequestTimeoutSec.ToString();

        ActiveRunDirLabel.Text = $"Output root: {settings.OutputRoot}";
    }

    private async Task LoadModelsAsync()
    {
        try
        {
            if (RefreshModelsBtn != null)
            {
                RefreshModelsBtn.IsEnabled = false;
                RefreshModelsBtn.Content = "Loading...";
            }
            if (ModelStatusLabel != null)
            {
                ModelStatusLabel.Text = " (Fetching models...)";
            }

            var endpoint = SharedEndpointBox?.Text?.Trim();
            var keyFile = ApiKeyFileBox?.Text?.Trim();
            var apiKeys = !string.IsNullOrWhiteSpace(keyFile) ? ApiKeyPool.LoadFile(keyFile) : Array.Empty<string>();
            var models = await OpenRouterModelService.FetchModelsAsync(endpoint, apiKeys);

            if (models.Count > 0)
            {
                var modelIds = models.Select(m => m.Id).ToList();

                var b1Current = B1ModelBox.Text;
                B1ModelBox.ItemsSource = modelIds;
                if (!string.IsNullOrWhiteSpace(b1Current)) B1ModelBox.Text = b1Current;

                var b2Current = B2ModelBox.Text;
                B2ModelBox.ItemsSource = modelIds;
                if (!string.IsNullOrWhiteSpace(b2Current)) B2ModelBox.Text = b2Current;

                var stabCurrent = StabilityModelBox.Text;
                StabilityModelBox.ItemsSource = modelIds;
                if (!string.IsNullOrWhiteSpace(stabCurrent)) StabilityModelBox.Text = stabCurrent;

                if (ModelStatusLabel != null)
                {
                    ModelStatusLabel.Text = $" ({models.Count} models available)";
                }
            }
        }
        catch
        {
            if (ModelStatusLabel != null)
            {
                ModelStatusLabel.Text = " (Offline fallback)";
            }
        }
        finally
        {
            if (RefreshModelsBtn != null)
            {
                RefreshModelsBtn.IsEnabled = true;
                RefreshModelsBtn.Content = "↻ Refresh Models";
            }
        }
    }

    private async void RefreshModels_Click(object sender, RoutedEventArgs e)
    {
        await LoadModelsAsync();
    }

    private Phase6Settings GetCurrentSettings()
    {
        var isSameEndpoint = SameEndpointCheckBox.IsChecked == true;
        var sharedEndpoint = SharedEndpointBox.Text.Trim();
        var b1Endpoint = isSameEndpoint ? sharedEndpoint : B1EndpointBox.Text.Trim();
        var b2Endpoint = isSameEndpoint ? sharedEndpoint : B2EndpointBox.Text.Trim();
        var stabilityEndpoint = isSameEndpoint ? sharedEndpoint : StabilityEndpointBox.Text.Trim();

        var maxTokens = int.TryParse(MaxTokensBox?.Text?.Trim(), out var mt) ? mt : 2000;
        var maxConcurrency = int.TryParse(MaxConcurrencyBox?.Text?.Trim(), out var mc) ? mc : 5;
        var rateLimitRpm = int.TryParse(RateLimitRpmBox?.Text?.Trim(), out var rpm) ? rpm : 0;
        var maxRetries = int.TryParse(MaxRetriesBox?.Text?.Trim(), out var mr) ? mr : 3;
        var timeoutSec = int.TryParse(RequestTimeoutSecBox?.Text?.Trim(), out var to) ? to : 120;

        return new Phase6Settings(
            WorkspaceRoot: WorkspacePathBox.Text.Trim(),
            DatasetRoot: DatasetPathBox.Text.Trim(),
            ManifestPath: ManifestPathBox.Text.Trim(),
            EvidenceBundleRoot: EvidenceBundlePathBox.Text.Trim(),
            ProjectionRoot: ProjectionPathBox.Text.Trim(),
            EvidenceFreezePath: EvidenceFreezePathBox.Text.Trim(),
            OutputRoot: OutputPathBox.Text.Trim(),
            Endpoint: sharedEndpoint,
            Model: B1ModelBox.Text.Trim(),
            B1Endpoint: b1Endpoint,
            B1Model: B1ModelBox.Text.Trim(),
            B2Endpoint: b2Endpoint,
            B2Model: B2ModelBox.Text.Trim(),
            StabilityEndpoint: stabilityEndpoint,
            StabilityModel: StabilityModelBox.Text.Trim(),
            ApiKeyFilePath: ApiKeyFileBox.Text.Trim(),
            RunB0: B0Box.IsChecked == true,
            RunB1: B1Box.IsChecked == true,
            RunB2: B2Box.IsChecked == true,
            Score: false,
            MaxTokens: maxTokens,
            MaxConcurrency: maxConcurrency,
            RateLimitRpm: rateLimitRpm,
            MaxRetries: maxRetries,
            RequestTimeoutSec: timeoutSec
        );
    }

    private void SameEndpointCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (B1EndpointBox == null || B2EndpointBox == null || StabilityEndpointBox == null || SharedEndpointBox == null) return;
        var isSame = SameEndpointCheckBox.IsChecked == true;
        B1EndpointBox.IsEnabled = !isSame;
        B2EndpointBox.IsEnabled = !isSame;
        StabilityEndpointBox.IsEnabled = !isSame;

        if (isSame)
        {
            var shared = SharedEndpointBox.Text;
            B1EndpointBox.Text = shared;
            B2EndpointBox.Text = shared;
            StabilityEndpointBox.Text = shared;
        }
    }

    private void SharedEndpointBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (SameEndpointCheckBox?.IsChecked == true && B1EndpointBox != null && B2EndpointBox != null && StabilityEndpointBox != null)
        {
            var shared = SharedEndpointBox.Text;
            B1EndpointBox.Text = shared;
            B2EndpointBox.Text = shared;
            StabilityEndpointBox.Text = shared;
        }
    }

    private void TryLoadDefaultResults()
    {
        var ws = WorkspacePathBox.Text.Trim();
        var defaultResultsDir = Path.Combine(ws, "ThucNgiem", "RQ3_Phase3", "phase6_results_v1");
        var defaultStabilityDir = Path.Combine(ws, "ThucNgiem", "RQ3_Phase3", "phase6_stability_v1");

        var metricsJson = Path.Combine(defaultResultsDir, "metrics_v2.json");
        var stabilityJson = Path.Combine(defaultStabilityDir, "phase6_stability_v1.json");
        var csvPath = Path.Combine(defaultResultsDir, "predictions_joined_private_gold_v2.csv");

        if (File.Exists(metricsJson))
        {
            LoadResultsIntoDashboard(metricsJson, stabilityJson, csvPath, Path.Combine(ws, "ThucNgiem", "RQ3_Phase3"));
        }
    }

    #region Tab 1: Browsing & Validation

    private void BrowseWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (PickFolder(WorkspacePathBox))
        {
            var ws = WorkspacePathBox.Text.Trim();
            var def = Phase6PathResolver.GetDefaultSettings(ws);
            DatasetPathBox.Text = def.DatasetRoot;
            ManifestPathBox.Text = def.ManifestPath;
            EvidenceBundlePathBox.Text = def.EvidenceBundleRoot;
            ProjectionPathBox.Text = def.ProjectionRoot;
            EvidenceFreezePathBox.Text = def.EvidenceFreezePath;
            OutputPathBox.Text = def.OutputRoot;
            if (File.Exists(def.ApiKeyFilePath)) ApiKeyFileBox.Text = def.ApiKeyFilePath;
            InvalidatePreflight();
        }
    }

    private void BrowseDataset_Click(object sender, RoutedEventArgs e)
    {
        if (PickFolder(DatasetPathBox)) InvalidatePreflight();
    }

    private void BrowseManifest_Click(object sender, RoutedEventArgs e)
    {
        if (PickFile(ManifestPathBox, "JSON files (*.json)|*.json|All files (*.*)|*.*")) InvalidatePreflight();
    }

    private void BrowseEvidenceBundle_Click(object sender, RoutedEventArgs e)
    {
        if (PickFolder(EvidenceBundlePathBox)) InvalidatePreflight();
    }

    private void BrowseProjection_Click(object sender, RoutedEventArgs e)
    {
        if (PickFolder(ProjectionPathBox)) InvalidatePreflight();
    }

    private void BrowseEvidenceFreeze_Click(object sender, RoutedEventArgs e)
    {
        if (PickFile(EvidenceFreezePathBox, "JSON files (*.json)|*.json|All files (*.*)|*.*")) InvalidatePreflight();
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        PickFolder(OutputPathBox);
    }

    private void BrowseApiKey_Click(object sender, RoutedEventArgs e)
    {
        if (PickFile(ApiKeyFileBox, "Text files (*.txt)|*.txt|All files (*.*)|*.*"))
        {
            InvalidatePreflight();
            _ = LoadModelsAsync();
        }
    }

    private static bool PickFolder(System.Windows.Controls.TextBox target)
    {
        using var dialog = new Forms.FolderBrowserDialog { SelectedPath = target.Text };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return false;
        target.Text = dialog.SelectedPath;
        return true;
    }

    private static bool PickFile(System.Windows.Controls.TextBox target, string filter)
    {
        var dialog = new Win32OpenFileDialog { Filter = filter, FileName = target.Text, CheckFileExists = true };
        if (dialog.ShowDialog() != true) return false;
        target.Text = dialog.FileName;
        return true;
    }

    private void InvalidatePreflight()
    {
        _isPreflightValid = false;
        HeaderStatusText.Text = "NOT VALIDATED";
        HeaderStatusText.Foreground = new SolidColorBrush(WpfColor.FromRgb(0xCC, 0xFB, 0xF1));
        CohortBadge.Background = new SolidColorBrush(WpfColor.FromRgb(0x11, 0x5E, 0x59));
        RunPredictionsBtn.IsEnabled = false;
        RunAllBtn.IsEnabled = false;
        FreezePredictionsBtn.IsEnabled = false;
        ScoreGoldBtn.IsEnabled = false;
        RunStabilityBtn.IsEnabled = false;
    }

    private void Validate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ValidationLogBox.Clear();
            var settings = GetCurrentSettings();
            var result = FrozenEvidenceValidator.Validate(settings);

            foreach (var l in result.ValidationLogs)
            {
                ValidationLogBox.AppendText(l + Environment.NewLine);
            }
            ValidationLogBox.ScrollToEnd();

            if (result.IsValid)
            {
                _isPreflightValid = true;
                CheckManifestStatus.Text = $"• Manifest Status: {result.ManifestStatus} (PASS)";
                CheckManifestStatus.Foreground = new SolidColorBrush(Colors.ForestGreen);

                CheckCaseCount.Text = $"• Cases Validated: {result.CasesValidated} / {result.TotalCasesExpected} (PASS)";
                CheckCaseCount.Foreground = new SolidColorBrush(Colors.ForestGreen);

                CheckPublicHashes.Text = "• Public Case Hashes: 0 ERRORS (PASS)";
                CheckPublicHashes.Foreground = new SolidColorBrush(Colors.ForestGreen);

                CheckBundleHashes.Text = "• Evidence Bundle Hashes: 0 ERRORS (PASS)";
                CheckBundleHashes.Foreground = new SolidColorBrush(Colors.ForestGreen);

                CheckProjectionHashes.Text = "• Projection Hashes: 0 ERRORS (PASS)";
                CheckProjectionHashes.Foreground = new SolidColorBrush(Colors.ForestGreen);

                CheckFreezeJson.Text = result.EvidenceFreezeValid ? "• Evidence Freeze: PASS" : "• Evidence Freeze: WARN (File not found)";
                CheckFreezeJson.Foreground = result.EvidenceFreezeValid ? new SolidColorBrush(Colors.ForestGreen) : new SolidColorBrush(Colors.DarkOrange);

                CheckApiKey.Text = result.ApiKeyAvailable ? $"• API Key Pool: {result.ApiKeyCount} UNIQUE KEY(S) (PASS)" : "• API Key File: NOT FOUND (B0 only)";
                CheckApiKey.Foreground = result.ApiKeyAvailable ? new SolidColorBrush(Colors.ForestGreen) : new SolidColorBrush(Colors.DarkOrange);

                CheckDocker.Text = result.DockerAvailable ? "• Docker Engine: AVAILABLE (PASS)" : "• Docker Engine: NOT RUNNING (OK for frozen run)";
                CheckDocker.Foreground = new SolidColorBrush(Colors.DarkSlateGray);

                HeaderStatusText.Text = $"FROZEN MAIN READY ({result.CasesValidated} CASES)";
                HeaderStatusText.Foreground = new SolidColorBrush(Colors.White);
                CohortBadge.Background = new SolidColorBrush(WpfColor.FromRgb(0x0F, 0x76, 0x6E));

                RunPredictionsBtn.IsEnabled = true;
                RunAllBtn.IsEnabled = true;
                BottomStatusText.Text = $"Preflight validation passed for {result.CasesValidated} cases. Ready to run pipeline.";
            }
            else
            {
                InvalidatePreflight();
                CheckManifestStatus.Text = $"• Manifest Status: FAIL ({result.ErrorMessage})";
                CheckManifestStatus.Foreground = new SolidColorBrush(Colors.Crimson);
                BottomStatusText.Text = $"Preflight validation failed: {result.ErrorMessage}";
                WpfMessageBox.Show(result.ErrorMessage ?? "Validation failed", "Validation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            InvalidatePreflight();
            ValidationLogBox.AppendText($"[EXCEPTION] {ex.Message}{Environment.NewLine}");
            BottomStatusText.Text = $"Validation error: {ex.Message}";
            WpfMessageBox.Show(ex.Message, "Validation Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenCohortManifest_Click(object sender, RoutedEventArgs e)
    {
        var p = ManifestPathBox.Text.Trim();
        if (File.Exists(p)) Process.Start(new ProcessStartInfo { FileName = p, UseShellExecute = true });
    }

    private void OpenEvidenceFreeze_Click(object sender, RoutedEventArgs e)
    {
        var p = EvidenceFreezePathBox.Text.Trim();
        if (File.Exists(p)) Process.Start(new ProcessStartInfo { FileName = p, UseShellExecute = true });
    }

    #endregion

    #region Tab 2 & 3: Run Pipeline & Live Log

    private async void RunPredictions_Click(object sender, RoutedEventArgs e)
    {
        await ExecutePredictionStageAsync(isResume: false);
    }

    private async Task<bool> ExecutePredictionStageAsync(bool isResume)
    {
        try
        {
            var settings = GetCurrentSettings();
            if (!_isPreflightValid)
            {
                Validate_Click(this, new RoutedEventArgs());
                if (!_isPreflightValid) return false;
            }

            if (_currentRunFolder == null || !isResume)
            {
                _currentRunFolder = _runService.CreateRunDirectory(
                    settings.OutputRoot,
                    settings.B1Model,
                    settings.B2Model);
            }

            ActiveRunDirLabel.Text = $"Run Folder: {_currentRunFolder}";
            MainTabs.SelectedIndex = 2; // switch to Live Log

            SetRunningState(true, "RUNNING PREDICTIONS (B0/B1/B2)");
            Log($"=== STARTING PREDICTIONS RUN ===");
            Log($"Target directory: {_currentRunFolder}");

            var exitCode = await _runService.RunPredictionsAsync(
                settings,
                _currentRunFolder,
                isResume,
                line => Dispatcher.Invoke(() => HandleProcessOutput(line)),
                _cancellation!.Token
            );

            if (exitCode == 0)
            {
                Log($"[SUCCESS] Predictions completed with exit code 0.");
                FreezePredictionsBtn.IsEnabled = true;
                BottomStatusText.Text = "Predictions completed. Ready to freeze predictions.";
                return true;
            }
            else
            {
                Log($"[ERROR] Batch runner exited with code {exitCode}.");
                BottomStatusText.Text = $"Predictions failed with exit code {exitCode}.";
                ResumeBtn.IsEnabled = true;
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            Log("[CANCELLED] Prediction execution cancelled safely by user. Checkpoint preserved.");
            BottomStatusText.Text = "Execution cancelled; checkpoint preserved.";
            ResumeBtn.IsEnabled = true;
            return false;
        }
        catch (Exception ex)
        {
            Log($"[FATAL ERROR] {ex.Message}");
            BottomStatusText.Text = $"Error: {ex.Message}";
            WpfMessageBox.Show(ex.Message, "Prediction Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally
        {
            SetRunningState(false, "PREDICTIONS FINISHED");
        }
    }

    private void FreezePredictions_Click(object sender, RoutedEventArgs e)
    {
        if (_currentRunFolder == null) return;
        var settings = GetCurrentSettings();
        var ok = _runService.FreezePredictions(settings, _currentRunFolder, line => Log(line));
        if (ok)
        {
            FreezePredictionsBtn.IsEnabled = false;
            ScoreGoldBtn.IsEnabled = true;
            CurrentStageText.Text = "PREDICTIONS FROZEN";
            BottomStatusText.Text = "Predictions frozen with SHA-256 hash. Ready to score with private gold.";
            WpfMessageBox.Show("Predictions have been successfully frozen and hashed.\nNo private gold has been touched.", "Predictions Frozen", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void ScoreGold_Click(object sender, RoutedEventArgs e)
    {
        if (_currentRunFolder == null) return;

        var confirm = WpfMessageBox.Show(
            "Predictions have been frozen and hashed.\nDo you confirm opening private gold to score predictions?",
            "Confirm Private Gold Scoring",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question
        );

        if (confirm != MessageBoxResult.Yes) return;

        await ExecuteScoringStageAsync();
    }

    private async Task<bool> ExecuteScoringStageAsync()
    {
        if (_currentRunFolder == null) return false;
        try
        {
            var settings = GetCurrentSettings();
            SetRunningState(true, "SCORING WITH PRIVATE GOLD");
            MainTabs.SelectedIndex = 2;

            Log("=== STARTING PRIVATE GOLD SCORING ===");

            var exitCode = await _runService.RunScoringAsync(
                settings,
                _currentRunFolder,
                line => Dispatcher.Invoke(() => Log(line)),
                _cancellation!.Token
            );

            if (exitCode == 0)
            {
                Log("[SUCCESS] Scoring script completed.");
                RunStabilityBtn.IsEnabled = true;
                ScoreGoldBtn.IsEnabled = false;
                BottomStatusText.Text = "Scoring completed. Results generated.";

                // Load newly generated results into Tab 4
                var metricsJson = Path.Combine(_currentRunFolder, "results", "metrics_v2.json");
                var csvPath = Path.Combine(_currentRunFolder, "results", "predictions_joined_private_gold_v2.csv");
                LoadResultsIntoDashboard(metricsJson, null, csvPath, _currentRunFolder);
                return true;
            }
            else
            {
                Log($"[ERROR] Scoring failed with exit code {exitCode}.");
                BottomStatusText.Text = $"Scoring script failed (code {exitCode}). Prediction freeze preserved.";
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            Log("[CANCELLED] Scoring cancelled by user.");
            return false;
        }
        catch (Exception ex)
        {
            Log($"[SCORING ERROR] {ex.Message}");
            WpfMessageBox.Show(ex.Message, "Scoring Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally
        {
            SetRunningState(false, "SCORING COMPLETE");
        }
    }

    private async void RunStability_Click(object sender, RoutedEventArgs e)
    {
        if (_currentRunFolder == null) return;
        await ExecuteStabilityStageAsync();
    }

    private async Task<bool> ExecuteStabilityStageAsync()
    {
        if (_currentRunFolder == null) return false;
        try
        {
            var settings = GetCurrentSettings();
            SetRunningState(true, "RUNNING STABILITY EVALUATION");
            MainTabs.SelectedIndex = 2;

            Log("=== STARTING STABILITY REPETITIONS (3-REP / 15 CASES) ===");

            var exitCode = await _runService.RunStabilityAsync(
                settings,
                _currentRunFolder,
                line => Dispatcher.Invoke(() => Log(line)),
                _cancellation!.Token
            );

            if (exitCode == 0)
            {
                Log("[SUCCESS] Stability evaluation completed.");
                RunStabilityBtn.IsEnabled = false;
                BottomStatusText.Text = "Stability evaluation completed.";

                var metricsJson = Path.Combine(_currentRunFolder, "results", "metrics_v2.json");
                var stabilityJson = Path.Combine(_currentRunFolder, "stability", "phase6_stability_v1.json");
                var csvPath = Path.Combine(_currentRunFolder, "results", "predictions_joined_private_gold_v2.csv");
                LoadResultsIntoDashboard(metricsJson, stabilityJson, csvPath, _currentRunFolder);
                return true;
            }
            else
            {
                Log($"[ERROR] Stability evaluation failed with exit code {exitCode}.");
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            Log("[CANCELLED] Stability evaluation cancelled by user.");
            return false;
        }
        catch (Exception ex)
        {
            Log($"[STABILITY ERROR] {ex.Message}");
            WpfMessageBox.Show(ex.Message, "Stability Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally
        {
            SetRunningState(false, "STABILITY COMPLETE");
        }
    }

    private async void RunAllRemaining_Click(object sender, RoutedEventArgs e)
    {
        // 1. Predictions
        var predOk = await ExecutePredictionStageAsync(isResume: false);
        if (!predOk) return;

        // 2. Freeze
        var settings = GetCurrentSettings();
        var freezeOk = _runService.FreezePredictions(settings, _currentRunFolder!, line => Log(line));
        if (!freezeOk) return;

        // 3. Confirm & Score
        var confirm = WpfMessageBox.Show(
            "Predictions have been frozen and hashed.\nProceed to score with private gold?",
            "Confirm Private Gold Scoring",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question
        );
        if (confirm != MessageBoxResult.Yes) return;

        var scoreOk = await ExecuteScoringStageAsync();
        if (!scoreOk) return;

        // 4. Stability
        await ExecuteStabilityStageAsync();

        WpfMessageBox.Show("All pipeline stages completed successfully!", "Pipeline Completed", MessageBoxButton.OK, MessageBoxImage.Information);
        MainTabs.SelectedIndex = 3; // Switch to Results tab
    }

    private async void Resume_Click(object sender, RoutedEventArgs e)
    {
        await ExecutePredictionStageAsync(isResume: true);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cancellation?.Cancel();
        _runService.ProcessService.CancelCurrent();
    }

    private void SetRunningState(bool isRunning, string stageText)
    {
        CurrentStageText.Text = stageText;
        CancelBtn.IsEnabled = isRunning;
        RunPredictionsBtn.IsEnabled = !isRunning && _isPreflightValid;
        RunAllBtn.IsEnabled = !isRunning && _isPreflightValid;
        ValidateButton.IsEnabled = !isRunning;

        if (isRunning)
        {
            _cancellation = new CancellationTokenSource();
            _runStartTime = DateTime.Now;
            _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _elapsedTimer.Tick += (_, _) =>
            {
                var elapsed = DateTime.Now - _runStartTime;
                ElapsedTimeText.Text = $"Elapsed: {elapsed:hh\\:mm\\:ss}";
            };
            _elapsedTimer.Start();
        }
        else
        {
            _elapsedTimer?.Stop();
            _elapsedTimer = null;
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    private void HandleProcessOutput(string line)
    {
        Log(line);

        // Progress parsing
        if (line.StartsWith("[PROGRESS] Completed case (", StringComparison.OrdinalIgnoreCase))
        {
            // e.g. [PROGRESS] Completed case (1/73): RQ3-M-MATH-04
            var start = line.IndexOf('(');
            var end = line.IndexOf(')');
            if (start > 0 && end > start)
            {
                var span = line.Substring(start + 1, end - start - 1);
                var parts = span.Split('/');
                if (parts.Length == 2 && int.TryParse(parts[0], out var cur) && int.TryParse(parts[1], out var tot))
                {
                    CompletedCasesText.Text = $"{cur} / {tot}";
                    CompletedPredsText.Text = $"{cur * 3} / {tot * 3}";
                    ExecutionProgressBar.Maximum = tot;
                    ExecutionProgressBar.Value = cur;
                    ProgressStatusText.Text = $"Processing case {cur}/{tot}...";
                }
            }
        }
    }

    private void Log(string message)
    {
        _logLineCount++;
        if (_logLineCount > 10000)
        {
            ConsoleLogBox.Clear();
            _logLineCount = 1;
        }
        ConsoleLogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        ConsoleLogBox.ScrollToEnd();
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        WpfClipboard.SetText(ConsoleLogBox.Text);
        BottomStatusText.Text = "Console log copied to clipboard.";
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        ConsoleLogBox.Clear();
        _logLineCount = 0;
    }

    private void OpenActiveRunFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = _currentRunFolder ?? OutputPathBox.Text.Trim();
        if (Directory.Exists(path))
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
    }

    #endregion

    #region Tab 4: Results & Artifacts

    private void LoadResultsIntoDashboard(string metricsJsonPath, string? stabilityJsonPath, string? csvPath, string rootFolder)
    {
        var data = Phase6MetricsService.LoadMetrics(metricsJsonPath, stabilityJsonPath);
        if (data == null) return;

        // 1. Verifier cards
        foreach (var m in data.VerifierMetrics)
        {
            if (m.Verifier.Contains("B0", StringComparison.OrdinalIgnoreCase))
            {
                B0AccuracyText.Text = m.DecisionAccuracyDisplay;
                B0CiText.Text = $"Wilson 95%: {m.Wilson95Ci} • Bootstrap: {m.Bootstrap95Ci}";
                B0RoutingText.Text = $"Routing Acc: {m.RoutingAccuracyDisplay} • Macro F1: {m.MacroAttributionF1Display}";
                B0AbstainText.Text = $"Abstention: {m.AbstentionRateDisplay} • Selective Acc: {m.SelectiveAccuracyDisplay}";
                B0CostText.Text = $"Errors: {m.ApiOrOutputErrors} • Calls: {m.ApiCalls} • Cost: {m.CostUsdDisplay}";
            }
            else if (m.Verifier.Contains("B1", StringComparison.OrdinalIgnoreCase))
            {
                B1AccuracyText.Text = m.DecisionAccuracyDisplay;
                B1CiText.Text = $"Wilson 95%: {m.Wilson95Ci} • Bootstrap: {m.Bootstrap95Ci}";
                B1RoutingText.Text = $"Routing Acc: {m.RoutingAccuracyDisplay} • Macro F1: {m.MacroAttributionF1Display}";
                B1AbstainText.Text = $"Abstention: {m.AbstentionRateDisplay} • Selective Acc: {m.SelectiveAccuracyDisplay}";
                B1CostText.Text = $"Errors: {m.ApiOrOutputErrors} • Calls: {m.ApiCalls} • Cost: {m.CostUsdDisplay}";
            }
            else if (m.Verifier.Contains("B2", StringComparison.OrdinalIgnoreCase))
            {
                B2AccuracyText.Text = m.DecisionAccuracyDisplay;
                B2CiText.Text = $"Wilson 95%: {m.Wilson95Ci} • Bootstrap: {m.Bootstrap95Ci}";
                B2RoutingText.Text = $"Routing Acc: {m.RoutingAccuracyDisplay} • Macro F1: {m.MacroAttributionF1Display}";
                B2AbstainText.Text = $"Abstention: {m.AbstentionRateDisplay} • Selective Acc: {m.SelectiveAccuracyDisplay}";
                B2CostText.Text = $"Errors: {m.ApiOrOutputErrors} • Calls: {m.ApiCalls} • Cost: {m.CostUsdDisplay}";
            }
        }

        // 2. Paired McNemar tests summary
        if (data.PairedTests.Count > 0)
        {
            var notes = new List<string>();
            foreach (var p in data.PairedTests)
            {
                var holmStr = p.HolmAdjustedP.HasValue ? $"Holm p={p.HolmAdjustedP.Value:F4}" : "p=" + p.ExactMcnemarP.ToString("F4");
                notes.Add($"{p.Comparison}: exact p={p.ExactMcnemarP:F4}, {holmStr}");
            }
            McNemarSummaryText.Text = "Paired McNemar test: " + string.Join(" • ", notes) + ".";
        }

        // 3. Stability records
        _stabilityRecords.Clear();
        foreach (var sr in data.StabilityRecords) _stabilityRecords.Add(sr);

        if (data.StabilityRate.HasValue)
        {
            StabilitySummaryText.Text = $"Stability Agreement Rate: {data.StabilityRate.Value:P2} ({data.StabilityAgreementCount ?? 0}/{data.StabilityComparisonTuples ?? 0} agreement tuples • Cost: ~${data.StabilityCostUsd ?? 0:F4} USD)";
        }

        // 4. Joined CSV
        if (!string.IsNullOrWhiteSpace(csvPath) && File.Exists(csvPath))
        {
            _joinedPredictions.Clear();
            var rows = ArtifactReaderService.ReadJoinedCsv(csvPath);
            foreach (var r in rows) _joinedPredictions.Add(r);
        }

        // 5. Artifact summaries
        _artifacts.Clear();
        var arts = ArtifactReaderService.GetRunArtifacts(rootFolder);
        foreach (var a in arts) _artifacts.Add(a);
    }

    private void LoadExistingResults_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog();
        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            var folder = dialog.SelectedPath;
            var metricsJson = Path.Combine(folder, "results", "metrics_v2.json");
            if (!File.Exists(metricsJson)) metricsJson = Path.Combine(folder, "metrics_v2.json");

            var stabilityJson = Path.Combine(folder, "stability", "phase6_stability_v1.json");
            if (!File.Exists(stabilityJson)) stabilityJson = Path.Combine(folder, "phase6_stability_v1.json");

            var csvPath = Path.Combine(folder, "results", "predictions_joined_private_gold_v2.csv");
            if (!File.Exists(csvPath)) csvPath = Path.Combine(folder, "predictions_joined_private_gold_v2.csv");

            if (File.Exists(metricsJson))
            {
                LoadResultsIntoDashboard(metricsJson, stabilityJson, csvPath, folder);
                BottomStatusText.Text = $"Loaded results from: {folder}";
            }
            else
            {
                WpfMessageBox.Show("Could not find metrics_v2.json in the selected directory.", "Metrics Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void OpenReportMd_Click(object sender, RoutedEventArgs e)
    {
        var ws = WorkspacePathBox.Text.Trim();
        var reportPath = Path.Combine(ws, "ThucNgiem", "RQ3_Phase3", "reports", "PHASE7_AMENDED_MAIN_REPORT_V1.md");
        if (File.Exists(reportPath))
        {
            Process.Start(new ProcessStartInfo { FileName = reportPath, UseShellExecute = true });
        }
    }

    private void OpenJoinedCsv_Click(object sender, RoutedEventArgs e)
    {
        var ws = WorkspacePathBox.Text.Trim();
        var csvPath = Path.Combine(ws, "ThucNgiem", "RQ3_Phase3_Consensus", "phase6_exploratory_v2_results", "predictions_joined_private_gold_v2.csv");
        if (_currentRunFolder != null)
        {
            var candidate = Path.Combine(_currentRunFolder, "results", "predictions_joined_private_gold_v2.csv");
            if (File.Exists(candidate)) csvPath = candidate;
        }
        if (File.Exists(csvPath))
        {
            Process.Start(new ProcessStartInfo { FileName = csvPath, UseShellExecute = true });
        }
    }

    private void OpenMetricsJson_Click(object sender, RoutedEventArgs e)
    {
        var ws = WorkspacePathBox.Text.Trim();
        var jsonPath = Path.Combine(ws, "ThucNgiem", "RQ3_Phase3_Consensus", "phase6_exploratory_v2_results", "metrics_v2.json");
        if (_currentRunFolder != null)
        {
            var candidate = Path.Combine(_currentRunFolder, "results", "metrics_v2.json");
            if (File.Exists(candidate)) jsonPath = candidate;
        }
        if (File.Exists(jsonPath))
        {
            Process.Start(new ProcessStartInfo { FileName = jsonPath, UseShellExecute = true });
        }
    }

    private void CopySha256Summary_Click(object sender, RoutedEventArgs e)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== RQ3 PHASE 6/7 AUDIT ARTIFACT SHA-256 HASHES ===");
        foreach (var a in _artifacts.Where(x => x.Exists))
        {
            sb.AppendLine($"{a.Name}: {a.Sha256}");
        }
        WpfClipboard.SetText(sb.ToString());
        BottomStatusText.Text = "SHA-256 hashes copied to clipboard.";
    }

    #endregion
}
