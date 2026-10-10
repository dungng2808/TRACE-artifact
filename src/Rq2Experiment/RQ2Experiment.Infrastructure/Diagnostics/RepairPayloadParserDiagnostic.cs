using RQ2Experiment.Infrastructure.OpenAI;

namespace RQ2Experiment.Infrastructure.Diagnostics;

internal static class RepairPayloadParserDiagnostic
{
    public static string Run()
    {
        const string directJson = """
            {"replacementSource":"@Test void repaired() {}","explanation":"fixed"}
            """;
        AssertParsed(directJson, true, "None");

        AssertParsed(
            """
            ```json
            {"replacementSource":"@Test void repaired() {}","explanation":"fixed"}
            ```
            """,
            false,
            "MarkdownFence");

        AssertParsed(
            directJson + "```",
            false,
            "ExtractedJsonObject");

        AssertParsed(
            "Here is the repair:\n" + directJson + "\nEnd of response.",
            false,
            "ExtractedJsonObject");

        AssertParsed(
            """
            {\n  \"replacementSource\": \"@Test void repaired() { call(\\\"value\\\"); }\",\n  \"explanation\": \"fixed\"\n}
            """,
            false,
            "JsonStringUnescape");

        AssertParsed(
            """
            {\n  "explanation": "fixed",\n  "replacementSource": "@Test void repaired() { call(\"value\"); }"\n}
            """,
            false,
            "EscapedWhitespace");

        try
        {
            RepairPayloadParser.Parse("{\"replacementSource\":\"\",\"explanation\":\"missing source\"}");
            throw new InvalidOperationException("Invalid repair schema was unexpectedly accepted.");
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains("required JSON schema", StringComparison.Ordinal))
        {
            // Expected.
        }

        return "Repair response normalization: direct JSON, fences, trailing text, extraction, escaped whitespace, and one-layer unescape: OK";
    }

    private static void AssertParsed(string value, bool expectedFirstPass, string expectedMethod)
    {
        var parsed = RepairPayloadParser.Parse(value);
        if (parsed.FirstPassJsonValid != expectedFirstPass ||
            parsed.NormalizationMethod != expectedMethod ||
            !parsed.ReplacementSource.Contains("repaired", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Repair payload normalization diagnostic failed for {expectedMethod}; " +
                $"actual={parsed.NormalizationMethod}, firstPass={parsed.FirstPassJsonValid}.");
        }
    }
}
