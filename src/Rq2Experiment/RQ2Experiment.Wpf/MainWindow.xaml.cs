using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using RQ2Experiment.Wpf.ViewModels;

namespace RQ2Experiment.Wpf;

public partial class MainWindow : Window
{
    private StatisticsWindow? _statisticsWindow;

    public MainWindow()
    {
        InitializeComponent();
        var viewModel = new MainViewModel();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.SelectedProvider))
            {
                Dispatcher.BeginInvoke(ApiKeysInputTextBox.Clear, DispatcherPriority.Background);
            }
        };
        DataContext = viewModel;
        Closed += (_, _) =>
        {
            if (DataContext is MainViewModel viewModel)
            {
                viewModel.ClearApiKeys();
            }

            ApiKeysInputTextBox.Clear();
        };
    }

    private void AddApiKeys_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.AddApiKeys(ApiKeysInputTextBox.Text);
            ApiKeysInputTextBox.Clear();
        }
    }

    private void RemoveApiKey_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel) viewModel.RemoveSelectedApiKey();
    }

    private void ClearApiKeys_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel) viewModel.ClearApiKeys();
        ApiKeysInputTextBox.Clear();
    }

    private void BrowseManifest_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        var dialog = new OpenFileDialog
        {
            Title = "Select RQ2 benchmark manifest",
            Filter = "JSON manifest (*.json)|*.json|All files (*.*)|*.*",
            FileName = viewModel.BenchmarkManifestPath
        };
        if (dialog.ShowDialog(this) == true) viewModel.BenchmarkManifestPath = dialog.FileName;
    }

    private void BrowseRepository_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        var dialog = new OpenFolderDialog
        {
            Title = "Select Java repository",
            InitialDirectory = Directory.Exists(viewModel.RepositoryPath) ? viewModel.RepositoryPath : string.Empty
        };
        if (dialog.ShowDialog(this) == true) viewModel.RepositoryPath = dialog.FolderName;
    }

    private void BrowseOutput_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        var dialog = new OpenFolderDialog
        {
            Title = "Select experiment output directory",
            InitialDirectory = Directory.Exists(viewModel.OutputDirectory) ? viewModel.OutputDirectory : string.Empty
        };
        if (dialog.ShowDialog(this) == true) viewModel.OutputDirectory = dialog.FolderName;
    }

    private void ShowStatistics_OnClick(object sender, RoutedEventArgs e)
    {
        if (_statisticsWindow is { IsLoaded: true })
        {
            if (_statisticsWindow.WindowState == WindowState.Minimized)
            {
                _statisticsWindow.WindowState = WindowState.Normal;
            }

            _statisticsWindow.Activate();
            return;
        }

        if (DataContext is not MainViewModel viewModel) return;

        _statisticsWindow = new StatisticsWindow(viewModel)
        {
            Owner = this
        };
        _statisticsWindow.Closed += (_, _) => _statisticsWindow = null;
        _statisticsWindow.Show();
    }
}
