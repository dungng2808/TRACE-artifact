namespace Rq1Experiment.Core.Abstractions;

public interface IApiKeyPool
{
    Task<ApiKeyLease?> AcquireAsync(string providerId, CancellationToken cancellationToken = default);
    Task MarkQuotaLimitedAsync(ApiKeyLease lease, CancellationToken cancellationToken = default);
    Task MarkUnusableAsync(ApiKeyLease lease, CancellationToken cancellationToken = default);
    Task ResetProviderPoolAsync(string providerId, CancellationToken cancellationToken = default);
}

public sealed record ApiKeyLease(string ProviderId, string SlotId, string Secret);

public interface ILlmProviderClient
{
    string ProviderId { get; }
    LlmRequest CreateRequest(string modelId, string role, string systemPrompt, string userPrompt);
}

public interface ILlmClientFactory
{
    ILlmProviderClient Create(string providerId);
}

public sealed record LlmRequest(
    string ProviderId,
    string ModelId,
    string Role,
    string SystemPrompt,
    string UserPrompt,
    string RequestHash)
{
    public string BaseEndpoint { get; init; } = string.Empty;
    public double Temperature { get; init; }
    public int MaxOutputTokens { get; init; } = 4096;
    public bool RequireJsonObject { get; init; } = true;
    public string ResponseJsonSchema { get; init; } = string.Empty;
    public string ThinkingLevel { get; init; } = string.Empty;
    public string DeepSeekThinkingMode { get; init; } = string.Empty;
    public int TransportRetryCount { get; init; } = 2;
}

public enum LlmTransportOutcome
{
    Success,
    RateLimited,
    QuotaLimited,
    InvalidApiKey,
    TransientFailure,
    PermanentFailure
}

public sealed record LlmTransportResult(
    LlmTransportOutcome Outcome,
    string? ResponseText = null,
    string? ResponseModelId = null,
    string? ErrorCode = null)
{
    public string? RawResponseText { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public int? TotalTokens { get; init; }
    public string? FinishReason { get; init; }
    public int? HttpStatusCode { get; init; }
}

public sealed record LlmCallResult(
    bool IsSuccess,
    bool IsWaitingForApiKey,
    string RequestHash,
    string? ResponseText,
    string? ResponseModelId,
    int SemanticLlmCalls,
    int TransportAttempts,
    IReadOnlyList<string> KeySlots,
    string? ErrorCode)
{
    public string? RawResponseText { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public int? TotalTokens { get; init; }
    public string? FinishReason { get; init; }
    public int? HttpStatusCode { get; init; }
}

public interface ILlmTransport
{
    Task<LlmTransportResult> SendAsync(
        LlmRequest request,
        ApiKeyLease key,
        CancellationToken cancellationToken = default);
}

public interface IFailoverLlmClient
{
    Task<LlmCallResult> SendAsync(
        LlmRequest request,
        CancellationToken cancellationToken = default);
}
