using System.Xml.Linq;

namespace RQ2Experiment.Infrastructure.Execution;

internal static class QualityReportReader
{
    public static double? ReadJacocoLineCoverage(
        string reportPath,
        IReadOnlyCollection<string>? targetClassNames = null)
    {
        if (!File.Exists(reportPath)) return null;
        var report = XDocument.Load(reportPath);
        IEnumerable<XElement> counters;
        if (targetClassNames is { Count: > 0 })
        {
            var targets = targetClassNames
                .Select(name => name.Replace('.', '/'))
                .ToHashSet(StringComparer.Ordinal);
            counters = report.Descendants("class")
                .Where(element => targets.Contains((string?)element.Attribute("name") ?? string.Empty))
                .SelectMany(element => element.Elements("counter"))
                .Where(IsLineCounter);
        }
        else
        {
            counters = report.Root?.Elements("counter").Where(IsLineCounter) ?? [];
        }

        var lineCounters = counters.ToArray();
        if (lineCounters.Length == 0) return null;
        var missed = lineCounters.Sum(counter => (int?)counter.Attribute("missed") ?? 0);
        var covered = lineCounters.Sum(counter => (int?)counter.Attribute("covered") ?? 0);
        return missed + covered == 0 ? 0 : (double)covered / (missed + covered);
    }

    public static double? ReadPitMutationScore(string reportPath)
    {
        if (!File.Exists(reportPath)) return null;
        var report = XDocument.Load(reportPath);
        var mutations = report.Descendants("mutation").ToArray();
        if (mutations.Length == 0) return null;
        var killed = mutations.Count(mutation =>
            string.Equals((string?)mutation.Attribute("detected"), "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals((string?)mutation.Attribute("status"), "KILLED", StringComparison.OrdinalIgnoreCase));
        return (double)killed / mutations.Length;
    }

    private static bool IsLineCounter(XElement element) =>
        string.Equals((string?)element.Attribute("type"), "LINE", StringComparison.Ordinal);
}
