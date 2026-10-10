using Rq1Experiment.Core.Abstractions;

namespace Rq1Experiment.Core.Llm;

public sealed class FailoverLlmClient(
    IApiKeyPool keyPool,
    ILlmTransport transport) : IFailoverLlmClient
{
    public async Task<LlmCallResult> SendAsync(
        LlmRequest request,
        CancellationToken cancellationToken = default)
    {
        var attempts = 0;
        var slots = new List<string>();

        while (true)
        {
            var key = await keyPool.AcquireAsync(request.ProviderId, cancellationToken);
            if (key is null)
            {
                return new LlmCallResult(
                    false,
                    true,
                    request.RequestHash,
                    null,
                    null,
                    1,
                    attempts,
                    slots,
                    "NO_AVAILABLE_API_KEY");
            }

            attempts++;
            slots.Add(key.SlotId);
            var result = await transport.SendAsync(request, key, cancellationToken);
            switch (result.Outcome)
            {
                case LlmTransportOutcome.Success:
                    return new LlmCallResult(
                        true, false, request.RequestHash, result.ResponseText,
                        result.ResponseModelId, 1, attempts, slots, null)
                    {
                        RawResponseText = result.RawResponseText,
                        InputTokens = result.InputTokens,
                        OutputTokens = result.OutputTokens,
                        TotalTokens = result.TotalTokens,
                        FinishReason = result.FinishReason,
                        HttpStatusCode = result.HttpStatusCode
                    };

                case LlmTransportOutcome.QuotaLimited:
                    await keyPool.MarkQuotaLimitedAsync(key, cancellationToken);
                    continue;

                case LlmTransportOutcome.InvalidApiKey:
                    await keyPool.MarkUnusableAsync(key, cancellationToken);
                    continue;

                case LlmTransportOutcome.RateLimited:
                case LlmTransportOutcome.TransientFailure:
                    if (attempts <= request.TransportRetryCount)
                    {
                        var delaySeconds = result.Outcome == LlmTransportOutcome.RateLimited
                            ? Math.Min(30, 5 * attempts)
                            : Math.Min(10, 2 * attempts);
                        await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
                        continue;
                    }
                    return Failure(request, result, attempts, slots);

                default:
                    return Failure(request, result, attempts, slots);
            }
        }
    }

    private static LlmCallResult Failure(
        LlmRequest request,
        LlmTransportResult result,
        int attempts,
        IReadOnlyList<string> slots) => new(
        false, false, request.RequestHash, null, null,
        1, attempts, slots, result.ErrorCode ?? result.Outcome.ToString())
    {
        RawResponseText = result.RawResponseText,
        HttpStatusCode = result.HttpStatusCode
    };
}
