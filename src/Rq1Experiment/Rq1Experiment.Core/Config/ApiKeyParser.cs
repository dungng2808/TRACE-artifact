namespace Rq1Experiment.Core.Config;

public static class ApiKeyParser
{
    public static IReadOnlyList<string> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();

        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var key = line.Trim();
            if (key.Length > 0 && seen.Add(key))
            {
                result.Add(key);
            }
        }

        return result;
    }
}
