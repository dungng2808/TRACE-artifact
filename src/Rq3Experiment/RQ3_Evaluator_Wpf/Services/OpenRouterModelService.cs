using System.Net.Http;
using System.Text.Json;

namespace RQ3Evaluator.Wpf.Services;

public sealed record OpenRouterModelItem(string Id, string Name, string DisplayText);

public static class OpenRouterModelService
{
    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    public static readonly IReadOnlyList<OpenRouterModelItem> FallbackModels = new List<OpenRouterModelItem>
    {
        new("deepseek-v4-flash", "DeepSeek: DeepSeek V4 Flash", "deepseek-v4-flash (DeepSeek: DeepSeek V4 Flash)"),
        new("deepseek-v4-pro", "DeepSeek: DeepSeek V4 Pro", "deepseek-v4-pro (DeepSeek: DeepSeek V4 Pro)"),
        new("google/gemini-2.5-flash", "Google: Gemini 2.5 Flash", "google/gemini-2.5-flash (Google: Gemini 2.5 Flash)"),
        new("google/gemini-2.5-pro", "Google: Gemini 2.5 Pro", "google/gemini-2.5-pro (Google: Gemini 2.5 Pro)"),
        new("anthropic/claude-sonnet-4", "Anthropic: Claude Sonnet 4", "anthropic/claude-sonnet-4 (Anthropic: Claude Sonnet 4)"),
        new("anthropic/claude-3.5-sonnet", "Anthropic: Claude 3.5 Sonnet", "anthropic/claude-3.5-sonnet (Anthropic: Claude 3.5 Sonnet)"),
        new("openai/gpt-4o", "OpenAI: GPT-4o", "openai/gpt-4o (OpenAI: GPT-4o)"),
        new("openai/gpt-4o-mini", "OpenAI: GPT-4o mini", "openai/gpt-4o-mini (OpenAI: GPT-4o mini)"),
        new("deepseek/deepseek-chat", "DeepSeek: DeepSeek V3", "deepseek/deepseek-chat (DeepSeek: DeepSeek V3)"),
        new("meta-llama/llama-3.3-70b-instruct", "Meta: Llama 3.3 70B", "meta-llama/llama-3.3-70b-instruct (Meta: Llama 3.3 70B)"),
        new("qwen/qwen-2.5-72b-instruct", "Qwen: Qwen 2.5 72B", "qwen/qwen-2.5-72b-instruct (Qwen: Qwen 2.5 72B)")
    };

    public static async Task<List<OpenRouterModelItem>> FetchModelsAsync(string? endpoint = null, string? apiKey = null, CancellationToken ct = default, bool fallbackOnFailure = true)
    {
        try
        {
            var url = "https://openrouter.ai/api/v1/models";
            if (!string.IsNullOrWhiteSpace(endpoint) && endpoint.Contains("/chat/completions"))
            {
                url = endpoint.Replace("/chat/completions", "/models");
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            }

            using var response = await _httpClient.SendAsync(req, ct);
            if (!response.IsSuccessStatusCode)
            {
                return fallbackOnFailure ? FallbackModels.ToList() : new();
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("data", out var dataElem) || dataElem.ValueKind != JsonValueKind.Array)
            {
                return fallbackOnFailure ? FallbackModels.ToList() : new();
            }

            var list = new List<OpenRouterModelItem>();

            foreach (var item in dataElem.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var idElem)) continue;
                var id = idElem.GetString();
                if (string.IsNullOrWhiteSpace(id)) continue;

                // Skip batch and non-standard endpoints that fail on synchronous /chat/completions
                if (id.EndsWith(":batch", StringComparison.OrdinalIgnoreCase) ||
                    id.EndsWith(":free", StringComparison.OrdinalIgnoreCase) ||
                    id.Contains(":batch-", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var name = item.TryGetProperty("name", out var nameElem) ? nameElem.GetString() ?? id : id;

                // Check modality if available
                if (item.TryGetProperty("architecture", out var archElem) && archElem.ValueKind == JsonValueKind.Object)
                {
                    if (archElem.TryGetProperty("modality", out var modElem))
                    {
                        var mod = modElem.GetString() ?? "";
                        if (!string.IsNullOrWhiteSpace(mod) && !mod.Contains("text"))
                        {
                            continue; // Skip non-text models (e.g. image-only)
                        }
                    }
                }

                var displayText = $"{id} ({name})";
                list.Add(new OpenRouterModelItem(id, name, displayText));
            }

            if (list.Count == 0)
            {
                return fallbackOnFailure ? FallbackModels.ToList() : new();
            }

            // Sort: Group by top tier providers, then alphabetical
            return list
                .OrderBy(m => GetProviderPriority(m.Id))
                .ThenBy(m => m.Id)
                .ToList();
        }
        catch
        {
            return fallbackOnFailure ? FallbackModels.ToList() : new();
        }
    }

    public static async Task<List<OpenRouterModelItem>> FetchModelsAsync(string? endpoint,IEnumerable<string> apiKeys,CancellationToken ct=default)
    {
        foreach(var key in apiKeys)
        {
            var models=await FetchModelsAsync(endpoint,key,ct,false);
            if(models.Count>0)return models;
        }
        return FallbackModels.ToList();
    }

    private static int GetProviderPriority(string id)
    {
        var lower = id.ToLowerInvariant();
        if (lower.StartsWith("google/")) return 1;
        if (lower.StartsWith("anthropic/")) return 2;
        if (lower.StartsWith("openai/")) return 3;
        if (lower.StartsWith("deepseek/")) return 4;
        if (lower.StartsWith("meta-llama/") || lower.StartsWith("meta/")) return 5;
        if (lower.StartsWith("qwen/")) return 6;
        if (lower.StartsWith("mistralai/") || lower.StartsWith("mistral/")) return 7;
        if (lower.StartsWith("x-ai/")) return 8;
        return 99;
    }
}
