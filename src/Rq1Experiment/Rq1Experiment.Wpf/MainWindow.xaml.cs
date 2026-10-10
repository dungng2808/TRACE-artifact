using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using Microsoft.Win32;
using Rq1Experiment.Infrastructure.Llm.Common;
using Rq1Experiment.Infrastructure.Java;
using Rq1Experiment.Infrastructure.Generation;
using Rq1Experiment.Infrastructure.Orchestration;
using Rq1Experiment.Infrastructure.Persistence;
using Rq1Experiment.Infrastructure.Prompts;
using Rq1Experiment.Wpf.ViewModels;

namespace Rq1Experiment.Wpf;

public partial class MainWindow : Window
{
    private readonly WorkspaceLayout _layout;
    private readonly MainWindowViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        var workspaceRoot = WorkspaceLayout.FindWorkspaceRoot(Environment.CurrentDirectory);
        _layout = new WorkspaceLayout(workspaceRoot);
        var configStore = new JsonExperimentConfigStore(_layout);
        var apiKeyStore = new FileApiKeyStore(_layout);
        var apiKeyPool = new SequentialApiKeyPool(apiKeyStore, _layout);
        var llmClientFactory = new LlmClientFactory();
        var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var directBaseline = new DirectBaselineService(
            llmClientFactory,
            new Rq1Experiment.Core.Llm.FailoverLlmClient(
                apiKeyPool,
                new HttpLlmTransport(httpClient)));
        var plannedPipeline = new PlannedPipelineService(
            llmClientFactory,
            new Rq1Experiment.Core.Llm.FailoverLlmClient(
                apiKeyPool,
                new HttpLlmTransport(httpClient)));
        var smokeSubjectService = new SmokeSubjectService(new ProcessRunner());

        _viewModel = new MainWindowViewModel(
            configStore,
            apiKeyStore,
            apiKeyPool,
            new JsonModelCatalogService(_layout),
            new JsonPhaseProgressStore(_layout),
            new ScaffoldExperimentOrchestrator(
                _layout,
                apiKeyPool,
                new RepositoryPreflightService(new ProcessRunner()),
                new SubjectSelectionService(new ProcessRunner()),
                smokeSubjectService,
                new StructuralTargetService(new ProcessRunner()),
                new RepositoryContextPromptService(new ProcessRunner()),
                directBaseline,
                plannedPipeline,
                new GatedToolExecutionService(new ProcessRunner()),
                new MetricsLedgerService(),
                new RunManagementService()),
            llmClientFactory,
            new OfficialConfigLockService(_layout),
            smokeSubjectService,
            _layout.WorkspaceRoot);

        _viewModel.BrowseRepositoryRequested += ViewModelOnBrowseRepositoryRequested;
        _viewModel.BrowseJavaHomeRequested += ViewModelOnBrowseJavaHomeRequested;
        _viewModel.OpenResultsRequested += ViewModelOnOpenResultsRequested;
        _viewModel.CreateNewExperimentRequested += ViewModelOnCreateNewExperimentRequested;
        _viewModel.DeleteExperimentRequested += ViewModelOnDeleteExperimentRequested;
        DataContext = _viewModel;
        Loaded += MainWindow_OnLoaded;
        Closing += MainWindow_OnClosing;
    }

    private async void ViewModelOnCreateNewExperimentRequested(object? sender, EventArgs e)
    {
        var suggested = $"rq1-{DateTimeOffset.Now:yyyyMMdd-HHmmss}";
        var dialog = new ExperimentNameDialog(suggested) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await _viewModel.CreateNewExperimentAsync(dialog.ExperimentName);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Unable to create experiment",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void ViewModelOnDeleteExperimentRequested(object? sender, EventArgs e)
    {
        var id = _viewModel.SelectedExistingExperimentId;
        if (string.IsNullOrWhiteSpace(id)) return;
        var answer = MessageBox.Show(this,
            $"Delete experiment '{id}'?\n\n"
            + "Its config snapshot, generated runs, metrics ledger, tool workspaces and locks will be permanently deleted. "
            + "Shared dataset/subject/target files and API keys will be preserved.",
            "Confirm experiment deletion", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            await _viewModel.DeleteSelectedExperimentAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Unable to delete experiment",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.InitializeAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Initialization failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ViewModelOnBrowseRepositoryRequested(object? sender, EventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Java repository",
            InitialDirectory = Directory.Exists(_viewModel.RepositoryRoot)
                ? _viewModel.RepositoryRoot
                : _layout.WorkspaceRoot
        };
        if (dialog.ShowDialog(this) == true)
            _viewModel.SetRepositoryRoot(dialog.FolderName);
    }

    private void ViewModelOnBrowseJavaHomeRequested(object? sender, EventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select JDK home (folder containing bin/java.exe)",
            InitialDirectory = Directory.Exists(_viewModel.JavaHome)
                ? _viewModel.JavaHome
                : _layout.WorkspaceRoot
        };
        if (dialog.ShowDialog(this) == true)
            _viewModel.JavaHome = dialog.FolderName;
    }

    private async void ViewModelOnOpenResultsRequested(object? sender, EventArgs e)
    {
        var outputRoot = _layout.ResolveOutputRoot(_viewModel.OutputRoot);
        var viewModel = new ResultsWindowViewModel(
            new MetricsLedgerService(),
            new Rq1Experiment.Contracts.Experiments.MetricsLedgerRequest(
                _viewModel.ExperimentId, _viewModel.SelectedMode, outputRoot, _layout.WorkspaceRoot,
                Path.Combine(_layout.ExperimentConfigHistoryDirectory, _viewModel.ExperimentId + ".json")),
            () => _viewModel.IsBusy,
            new RunManagementService());
        var window = new ResultsWindow(viewModel) { Owner = this };
        window.Show();
        try
        {
            await viewModel.RebuildAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(window, exception.Message, "Unable to load results",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenRunFolder_OnClick(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(_layout.ResolveOutputRoot(_viewModel.OutputRoot), "runs", _viewModel.ExperimentId);
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void MainWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_viewModel.IsBusy) return;
        var answer = MessageBox.Show(
            this,
            "A pipeline is running. Cancel it and close after saving its checkpoint?",
            "Confirm close",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer == MessageBoxResult.No)
        {
            e.Cancel = true;
            return;
        }

        _viewModel.CancelCommand.Execute(null);
    }
}
