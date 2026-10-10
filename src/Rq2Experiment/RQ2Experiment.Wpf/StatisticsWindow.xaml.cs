using System.Windows;
using System.Windows.Threading;
using RQ2Experiment.Wpf.ViewModels;

namespace RQ2Experiment.Wpf;

public partial class StatisticsWindow : Window
{
    private bool initialViewportReset;

    public StatisticsWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        ContentRendered += StatisticsWindow_OnContentRendered;
    }

    private void StatisticsWindow_OnContentRendered(object? sender, EventArgs e)
    {
        if (initialViewportReset) return;
        initialViewportReset = true;

        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            // Keep keyboard focus outside the scrollable report. A focused DataGrid can
            // otherwise call BringIntoView after Loaded and hide the first KPI row.
            CloseButton.Focus();
            StatisticsScrollViewer.ScrollToVerticalOffset(0);
            StatisticsScrollViewer.ScrollToTop();
        });
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();
}
