using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Rq1Experiment.Contracts.Config;
using Rq1Experiment.Core.Abstractions;

namespace Rq1Experiment.Infrastructure.Llm.Common;

public sealed class HttpLlmTransport(HttpClient httpClient) : ILlmTransport
{
    public async Task<LlmTransportResult> SendAsync(
        LlmRequest request,
        ApiKeyLease key,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var message = request.ProviderId.ToLowerInvariant() switch
            {
                ProviderIds.Gemini => CreateGeminiRequest(request, key),
                ProviderIds.DeepSeek => CreateOpenAiCompatibleRequest(request, key, "https://api.deepseek.com"),
                ProviderIds.OpenRouter => CreateOpenAiCompatibleRequest(request, key, "https://openrouter.ai/api/v1"),
                ProviderIds.TeamoRouter => CreateOpenAiCompatibleRequest(request, key, "https://api.teamorouter.com/v1"),
                ProviderIds.JustWoker => CreateOpenAiCompatibleRequest(request, key, "https://api.justwoker.icu/v1"),
                _ => throw new ArgumentException("Unsupported LLM provider: " + request.ProviderId)
            };
            if (UsesStreaming(request.ProviderId))
            {
                return await SendOpenAiCompatibleStreamingAsync(httpClient, message, cancellationToken);
            }
            using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseContentRead, cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                return Error(response.StatusCode, raw);
            return request.ProviderId.Equals(ProviderIds.Gemini, StringComparison.OrdinalIgnoreCase)
                ? ParseGemini(raw, response.StatusCode)
                : ParseOpenAiCompatible(raw, response.StatusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new LlmTransportResult(LlmTransportOutcome.TransientFailure, ErrorCode: "HTTP_TIMEOUT");
        }
        catch (HttpRequestException exception)
        {
            return new LlmTransportResult(LlmTransportOutcome.TransientFailure, ErrorCode: "HTTP_NETWORK_ERROR")
            { RawResponseText = exception.Message };
        }
        catch (JsonException exception)
        {
            return new LlmTransportResult(LlmTransportOutcome.PermanentFailure, ErrorCode: "INVALID_PROVIDER_RESPONSE_JSON")
            { RawResponseText = exception.Message };
        }
    }

    private static HttpRequestMessage CreateGeminiRequest(LlmRequest request, ApiKeyLease key)
    {
        var root = string.IsNullOrWhiteSpace(request.BaseEndpoint)
            ? "https://generativelanguage.googleapis.com" : request.BaseEndpoint.TrimEnd('/');
        if (root.EndsWith("/v1beta", StringComparison.OrdinalIgnoreCase)) root = root[..^7];
        var model = Uri.EscapeDataString(request.ModelId.Replace("models/", "", StringComparison.OrdinalIgnoreCase));
        var generationConfig = new Dictionary<string, object?>
        {
            ["maxOutputTokens"] = request.MaxOutputTokens,
            ["responseMimeType"] = request.RequireJsonObject ? "application/json" : "text/plain"
        };
        if (!request.ModelId.Contains("gemini-3", StringComparison.OrdinalIgnoreCase))
            generationConfig["temperature"] = request.Temperature;
        if (!string.IsNullOrWhiteSpace(request.ThinkingLevel))
            generationConfig["thinkingConfig"] = new { thinkingLevel = request.ThinkingLevel };
        if (request.RequireJsonObject && !string.IsNullOrWhiteSpace(request.ResponseJsonSchema))
            generationConfig["responseJsonSchema"] = JsonSerializer.Deserialize<JsonElement>(request.ResponseJsonSchema);
        var body = new
        {
            systemInstruction = new { parts = new[] { new { text = request.SystemPrompt } } },
            contents = new[] { new { role = "user", parts = new[] { new { text = request.UserPrompt } } } },
            generationConfig
        };
        var message = new HttpRequestMessage(HttpMethod.Post,
            $"{root}/v1beta/models/{model}:generateContent");
        message.Headers.Add("x-goog-api-key", key.Secret);
        message.Content = Json(body);
        return message;
    }

    private static HttpRequestMessage CreateOpenAiCompatibleRequest(
        LlmRequest request, ApiKeyLease key, string defaultRoot)
    {
        var root = string.IsNullOrWhiteSpace(request.BaseEndpoint) ? defaultRoot : request.BaseEndpoint.TrimEnd('/');
        var endpoint = root.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? root : root + "/chat/completions";
        var isStreaming = UsesStreaming(request.ProviderId);
        var body = new Dictionary<string, object?>
        {
            ["model"] = request.ModelId,
            ["messages"] = new[]
            {
                new { role = "system", content = request.SystemPrompt },
                new { role = "user", content = request.UserPrompt }
            },
            ["temperature"] = request.Temperature,
            ["max_tokens"] = request.MaxOutputTokens,
            ["response_format"] = request.RequireJsonObject ? new { type = "json_object" } : null,
            ["stream"] = isStreaming,
            ["stream_options"] = isStreaming ? new { include_usage = true } : null
        };
        if (request.ProviderId.Equals(ProviderIds.DeepSeek, StringComparison.OrdinalIgnoreCase)
            || request.ProviderId.Equals(ProviderIds.TeamoRouter, StringComparison.OrdinalIgnoreCase))
            body["thinking"] = new
            {
                type = request.DeepSeekThinkingMode is "disabled" ? "disabled" : "enabled"
            };
        var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Secret);
        message.Content = Json(body);
        return message;
    }

    private static bool UsesStreaming(string providerId) =>
        providerId.Equals(ProviderIds.TeamoRouter, StringComparison.OrdinalIgnoreCase)
        || providerId.Equals(ProviderIds.JustWoker, StringComparison.OrdinalIgnoreCase);

    private static async Task<LlmTransportResult> SendOpenAiCompatibleStreamingAsync(
        HttpClient httpClient,
        HttpRequestMessage message,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var rawFull = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return Error(response.StatusCode, rawFull);
        }

        if (!rawFull.Contains("data:", StringComparison.OrdinalIgnoreCase))
        {
            return ParseOpenAiCompatible(rawFull, response.StatusCode);
        }

        using var reader = new StringReader(rawFull);
        var textBuilder = new StringBuilder();
        string? model = null;
        string? finishReason = null;
        int? promptTokens = null;
        int? completionTokens = null;
        int? totalTokens = null;

        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") break;

            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                if (root.TryGetProperty("model", out var modelProp) && modelProp.GetString() is { } m)
                    model = m;

                if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                {
                    var first = choices[0];
                    if (first.TryGetProperty("delta", out var delta))
                    {
                        if (delta.TryGetProperty("content", out var contentProp) && contentProp.GetString() is { } contentChunk)
                            textBuilder.Append(contentChunk);
                    }
                    if (first.TryGetProperty("finish_reason", out var finishProp) && finishProp.GetString() is { } finish)
                        finishReason = finish;
                }

                if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    promptTokens = Int(usage, "prompt_tokens") ?? promptTokens;
                    completionTokens = Int(usage, "completion_tokens") ?? completionTokens;
                    totalTokens = Int(usage, "total_tokens") ?? totalTokens;
                }
            }
            catch (JsonException)
            {
                // Intermediate SSE chunks
            }
        }

        var fullText = textBuilder.ToString();
        return new LlmTransportResult(LlmTransportOutcome.Success, fullText, model)
        {
            RawResponseText = rawFull,
            InputTokens = promptTokens,
            OutputTokens = completionTokens,
            TotalTokens = totalTokens ?? (promptTokens.HasValue && completionTokens.HasValue ? promptTokens + completionTokens : null),
            FinishReason = finishReason,
            HttpStatusCode = (int)response.StatusCode
        };
    }

    private static StringContent Json(object body) => new(
        JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static LlmTransportResult ParseGemini(string raw, HttpStatusCode status)
    {
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        var text = root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0
            ? candidates[0].GetProperty("content").GetProperty("parts").EnumerateArray()
                .Where(part => part.TryGetProperty("text", out _))
                .Select(part => part.GetProperty("text").GetString() ?? string.Empty)
                .Aggregate(string.Empty, string.Concat)
            : null;
        var usage = root.TryGetProperty("usageMetadata", out var value) ? value : default;
        var finish = candidates.ValueKind == JsonValueKind.Array && candidates.GetArrayLength() > 0
                     && candidates[0].TryGetProperty("finishReason", out var finishValue)
            ? finishValue.GetString() : null;
        return new LlmTransportResult(LlmTransportOutcome.Success, text,
            root.TryGetProperty("modelVersion", out var model) ? model.GetString() : null)
        {
            RawResponseText = raw,
            InputTokens = Int(usage, "promptTokenCount"),
            OutputTokens = Int(usage, "candidatesTokenCount"),
            TotalTokens = Int(usage, "totalTokenCount"),
            FinishReason = finish,
            HttpStatusCode = (int)status
        };
    }

    private static LlmTransportResult ParseOpenAiCompatible(string raw, HttpStatusCode status)
    {
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        var choices = root.GetProperty("choices");
        var first = choices.GetArrayLength() > 0 ? choices[0] : default;
        var text = first.ValueKind == JsonValueKind.Object
                   && first.TryGetProperty("message", out var message)
                   && message.TryGetProperty("content", out var content)
            ? content.GetString() : null;
        var usage = root.TryGetProperty("usage", out var value) ? value : default;
        return new LlmTransportResult(LlmTransportOutcome.Success, text,
            root.TryGetProperty("model", out var model) ? model.GetString() : null)
        {
            RawResponseText = raw,
            InputTokens = Int(usage, "prompt_tokens"),
            OutputTokens = Int(usage, "completion_tokens"),
            TotalTokens = Int(usage, "total_tokens"),
            FinishReason = first.ValueKind == JsonValueKind.Object
                           && first.TryGetProperty("finish_reason", out var finish) ? finish.GetString() : null,
            HttpStatusCode = (int)status
        };
    }

    private static int? Int(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            && value.TryGetInt32(out var number) ? number : null;

    private static LlmTransportResult Error(HttpStatusCode status, string raw)
    {
        var outcome = status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => LlmTransportOutcome.InvalidApiKey,
            HttpStatusCode.TooManyRequests => IsExplicitQuotaExhaustion(raw)
                ? LlmTransportOutcome.QuotaLimited
                : LlmTransportOutcome.RateLimited,
            HttpStatusCode.PaymentRequired => LlmTransportOutcome.QuotaLimited,
            HttpStatusCode.RequestTimeout or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable
                or HttpStatusCode.GatewayTimeout or HttpStatusCode.InternalServerError => LlmTransportOutcome.TransientFailure,
            _ => LlmTransportOutcome.PermanentFailure
        };
        var errorCode = outcome switch
        {
            LlmTransportOutcome.RateLimited => "HTTP_429_RATE_LIMITED",
            LlmTransportOutcome.QuotaLimited => "HTTP_" + (int)status + "_QUOTA_EXHAUSTED",
            _ => "HTTP_" + (int)status
        };
        return new LlmTransportResult(outcome, ErrorCode: errorCode)
        { RawResponseText = raw, HttpStatusCode = (int)status };
    }

    private static bool IsExplicitQuotaExhaustion(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        // RPM/TPM 429 responses are temporary and must not permanently remove
        // an API key. Only explicit daily/billing/credit exhaustion is treated
        // as quota exhaustion. Unknown 429 shapes are conservatively transient.
        return raw.Contains("PerDay", StringComparison.OrdinalIgnoreCase)
               || raw.Contains("per day", StringComparison.OrdinalIgnoreCase)
               || raw.Contains("daily quota", StringComparison.OrdinalIgnoreCase)
               || raw.Contains("quota_exceeded", StringComparison.OrdinalIgnoreCase)
               || raw.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase)
               || raw.Contains("insufficient credit", StringComparison.OrdinalIgnoreCase)
               || raw.Contains("insufficient_credit", StringComparison.OrdinalIgnoreCase)
               || raw.Contains("billing quota", StringComparison.OrdinalIgnoreCase);
    }
}
