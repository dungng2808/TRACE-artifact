using System.Text.RegularExpressions;

namespace RQ2Experiment.Infrastructure.Execution;

internal static partial class JavaMethodEditor
{
    public static string ExtractMethod(string source, string methodName)
    {
        var range = FindMethodRange(source, methodName);
        return source[range.Start..range.End];
    }

    public static string ReplaceMethod(string source, string methodName, string replacementSource)
    {
        if (replacementSource.Contains("```", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Replacement source must not contain Markdown fences.");
        }

        var range = FindMethodRange(source, methodName);
        if (!MethodNameRegex(methodName).IsMatch(replacementSource))
        {
            throw new InvalidOperationException($"Replacement does not define method '{methodName}'.");
        }

        return source[..range.Start] + replacementSource.Trim() + source[range.End..];
    }

    public static int CountChangedLines(string before, string after)
    {
        var left = SplitLines(before);
        var right = SplitLines(after);
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];

        for (var i = 1; i <= left.Length; i++)
        {
            for (var j = 1; j <= right.Length; j++)
            {
                current[j] = string.Equals(left[i - 1], right[j - 1], StringComparison.Ordinal)
                    ? previous[j - 1] + 1
                    : Math.Max(previous[j], current[j - 1]);
            }

            (previous, current) = (current, previous);
            Array.Clear(current);
        }

        var longestCommonSubsequence = previous[right.Length];
        return left.Length + right.Length - 2 * longestCommonSubsequence;
    }

    public static int CountTestMethods(string source) => TestAnnotationRegex().Count(source);

    private static (int Start, int End) FindMethodRange(string source, string methodName)
    {
        var match = MethodDefinitionRegex(methodName).Match(source);
        if (!match.Success)
        {
            throw new InvalidOperationException($"Could not locate Java method '{methodName}'.");
        }

        var signatureStart = match.Index;
        var start = ExpandToAnnotations(source, signatureStart);
        var openingBrace = source.IndexOf('{', match.Index + match.Length);
        if (openingBrace < 0)
        {
            throw new InvalidOperationException($"Method '{methodName}' has no opening brace.");
        }

        var end = FindMatchingBrace(source, openingBrace) + 1;
        return (start, end);
    }

    private static int ExpandToAnnotations(string source, int signatureStart)
    {
        var lineStart = source.LastIndexOf('\n', Math.Max(0, signatureStart - 1)) + 1;
        var candidate = lineStart;
        while (candidate > 0)
        {
            var previousLineEnd = candidate - 1;
            var previousLineStart = source.LastIndexOf('\n', Math.Max(0, previousLineEnd - 1)) + 1;
            var previousLine = source[previousLineStart..previousLineEnd].Trim();
            if (previousLine.StartsWith('@') || previousLine.Length == 0)
            {
                candidate = previousLineStart;
                continue;
            }

            break;
        }

        return candidate;
    }

    private static int FindMatchingBrace(string source, int openingBrace)
    {
        var depth = 0;
        var state = LexicalState.Code;
        for (var i = openingBrace; i < source.Length; i++)
        {
            var current = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';
            switch (state)
            {
                case LexicalState.Code when current == '/' && next == '/':
                    state = LexicalState.LineComment;
                    i++;
                    break;
                case LexicalState.Code when current == '/' && next == '*':
                    state = LexicalState.BlockComment;
                    i++;
                    break;
                case LexicalState.Code when current == '"':
                    state = LexicalState.String;
                    break;
                case LexicalState.Code when current == '\'':
                    state = LexicalState.Character;
                    break;
                case LexicalState.Code when current == '{':
                    depth++;
                    break;
                case LexicalState.Code when current == '}':
                    depth--;
                    if (depth == 0) return i;
                    break;
                case LexicalState.LineComment when current == '\n':
                    state = LexicalState.Code;
                    break;
                case LexicalState.BlockComment when current == '*' && next == '/':
                    state = LexicalState.Code;
                    i++;
                    break;
                case LexicalState.String when current == '\\':
                case LexicalState.Character when current == '\\':
                    i++;
                    break;
                case LexicalState.String when current == '"':
                    state = LexicalState.Code;
                    break;
                case LexicalState.Character when current == '\'':
                    state = LexicalState.Code;
                    break;
            }
        }

        throw new InvalidOperationException("Unbalanced braces in Java source.");
    }

    private static string[] SplitLines(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    private static Regex MethodDefinitionRegex(string methodName) => new(
        $@"(?m)^[ \t]*(?:(?:public|protected|private|static|final|synchronized)[ \t]+)*(?:void|[A-Za-z_$][\w$<>,.?\[\] ]*)[ \t]+{Regex.Escape(methodName)}[ \t]*\(",
        RegexOptions.CultureInvariant);

    private static Regex MethodNameRegex(string methodName) => new(
        $@"\b{Regex.Escape(methodName)}\s*\(",
        RegexOptions.CultureInvariant);

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
