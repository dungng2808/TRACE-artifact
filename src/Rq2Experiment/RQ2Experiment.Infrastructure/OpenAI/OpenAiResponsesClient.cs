using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using RQ2Experiment.Infrastructure.LLM;

namespace RQ2Experiment.Infrastructure.OpenAI;

public sealed record LlmRepairResponse(
    string ReplacementSource,
    string Explanation,
    int InputTokens,
    int OutputTokens,
    int CachedInputTokens,
    int CacheWriteInputTokens,
    int ReasoningTokens,
    string ResponseId,
    string ActualModel,
    int ApiCalls = 1,
    int RetryCount = 0,
    int KeyFailoverCount = 0,
    string ApiKeyFingerprint = "",
    bool FirstPassJsonValid = true,
    string OutputNormalization = "None",
    bool TokenUsageReported = true);

/// <summary>
/// Client for OpenAI Responses and OpenAI-compatible Chat Completions endpoints.
/// The historical file/namespace name is retained to avoid breaking existing project layout.
/// </summary>
public sealed class CompatibleLlmClient : ILlmRepairClient
{
    private readonly HttpClient httpClient;
    private readonly LlmApiProtocol protocol;
    private readonly bool useJsonMode;
    private readonly string apiKeyFingerprint;
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public CompatibleLlmClient(
        string apiKey,
        string baseUrl,
        LlmApiProtocol protocol,
        bool requiresApiKey,
        bool useJsonMode)
    {
        if (requiresApiKey && string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("API key is required by the selected provider.", nameof(apiKey));
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            throw new ArgumentException("API base URL is invalid.", nameof(baseUrl));
        }

        var normalizedBaseUrl = baseUri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? baseUri
            : new Uri(baseUri.AbsoluteUri + "/");

        this.protocol = protocol;
        this.useJsonMode = useJsonMode;
        apiKeyFingerprint = ApiKeyFingerprint.Create(apiKey);
        httpClient = new HttpClient
        {
            BaseAddress = normalizedBaseUrl,
            Timeout = TimeSpan.FromMinutes(10)
        };
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("RQ2-Experiment-Workbench/2.0");
    }

    public IReadOnlyList<string> KeyFingerprints => [apiKeyFingerprint];

    public async Task<IReadOnlyList<string>> ListTextModelsAsync(CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync("models", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, body);

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("The models response does not contain a data array. Enter a model ID manually.");
        }

        return data.EnumerateArray()
            .Where(item => item.TryGetProperty("id", out _))
            .Select(item => item.GetProperty("id").GetString())
            .Where(id => !string.IsNullOrWhiteSpace(id) && IsLikelyTextGenerationModel(id!))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task ValidateCredentialsAsync(
        string validationPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(validationPath)) return;

        using var response = await httpClient.GetAsync(validationPath, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, body);
    }

    public async Task<LlmRepairResponse> GenerateRepairAsync(
        string model,
        string reasoningEffort,
        string instructions,
        string input,
        string artifactDirectory,
        int iteration,
        CancellationToken cancellationToken,
        string? requestTag = null)
    {
        Directory.CreateDirectory(artifactDirectory);
        var requestBody = protocol == LlmApiProtocol.Responses
            ? CreateResponsesRequest(model, reasoningEffort, instructions, input)
            : CreateChatCompletionsRequest(model, instructions, input);
        var serializedRequest = JsonSerializer.Serialize(requestBody, jsonOptions);
        var artifactStem = string.IsNullOrWhiteSpace(requestTag)
            ? $"iteration-{iteration:00}"
            : $"iteration-{iteration:00}-{requestTag}";
        var requestPath = Path.Combine(artifactDirectory, $"{artifactStem}-request.json");
        await File.WriteAllTextAsync(requestPath, serializedRequest, cancellationToken);

        var endpoint = protocol == LlmApiProtocol.Responses ? "responses" : "chat/completions";
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(serializedRequest, Encoding.UTF8, "application/json")
        };
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var responsePath = Path.Combine(artifactDirectory, $"{artifactStem}-response.json");
        await File.WriteAllTextAsync(responsePath, body, cancellationToken);
        EnsureSuccess(response, body);

        JsonDocument responseDocument;
        try
        {
            if (body.TrimStart().StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var contentBuilder = new StringBuilder();
                int promptTokens = 0;
                int completionTokens = 0;
                using var reader = new StringReader(body);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    var trimmedLine = line.Trim();
                    if (trimmedLine.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        var dataPayload = trimmedLine.Substring(5).Trim();
                        if (string.Equals(dataPayload, "[DONE]", StringComparison.OrdinalIgnoreCase)) break;
                        if (string.IsNullOrEmpty(dataPayload)) continue;
                        try
                        {
                            using var chunkDoc = JsonDocument.Parse(dataPayload);
                            var chunkRoot = chunkDoc.RootElement;
                            if (chunkRoot.TryGetProperty("choices", out var choicesElem) && choicesElem.GetArrayLength() > 0)
                            {
                                var firstChoice = choicesElem[0];
                                if (firstChoice.TryGetProperty("delta", out var deltaElem) && deltaElem.TryGetProperty("content", out var dc) && dc.ValueKind == JsonValueKind.String)
                                {
                                    contentBuilder.Append(dc.GetString());
                                }
                                else if (firstChoice.TryGetProperty("message", out var msgElem) && msgElem.TryGetProperty("content", out var mc) && mc.ValueKind == JsonValueKind.String)
                                {
                                    contentBuilder.Append(mc.GetString());
                                }
                            }
                            if (chunkRoot.TryGetProperty("usage", out var usageElem) && usageElem.ValueKind == JsonValueKind.Object)
                            {
                                if (usageElem.TryGetProperty("prompt_tokens", out var pt)) promptTokens = pt.GetInt32();
                                if (usageElem.TryGetProperty("completion_tokens", out var ctElem)) completionTokens = ctElem.GetInt32();
                            }
                        }
                        catch { }
                    }
                }
                var reconstructed = JsonSerializer.Serialize(new
                {
                    choices = new[] { new { message = new { content = contentBuilder.ToString() } } },
                    usage = new { prompt_tokens = promptTokens, completion_tokens = completionTokens }
                });
                responseDocument = JsonDocument.Parse(reconstructed);
            }
            else
            {
                responseDocument = JsonDocument.Parse(body);
            }
        }
        catch (JsonException exception)
        {
            await WriteResponseAccountingAsync(
                artifactDirectory,
                artifactStem,
                new LlmUsageSnapshot(0, 0, 0, 0, 0, false),
                accepted: false,
                "The LLM API response envelope was not valid JSON.",
                cancellationToken);
            throw new LlmModelOutputException(
                "The LLM API response envelope was not valid JSON.",
                exception,
                new LlmUsageSnapshot(0, 0, 0, 0, 0, false),
                string.Empty,
                model);
        }
        using (responseDocument)
        {
            var root = responseDocument.RootElement;
            var usage = ReadUsage(root);
            var responseId = root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString() ?? string.Empty
                : string.Empty;
            var actualModelName = root.TryGetProperty("model", out var actualModel) && actualModel.ValueKind == JsonValueKind.String
                ? actualModel.GetString() ?? model
                : model;

            try
            {
                var outputText = protocol == LlmApiProtocol.Responses
                    ? ExtractResponsesOutputText(root)
                    : ExtractChatOutputText(root);
                var repairPayload = RepairPayloadParser.Parse(outputText);
                await File.WriteAllTextAsync(
                    Path.Combine(artifactDirectory, $"{artifactStem}-normalization.json"),
                    JsonSerializer.Serialize(new
                    {
                        accepted = true,
                        repairPayload.FirstPassJsonValid,
                        repairPayload.NormalizationMethod,
                        outputCharacters = outputText.Length,
                        tokenUsageReported = usage.Reported
                    }, jsonOptions),
                    cancellationToken);
                await WriteResponseAccountingAsync(
                    artifactDirectory,
                    artifactStem,
                    usage,
                    accepted: true,
                    error: string.Empty,
                    cancellationToken);

                return new LlmRepairResponse(
                    repairPayload.ReplacementSource,
                    repairPayload.Explanation,
                    usage.InputTokens,
                    usage.OutputTokens,
                    usage.CachedInputTokens,
                    usage.CacheWriteInputTokens,
                    usage.ReasoningTokens,
                    responseId,
                    actualModelName,
                    ApiKeyFingerprint: apiKeyFingerprint,
                    FirstPassJsonValid: repairPayload.FirstPassJsonValid,
                    OutputNormalization: repairPayload.NormalizationMethod,
                    TokenUsageReported: usage.Reported);
            }
            catch (InvalidOperationException exception) when (exception is not LlmModelOutputException)
            {
                await WriteResponseAccountingAsync(
                    artifactDirectory,
                    artifactStem,
                    usage,
                    accepted: false,
                    exception.Message,
                    cancellationToken);
                throw new LlmModelOutputException(
                    exception.Message,
                    exception,
                    usage,
                    responseId,
                    actualModelName);
            }
        }
    }

    public void Dispose() => httpClient.Dispose();

    private Dictionary<string, object?> CreateResponsesRequest(
        string model,
        string reasoningEffort,
        string instructions,
        string input)
    {
        var requestBody = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["instructions"] = instructions,
            ["input"] = input,
            ["max_output_tokens"] = 16_000,
            ["store"] = false,
            ["text"] = new
            {
                format = new
                {
                    type = "json_schema",
                    name = "repair_patch",
                    strict = true,
                    schema = RepairSchema
                }
            }
        };
        if (SupportsReasoningEffort(model))
        {
            requestBody["reasoning"] = new { effort = reasoningEffort };
        }

        return requestBody;
    }

    private Dictionary<string, object?> CreateChatCompletionsRequest(
        string model,
        string instructions,
        string input)
    {
        var requestBody = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = new object[]
            {
                new { role = "system", content = instructions + " Return only one JSON object with replacementSource and explanation." },
                new { role = "user", content = input }
            },
            ["max_tokens"] = 16_000,
            ["stream"] = false
        };
        if (useJsonMode)
        {
            requestBody["response_format"] = new { type = "json_object" };
        }

        return requestBody;
    }

    private static object RepairSchema => new
    {
        type = "object",
        properties = new
        {
            replacementSource = new { type = "string" },
            explanation = new { type = "string" }
        },
        required = new[] { "replacementSource", "explanation" },
        additionalProperties = false
    };

    private static string ExtractResponsesOutputText(JsonElement root)
    {
        if (root.TryGetProperty("output_text", out var directOutput) && directOutput.ValueKind == JsonValueKind.String)
        {
            return directOutput.GetString() ?? string.Empty;
        }

        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
                foreach (var part in content.EnumerateArray())
                {
                    if (part.TryGetProperty("type", out var type) &&
                        type.GetString() == "output_text" &&
                        part.TryGetProperty("text", out var text))
                    {
                        return text.GetString() ?? string.Empty;
                    }
                }
            }
        }

        throw new InvalidOperationException("The Responses API result contains no output text.");
    }

    private static string ExtractChatOutputText(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0 ||
            !choices[0].TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content))
        {
            throw new InvalidOperationException("The Chat Completions result contains no assistant message.");
        }

        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? string.Empty;
        if (content.ValueKind == JsonValueKind.Array)
        {
            var parts = content.EnumerateArray()
                .Where(part => part.TryGetProperty("text", out _))
                .Select(part => part.GetProperty("text").GetString() ?? string.Empty);
            return string.Concat(parts);
        }

        throw new InvalidOperationException("The assistant message content has an unsupported shape.");
    }

    private static int ReadInt(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value) ? value : 0;

    private static int ReadNestedInt(JsonElement parent, string objectName, string propertyName) =>
        parent.TryGetProperty(objectName, out var nested) ? ReadInt(nested, propertyName) : 0;

    private LlmUsageSnapshot ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return new LlmUsageSnapshot(0, 0, 0, 0, 0, false);
        }

        var inputDetails = protocol == LlmApiProtocol.Responses
            ? "input_tokens_details"
            : "prompt_tokens_details";
        var outputDetails = protocol == LlmApiProtocol.Responses
            ? "output_tokens_details"
            : "completion_tokens_details";
        return new LlmUsageSnapshot(
            ReadInt(usage, protocol == LlmApiProtocol.Responses ? "input_tokens" : "prompt_tokens"),
            ReadInt(usage, protocol == LlmApiProtocol.Responses ? "output_tokens" : "completion_tokens"),
            ReadNestedInt(usage, inputDetails, "cached_tokens"),
            ReadNestedInt(usage, inputDetails, "cache_write_tokens"),
            ReadNestedInt(usage, outputDetails, "reasoning_tokens"),
            true);
    }

    private async Task WriteResponseAccountingAsync(
        string artifactDirectory,
        string artifactStem,
        LlmUsageSnapshot usage,
        bool accepted,
        string error,
        CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(
            Path.Combine(artifactDirectory, $"{artifactStem}-accounting.json"),
            JsonSerializer.Serialize(new
            {
                accepted,
                tokenUsageReported = usage.Reported,
                usage.InputTokens,
                usage.OutputTokens,
                totalTokens = usage.TotalTokens,
                usage.CachedInputTokens,
                usage.CacheWriteInputTokens,
                usage.ReasoningTokens,
                error
            }, jsonOptions),
            cancellationToken);
    }

    private static void EnsureSuccess(HttpResponseMessage response, string body)
    {
        if (response.IsSuccessStatusCode) return;
        var safeBody = body.Length <= 1_500 ? body : body[..1_500] + "...";
        var retryAfter = response.Headers.RetryAfter?.Delta;
        if (!retryAfter.HasValue && response.Headers.RetryAfter?.Date is { } retryDate)
        {
            retryAfter = retryDate - DateTimeOffset.UtcNow;
            if (retryAfter < TimeSpan.Zero) retryAfter = TimeSpan.Zero;
        }

        throw new LlmHttpException(
            $"LLM API returned {(int)response.StatusCode} ({response.ReasonPhrase}): {safeBody}",
            response.StatusCode,
            retryAfter);
    }

    private static bool IsLikelyTextGenerationModel(string id)
    {
        string[] excludedFragments =
        [
            "embedding", "embed-", "text-embedding", "moderation", "whisper", "transcribe",
            "text-to-speech", "tts-", "dall-e", "sora", "image-generation", "realtime"
        ];
        return !excludedFragments.Any(fragment => id.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    private static bool SupportsReasoningEffort(string id) =>
        id.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith('o') && id.Length > 1 && char.IsDigit(id[1]);
}
