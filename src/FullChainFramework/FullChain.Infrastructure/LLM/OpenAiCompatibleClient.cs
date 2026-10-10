using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FullChain.Contracts;

namespace FullChain.Infrastructure.LLM;

public sealed record LlmResponse(
    string Content,
    int PromptTokens,
    int CompletionTokens,
    long LatencyMs,
    string ModelUsed
);

public interface ILlmClient
{
    Task<LlmResponse> SendPromptAsync(
        string systemPrompt,
        string userPrompt,
        IGlobalBudgetGovernor budget,
        CancellationToken ct = default);
}

public sealed class OpenAiCompatibleClient : ILlmClient
{
    private readonly HttpClient _httpClient;
    private readonly string _endpoint;
    private readonly string _modelId;
    private readonly string _apiKey;
    private readonly double _temperature;
    private readonly int _timeoutSeconds;
    private readonly string _provider;

    public OpenAiCompatibleClient(
        LlmConfiguration config,
        HttpClient? httpClient = null,
        string? resolvedApiKey = null,
        IApiKeyResolver? apiKeyResolver = null)
    {
        _endpoint = config.Endpoint;
        _modelId = config.ModelId;
        _temperature = config.Temperature;
        _timeoutSeconds = config.TimeoutSeconds > 0 ? config.TimeoutSeconds : 120;
        _provider = config.Provider.ToLowerInvariant();

        _apiKey = resolvedApiKey ?? (apiKeyResolver ?? new ApiKeyResolver()).Resolve(config);
        ProviderCredentialValidator.EnsureUsable(config, _apiKey);
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(_timeoutSeconds) };
    }

    public async Task<LlmResponse> SendPromptAsync(
        string systemPrompt,
        string userPrompt,
        IGlobalBudgetGovernor budget,
        CancellationToken ct = default)
    {
        budget.CheckBudget("LLM_REQUEST", estimatedTokens: 1000);

        // If provider is "agy" (Antigravity CLI local executor)
        if (_provider.Equals("agy", StringComparison.OrdinalIgnoreCase))
        {
            return await ExecuteViaAgyCliAsync(systemPrompt, userPrompt, budget, ct);
        }

        var requestPayload = new
        {
            model = _modelId,
            temperature = _temperature,
            stream = false,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            response_format = new { type = "json_object" }
        };

        var jsonBody = JsonSerializer.Serialize(requestPayload);
        var stopwatch = Stopwatch.StartNew();

        int maxRetries = 2;
        int attempt = 0;
        Exception? lastException = null;

        while (attempt <= maxRetries)
        {
            attempt++;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
                request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                if (!string.IsNullOrWhiteSpace(_apiKey))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                }

                // Extra headers for OpenRouter
                if (_endpoint.Contains("openrouter", StringComparison.OrdinalIgnoreCase))
                {
                    request.Headers.Add("HTTP-Referer", "https://github.com/full-chain-framework");
                    request.Headers.Add("X-Title", "FullChainFramework");
                }

                using var response = await _httpClient.SendAsync(request, ct);
                var responseContent = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode)
                {
                    var statusCode = (int)response.StatusCode;
                    if ((statusCode == 429 || statusCode >= 500) && attempt <= maxRetries)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
                        continue;
                    }
                    throw new ProviderFailureException($"LLM provider returned HTTP {statusCode}.");
                }

                string content = string.Empty;
                int promptTokens = 0;
                int completionTokens = 0;

                var trimmedResponse = responseContent.TrimStart();
                if (trimmedResponse.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    var contentBuilder = new StringBuilder();
                    using var reader = new StringReader(responseContent);
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        var trimmedLine = line.Trim();
                        if (trimmedLine.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                        {
                            var dataPayload = trimmedLine.Substring(5).Trim();
                            if (string.Equals(dataPayload, "[DONE]", StringComparison.OrdinalIgnoreCase))
                            {
                                break;
                            }
                            if (string.IsNullOrEmpty(dataPayload))
                            {
                                continue;
                            }

                            try
                            {
                                using var chunkDoc = JsonDocument.Parse(dataPayload);
                                var chunkRoot = chunkDoc.RootElement;
                                if (chunkRoot.TryGetProperty("choices", out var choicesElem) && choicesElem.GetArrayLength() > 0)
                                {
                                    var firstChoice = choicesElem[0];
                                    if (firstChoice.TryGetProperty("delta", out var deltaElem))
                                    {
                                        if (deltaElem.TryGetProperty("content", out var deltaContent) && deltaContent.ValueKind == JsonValueKind.String)
                                        {
                                            contentBuilder.Append(deltaContent.GetString());
                                        }
                                    }
                                    else if (firstChoice.TryGetProperty("message", out var msgElem))
                                    {
                                        if (msgElem.TryGetProperty("content", out var msgContent) && msgContent.ValueKind == JsonValueKind.String)
                                        {
                                            contentBuilder.Append(msgContent.GetString());
                                        }
                                    }
                                }

                                if (chunkRoot.TryGetProperty("usage", out var usageElem) && usageElem.ValueKind == JsonValueKind.Object)
                                {
                                    if (usageElem.TryGetProperty("prompt_tokens", out var pt)) promptTokens = pt.GetInt32();
                                    if (usageElem.TryGetProperty("completion_tokens", out var ctElem)) completionTokens = ctElem.GetInt32();
                                }
                            }
                            catch
                            {
                                // Ignore partial or malformed chunk
                            }
                        }
                    }
                    content = contentBuilder.ToString();
                }
                else
                {
                    using var doc = JsonDocument.Parse(responseContent);
                    var root = doc.RootElement;
                    var choices = root.GetProperty("choices");
                    if (choices.GetArrayLength() == 0)
                    {
                        throw new InvalidOperationException("LLM returned 0 choices in response.");
                    }

                    content = choices[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty;

                    if (root.TryGetProperty("usage", out var usageElem))
                    {
                        if (usageElem.TryGetProperty("prompt_tokens", out var pt)) promptTokens = pt.GetInt32();
                        if (usageElem.TryGetProperty("completion_tokens", out var ctElem)) completionTokens = ctElem.GetInt32();
                    }
                }

                stopwatch.Stop();
                budget.RecordUsage("LLM_REQUEST", 1, promptTokens + completionTokens, stopwatch.Elapsed.TotalSeconds);

                return new LlmResponse(
                    Content: CleanMarkdownFences(content),
                    PromptTokens: promptTokens,
                    CompletionTokens: completionTokens,
                    LatencyMs: stopwatch.ElapsedMilliseconds,
                    ModelUsed: _modelId
                );
            }
            catch (ProviderFailureException)
            {
                throw;
            }
            catch (Exception ex) when (attempt <= maxRetries && !ct.IsCancellationRequested)
            {
                lastException = ex;
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
            }
        }

        throw new ProviderFailureException($"LLM call failed after {maxRetries} retries: {lastException?.Message}", lastException);
    }

    private async Task<LlmResponse> ExecuteViaAgyCliAsync(
        string systemPrompt,
        string userPrompt,
        IGlobalBudgetGovernor budget,
        CancellationToken ct)
    {
        var combinedPrompt = $"{systemPrompt}\n\nUSER PROMPT:\n{userPrompt}\n\nOutput ONLY strict valid JSON object.";
        var stopwatch = Stopwatch.StartNew();

        var psi = new ProcessStartInfo
        {
            FileName = "agy",
            Arguments = $"-p {EscapeCommandLineArgument(combinedPrompt)}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync(ct);
        stopwatch.Stop();

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"agy CLI execution failed with code {process.ExitCode}: {stderr}");
        }

        var cleaned = CleanMarkdownFences(stdout.Trim());
        // Estimate rough token usage for agy CLI (approx 4 chars per token)
        int promptTokens = combinedPrompt.Length / 4;
        int completionTokens = cleaned.Length / 4;

        budget.RecordUsage("LLM_REQUEST", 1, promptTokens + completionTokens, stopwatch.Elapsed.TotalSeconds);

        return new LlmResponse(
            Content: cleaned,
            PromptTokens: promptTokens,
            CompletionTokens: completionTokens,
            LatencyMs: stopwatch.ElapsedMilliseconds,
            ModelUsed: "agy-default"
        );
    }

    private static string CleanMarkdownFences(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed["```json".Length..].TrimStart();
        }
        else if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            trimmed = trimmed["```".Length..].TrimStart();
        }

        if (trimmed.EndsWith("```", StringComparison.Ordinal))
        {
            trimmed = trimmed[..^3].TrimEnd();
        }

        return trimmed.Trim();
    }

    private static string EscapeCommandLineArgument(string arg)
    {
        return "\"" + arg.Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", "") + "\"";
    }

}
