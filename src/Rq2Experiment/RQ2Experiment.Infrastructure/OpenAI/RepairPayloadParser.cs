using System.Text.Json;

namespace RQ2Experiment.Infrastructure.OpenAI;

internal sealed record ParsedRepairPayload(
    string ReplacementSource,
    string Explanation,
    bool FirstPassJsonValid,
    string NormalizationMethod);

internal static class RepairPayloadParser
{
    public static ParsedRepairPayload Parse(string outputText)
    {
        var trimmed = outputText.Trim();
        if (TryParseObject(trimmed, out var direct))
        {
            return direct with { FirstPassJsonValid = true, NormalizationMethod = "None" };
        }

        var candidates = new List<(string Text, string Method)>();
        var withoutFence = StripMarkdownFence(trimmed);
        AddCandidate(candidates, withoutFence, "MarkdownFence", trimmed);

        if (TryExtractFirstJsonObject(trimmed, out var extracted))
        {
            AddCandidate(candidates, extracted, "ExtractedJsonObject", trimmed);
        }
        if (!withoutFence.Equals(trimmed, StringComparison.Ordinal) &&
            TryExtractFirstJsonObject(withoutFence, out var fenceExtracted))
        {
            AddCandidate(candidates, fenceExtracted, "MarkdownFence+ExtractedJsonObject", trimmed);
        }

        var whitespaceNormalized = NormalizeEscapedWhitespaceOutsideStrings(trimmed);
        AddCandidate(candidates, whitespaceNormalized, "EscapedWhitespace", trimmed);
        if (TryExtractFirstJsonObject(whitespaceNormalized, out var whitespaceExtracted))
        {
            AddCandidate(candidates, whitespaceExtracted, "EscapedWhitespace+ExtractedJsonObject", trimmed);
        }

        foreach (var source in new[] { trimmed, withoutFence, whitespaceNormalized })
        {
            if (!TryUnescapeOneJsonStringLayer(source, out var unescaped)) continue;
            AddCandidate(candidates, unescaped, "JsonStringUnescape", trimmed);
            var unescapedWithoutFence = StripMarkdownFence(unescaped);
            AddCandidate(candidates, unescapedWithoutFence, "JsonStringUnescape+MarkdownFence", trimmed);
            if (TryExtractFirstJsonObject(unescapedWithoutFence, out var unescapedExtracted))
            {
                AddCandidate(
                    candidates,
                    unescapedExtracted,
                    "JsonStringUnescape+ExtractedJsonObject",
                    trimmed);
            }
        }

        foreach (var candidate in candidates)
        {
            if (TryParseObject(candidate.Text, out var parsed))
            {
                return parsed with
                {
                    FirstPassJsonValid = false,
                    NormalizationMethod = candidate.Method
                };
            }
        }

        throw new InvalidOperationException(
            "The model did not return a repair object matching the required JSON schema after deterministic normalization.");
    }

    private static bool TryParseObject(string value, out ParsedRepairPayload payload)
    {
        payload = null!;
        try
        {
            using var document = JsonDocument.Parse(value);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("replacementSource", out var replacement) ||
                replacement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(replacement.GetString()) ||
                !root.TryGetProperty("explanation", out var explanation) ||
                explanation.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            payload = new ParsedRepairPayload(
                replacement.GetString()!,
                explanation.GetString() ?? string.Empty,
                true,
                "None");
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void AddCandidate(
        List<(string Text, string Method)> candidates,
        string candidate,
        string method,
        string original)
    {
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Equals(original, StringComparison.Ordinal)) return;
        if (candidates.Any(item => item.Text.Equals(candidate, StringComparison.Ordinal))) return;
        candidates.Add((candidate.Trim(), method));
    }

    private static string StripMarkdownFence(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return trimmed;
        var firstNewLine = trimmed.IndexOf('\n');
        if (firstNewLine < 0) return trimmed;
        var body = trimmed[(firstNewLine + 1)..];
        var lastFence = body.LastIndexOf("```", StringComparison.Ordinal);
        return (lastFence >= 0 ? body[..lastFence] : body).Trim();
    }

    private static bool TryUnescapeOneJsonStringLayer(string value, out string unescaped)
    {
        unescaped = string.Empty;
        if (!value.Contains("\\\"", StringComparison.Ordinal) &&
            !value.Contains("\\n", StringComparison.Ordinal) &&
            !(value.StartsWith('"') && value.EndsWith('"')))
        {
            return false;
        }

        try
        {
            var encoded = value.StartsWith('"') && value.EndsWith('"')
                ? value
                : $"\"{value}\"";
            unescaped = JsonSerializer.Deserialize<string>(encoded) ?? string.Empty;
            return !string.IsNullOrWhiteSpace(unescaped) && !unescaped.Equals(value, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryExtractFirstJsonObject(string value, out string jsonObject)
    {
        jsonObject = string.Empty;
        var start = value.IndexOf('{');
        if (start < 0) return false;

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = start; index < value.Length; index++)
        {
            var current = value[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (current == '\\')
                {
                    escaped = true;
                }
                else if (current == '"')
                {
                    inString = false;
                }
                continue;
            }

            if (current == '"')
            {
                inString = true;
            }
            else if (current == '{')
            {
                depth++;
            }
            else if (current == '}' && --depth == 0)
            {
                jsonObject = value[start..(index + 1)];
                return true;
            }
        }

        return false;
    }

    private static string NormalizeEscapedWhitespaceOutsideStrings(string value)
    {
        var output = new System.Text.StringBuilder(value.Length);
        var inString = false;
        var escaped = false;
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (inString)
            {
                output.Append(current);
                if (escaped)
                {
                    escaped = false;
                }
                else if (current == '\\')
                {
                    escaped = true;
                }
                else if (current == '"')
                {
                    inString = false;
                }
                continue;
            }

            if (current == '"')
            {
                inString = true;
                output.Append(current);
            }
            else if (current == '\\' && index + 1 < value.Length && value[index + 1] is 'n' or 'r' or 't')
            {
                output.Append(value[index + 1] == 't' ? '\t' : '\n');
                index++;
            }
            else
            {
                output.Append(current);
            }
        }

        return output.ToString();
    }
}
