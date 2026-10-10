using System.Security.Cryptography;
using System.Text;
using System.Net;

namespace RQ2Experiment.Infrastructure.OpenAI;

public interface ILlmRepairClient : IDisposable
{
    IReadOnlyList<string> KeyFingerprints { get; }

    Task<LlmRepairResponse> GenerateRepairAsync(
        string model,
        string reasoningEffort,
        string instructions,
        string input,
        string artifactDirectory,
        int iteration,
        CancellationToken cancellationToken,
        string? requestTag = null);
}

internal static class ApiKeyFingerprint
{
    public static string Create(string secret) => string.IsNullOrWhiteSpace(secret)
        ? "NO-AUTH"
        : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)))[..8];
}

internal sealed class LlmHttpException(
    string message,
    HttpStatusCode statusCode,
    TimeSpan? retryAfter = null)
    : HttpRequestException(message, null, statusCode)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

internal readonly record struct LlmUsageSnapshot(
    int InputTokens,
    int OutputTokens,
    int CachedInputTokens,
    int CacheWriteInputTokens,
    int ReasoningTokens,
    bool Reported)
{
    public int TotalTokens => InputTokens + OutputTokens;
}

internal sealed class LlmModelOutputException(
    string message,
    Exception? innerException,
    LlmUsageSnapshot usage,
    string responseId,
    string actualModel)
    : InvalidOperationException(message, innerException)
{
    public LlmUsageSnapshot Usage { get; } = usage;
    public string ResponseId { get; } = responseId;
    public string ActualModel { get; } = actualModel;
}
