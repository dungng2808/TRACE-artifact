using System.ComponentModel;
using RQ2Experiment.Core.Domain;
using RQ2Experiment.Infrastructure.Execution;

namespace RQ2Experiment.Infrastructure.Diagnostics;

internal static class MeasurementClassificationDiagnostic
{
    public static string Run()
    {
        var root = Path.Combine(
            Environment.CurrentDirectory,
            "artifacts",
            "diagnostics-measurement-classification",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var reportPath = Path.Combine(root, "report.xml");
        var startedAt = DateTimeOffset.UtcNow;
        var succeededProcess = new ProcessResult(0, string.Empty, string.Empty, TimeSpan.FromSeconds(1));

        var missing = JavaBuildRunner.ReadMeasurement(
            succeededProcess,
            reportPath,
            startedAt,
            () => 0.5);
        AssertStatus(missing, MeasurementStatus.ReportMissing);

        File.WriteAllText(reportPath, "<report />");
        File.SetLastWriteTimeUtc(reportPath, startedAt.UtcDateTime.AddMinutes(-10));
        var stale = JavaBuildRunner.ReadMeasurement(
            succeededProcess,
            reportPath,
            startedAt,
            () => 0.5);
        AssertStatus(stale, MeasurementStatus.ReportStale);

        File.SetLastWriteTimeUtc(reportPath, DateTime.UtcNow);
        var invalid = JavaBuildRunner.ReadMeasurement(
            succeededProcess,
            reportPath,
            startedAt,
            () => null);
        AssertStatus(invalid, MeasurementStatus.ReportInvalid);

        var succeeded = JavaBuildRunner.ReadMeasurement(
            succeededProcess,
            reportPath,
            startedAt,
            () => 0.5);
        if (succeeded.Status != MeasurementStatus.Succeeded || succeeded.Value != 0.5)
        {
            throw new InvalidOperationException("Fresh measurement report was not accepted.");
        }

        var failed = JavaBuildRunner.ReadMeasurement(
            new ProcessResult(7, string.Empty, "tool failed", TimeSpan.FromSeconds(2)),
            reportPath,
            startedAt,
            () => 0.5);
        AssertStatus(failed, MeasurementStatus.ToolFailed);
        AssertStatus(
            JavaBuildRunner.ClassifyMeasurementException(new TimeoutException("timed out")),
            MeasurementStatus.ToolTimedOut);
        AssertStatus(
            JavaBuildRunner.ClassifyMeasurementException(new Win32Exception("not found")),
            MeasurementStatus.ToolUnavailable);

        return "Measurement classification and fresh-report enforcement: OK";
    }

    private static void AssertStatus(QualityMeasurementResult result, MeasurementStatus expected)
    {
        if (result.Status != expected || string.IsNullOrWhiteSpace(result.Detail))
        {
            throw new InvalidOperationException(
                $"Measurement classification failed: expected={expected}, actual={result.Status}.");
        }
    }
}
