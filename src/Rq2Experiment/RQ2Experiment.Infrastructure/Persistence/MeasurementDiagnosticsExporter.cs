using System.Globalization;
using System.Text;
using RQ2Experiment.Core.Domain;

namespace RQ2Experiment.Infrastructure.Persistence;

public static class MeasurementDiagnosticsExporter
{
    public static async Task<string> ExportAsync(
        IReadOnlyList<ExperimentRunResult> results,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(results);
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, "measurement-diagnostics.csv");
        var builder = new StringBuilder();
        builder.AppendLine("task_id,project_id,strategy,repetition,status,failure_category,metric,measurement_status,detail,report_path");
        foreach (var result in results.OrderBy(result => result.TaskId).ThenBy(result => result.Strategy))
        {
            Append(builder, result, "coverage", result.FinalCoverageStatus, result.FinalCoverageDetail, result.FinalCoverageReportPath);
            Append(builder, result, "mutation", result.FinalMutationStatus, result.FinalMutationDetail, result.FinalMutationReportPath);
        }
        await File.WriteAllTextAsync(path, builder.ToString(), cancellationToken);
        return path;
    }

    private static void Append(
        StringBuilder builder,
        ExperimentRunResult result,
        string metric,
        MeasurementStatus status,
        string detail,
        string reportPath)
    {
        builder.AppendJoin(',', new[]
        {
            Escape(result.TaskId), Escape(result.ProjectId), Escape(result.Strategy.ToString()),
            result.Repetition.ToString(CultureInfo.InvariantCulture), Escape(result.Status.ToString()),
            Escape(result.FailureCategory.ToString()), Escape(metric), Escape(status.ToString()),
            Escape(detail), Escape(reportPath)
        });
        builder.AppendLine();
    }

    private static string Escape(string value) => '"' + value.Replace("\"", "\"\"") + '"';
}
