using System.Windows;
using System.Windows.Controls;
using System.IO;
using Microsoft.Win32;
using Rq1Experiment.Wpf.ViewModels;

namespace Rq1Experiment.Wpf;

public partial class ResultsWindow : Window
{
    private readonly ResultsWindowViewModel _viewModel;

    public ResultsWindow(ResultsWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
    }

    private void Export_OnClick(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(_viewModel.RunsCsvPath))
        {
            MessageBox.Show(this, "Rebuild the ledger before exporting.", "No runs.csv",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new SaveFileDialog { Title = "Export RQ1 runs.csv", FileName = "runs.csv", Filter = "CSV files (*.csv)|*.csv" };
        if (dialog.ShowDialog(this) != true) return;
        File.Copy(_viewModel.RunsCsvPath, dialog.FileName, overwrite: true);
        MessageBox.Show(this, $"Exported to {dialog.FileName}", "Export complete",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void ViewPlan_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ResultMetricRow row })
            await _viewModel.ShowPlanAsync(row);
    }

    private async void ViewTests_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ResultMetricRow row })
            await _viewModel.ShowTestsAsync(row);
    }

    private async void ViewLogs_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ResultMetricRow row })
            await _viewModel.ShowLogsAsync(row);
    }

    private async void ApproveRow_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ResultMetricRow row })
            await UpdateReviewAsync([row], "APPROVED");
    }

    private async void RejectRow_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ResultMetricRow row })
            await UpdateReviewAsync([row], "REJECTED");
    }

    private async void ApproveSelected_OnClick(object sender, RoutedEventArgs e) =>
        await UpdateReviewAsync(MetricsGrid.SelectedItems.Cast<ResultMetricRow>().ToList(), "APPROVED");

    private async void RejectSelected_OnClick(object sender, RoutedEventArgs e) =>
        await UpdateReviewAsync(MetricsGrid.SelectedItems.Cast<ResultMetricRow>().ToList(), "REJECTED");

    private async void RerunRow_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ResultMetricRow row })
        {
            var answer = MessageBox.Show(this,
                $"Reset artifact cho {row.SubjectId} {row.Approach} R{row.Repetition:00} để chạy lại?\n\n"
                + "Sau khi reset, quay lại Dashboard và nhấn 'Retry failed runs' hoặc 'Resume'.",
                "Xác nhận reset run",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
            await _viewModel.ResetSingleRowAsync(row);
        }
    }

    private async void ResetFailedRuns_OnClick(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "Reset tất cả các run bị lỗi (quá token, compile/test fail, invalid output) để chạy lại?\n\n"
            + "Toàn bộ các run đã VALID/PASS sẽ ĐƯỢC GIỮ NGUYÊN.\n"
            + "Sau khi reset, bạn có thể chỉnh tăng Budget Token trên Dashboard và nhấn 'Retry failed runs'.",
            "Xác nhận reset tất cả run lỗi",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            await _viewModel.ResetFailedRunsAsync();
            MessageBox.Show(this,
                "Đã reset các run lỗi thành công!\n\n"
                + "Hãy quay lại Dashboard, điều chỉnh 'Total token ceiling / run' (ví dụ: 131072 hoặc 200000) và nhấn 'Retry failed runs'.",
                "Reset hoàn tất",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Reset thất bại",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void ResetSelected_OnClick(object sender, RoutedEventArgs e)
    {
        var selected = MetricsGrid.SelectedItems.Cast<ResultMetricRow>().ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "Vui lòng chọn ít nhất một hàng trong bảng.", "Chưa chọn run",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var answer = MessageBox.Show(this,
            $"Reset {selected.Count} run đã chọn để sinh lại?\n\n"
            + "Sau khi reset, quay lại Dashboard và nhấn 'Retry failed runs' hoặc 'Resume'.",
            "Xác nhận reset các run đã chọn",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            await _viewModel.ResetSelectedRowsAsync(selected);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Reset thất bại",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task UpdateReviewAsync(IReadOnlyCollection<ResultMetricRow> rows, string status)
    {
        try
        {
            var reviewableCount = rows.Count(row => row.CanReview);
            if (reviewableCount > 1)
            {
                var answer = MessageBox.Show(this,
                    $"Set {reviewableCount} selected focal methods to {status}?\n\n"
                    + "Only confirm after reviewing the selected methods.",
                    "Confirm bulk manual review",
                    MessageBoxButton.YesNo,
                    status == "REJECTED" ? MessageBoxImage.Warning : MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes) return;
            }
            await _viewModel.SetReviewStatusAsync(rows, status);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Review update failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
