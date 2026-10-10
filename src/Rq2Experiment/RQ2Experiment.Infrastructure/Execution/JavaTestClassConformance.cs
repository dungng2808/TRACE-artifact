using System.Text;
using System.Text.RegularExpressions;

namespace RQ2Experiment.Infrastructure.Execution;

internal sealed record JavaTestConformanceResult(
    IReadOnlyList<string> MissingTests,
    IReadOnlyList<string> ModifiedTests)
{
    public bool IsConformant => MissingTests.Count == 0 && ModifiedTests.Count == 0;
    public int ViolationCount => MissingTests.Count + ModifiedTests.Count;

    public string BuildSummary()
    {
        var parts = new List<string>();
        if (MissingTests.Count > 0) parts.Add($"missing tests: {string.Join(", ", MissingTests)}");
        if (ModifiedTests.Count > 0) parts.Add($"modified existing tests: {string.Join(", ", ModifiedTests)}");
        return IsConformant
            ? "All non-focal test methods were preserved."
            : "Test-class conformance rejected the candidate (" + string.Join("; ", parts) + ").";
    }
}

internal static partial class JavaTestClassConformance
{
    public static JavaTestConformanceResult Analyze(
        string baselineSource,
        string candidateSource,
        string focalMethodName)
    {
        var baseline = ExtractTests(baselineSource);
        var candidate = ExtractTests(candidateSource);
        var missing = new List<string>();
        var modified = new List<string>();

        foreach (var (methodName, source) in baseline)
        {
            if (methodName.Equals(focalMethodName, StringComparison.Ordinal)) continue;
            if (!candidate.TryGetValue(methodName, out var candidateSourceMethod))
            {
                missing.Add(methodName);
            }
            else if (!NormalizeJava(source).Equals(NormalizeJava(candidateSourceMethod), StringComparison.Ordinal))
            {
                modified.Add(methodName);
            }
        }

        return new JavaTestConformanceResult(missing, modified);
    }

    private static IReadOnlyDictionary<string, string> ExtractTests(string source)
    {
        var tests = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in VoidMethodRegex().Matches(source))
        {
            var methodName = match.Groups["name"].Value;
            string methodSource;
            try
            {
                methodSource = JavaMethodEditor.ExtractMethod(source, methodName);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            if (!TestAnnotationRegex().IsMatch(methodSource)) continue;
            if (!tests.TryAdd(methodName, methodSource))
            {
                throw new InvalidOperationException($"Duplicate Java test method name '{methodName}' is not supported.");
            }
        }

        return tests;
    }

    private static string NormalizeJava(string source)
    {
        var output = new StringBuilder(source.Length);
        var state = LexicalState.Code;
        for (var index = 0; index < source.Length; index++)
        {
            var current = source[index];
            var next = index + 1 < source.Length ? source[index + 1] : '\0';
            switch (state)
            {
                case LexicalState.Code when char.IsWhiteSpace(current):
                    break;
                case LexicalState.Code when current == '/' && next == '/':
                    state = LexicalState.LineComment;
                    index++;
                    break;
                case LexicalState.Code when current == '/' && next == '*':
                    state = LexicalState.BlockComment;
                    index++;
                    break;
                case LexicalState.Code when current == '"':
                    output.Append(current);
                    state = LexicalState.String;
                    break;
                case LexicalState.Code when current == '\'':
                    output.Append(current);
                    state = LexicalState.Character;
                    break;
                case LexicalState.Code:
                    output.Append(current);
                    break;
                case LexicalState.LineComment when current == '\n':
                    state = LexicalState.Code;
                    break;
                case LexicalState.BlockComment when current == '*' && next == '/':
                    state = LexicalState.Code;
                    index++;
                    break;
                case LexicalState.String:
                case LexicalState.Character:
                    output.Append(current);
                    if (current == '\\' && index + 1 < source.Length)
                    {
                        output.Append(source[++index]);
                    }
                    else if (state == LexicalState.String && current == '"')
                    {
                        state = LexicalState.Code;
                    }
                    else if (state == LexicalState.Character && current == '\'')
                    {
                        state = LexicalState.Code;
                    }
                    break;
            }
        }

        return output.ToString();
    }

    [GeneratedRegex(@"(?m)^[ \t]*(?:(?:public|protected|private|static|final|synchronized)[ \t]+)*void[ \t]+(?<name>[A-Za-z_$][A-Za-z0-9_$]*)[ \t]*\(")]
    private static partial Regex VoidMethodRegex();

    [GeneratedRegex(@"(?m)^\s*@(?:org\.junit\.)?(?:jupiter\.api\.)?(?:Test|ParameterizedTest|RepeatedTest)\b")]
    private static partial Regex TestAnnotationRegex();

    private enum LexicalState
    {
        Code,
        String,
        Character,
        LineComment,
        BlockComment
    }
}
