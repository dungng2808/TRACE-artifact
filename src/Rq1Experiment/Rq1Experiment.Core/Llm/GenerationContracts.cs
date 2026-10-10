using System.Text;
using Rq1Experiment.Contracts.Prompts;

namespace Rq1Experiment.Core.Llm;

public static class GeneratedTestIdentity
{
    public static string For(RepositoryContext context)
    {
        var focalSimpleName = context.FocalClass.Split('.').Last().Split('$').Last();
        var simpleName = Sanitize(focalSimpleName) + "GeneratedTest";
        return string.IsNullOrWhiteSpace(context.PackageName)
            ? simpleName
            : context.PackageName + "." + simpleName;
    }

    private static string Sanitize(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var character in value)
            result.Append(char.IsLetterOrDigit(character) || character is '_' or '$' ? character : '_');
        if (result.Length == 0 || !(char.IsLetter(result[0]) || result[0] is '_' or '$'))
            result.Insert(0, '_');
        return result.ToString();
    }
}

public readonly record struct TokenBudgetAllocation(
    string EstimatorVersion,
    int EstimatedInputTokens,
    int SafetyReserveTokens,
    int MaxOutputTokens);

public static class ConservativeTokenBudget
{
    public const string EstimatorVersion = "utf16-char-third-plus-reserve-v1";

    public static TokenBudgetAllocation Allocate(
        int totalTokenBudget,
        string input,
        int maxOutputTokensPerCall)
    {
        // Exact provider tokenizers are not available uniformly across all three providers.
        // Code/context is estimated conservatively at one token per three UTF-16 chars,
        // with an additional reserve to absorb tokenizer and wrapper differences.
        var estimatedInput = (int)Math.Ceiling((input?.Length ?? 0) / 3d);
        // Keep the reserve proportional for production-sized windows without making
        // deliberately small smoke/test budgets unusable.
        var desiredReserve = Math.Max(256, (int)Math.Ceiling(totalTokenBudget * 0.025d));
        var reserve = Math.Min(desiredReserve, Math.Max(1, totalTokenBudget / 8));
        var available = Math.Max(0, totalTokenBudget - estimatedInput - reserve);
        return new TokenBudgetAllocation(
            EstimatorVersion,
            estimatedInput,
            reserve,
            Math.Min(maxOutputTokensPerCall, available));
    }
}
