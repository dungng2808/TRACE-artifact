using Rq1Experiment.Contracts.Config;
using Rq1Experiment.Core.Abstractions;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Rq1Experiment.Infrastructure.Persistence;

public sealed class JsonModelCatalogService(
    WorkspaceLayout layout,
    HttpClient? httpClient = null) : IModelCatalogService
{
    private readonly HttpClient _httpClient = httpClient ?? new HttpClient();
    private readonly SemaphoreSlim _catalogGate = new(1, 1);

    public async Task<IReadOnlyList<string>> GetModelsAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        var provider = ProviderIds.All.FirstOrDefault(
            value => value.Equals(providerId, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
            throw new ArgumentException("Unsupported provider.", nameof(providerId));

        var catalog = await JsonSupport.ReadAsync<Dictionary<string, List<string>>>(
            layout.ModelCatalogPath,
            cancellationToken) ?? [];

        return catalog.TryGetValue(provider, out var models)
            ? models.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray()
            : [];
    }

    public async Task<ModelCatalogRefreshResult> RefreshAsync(
        string providerId,
        string? baseUrl,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        var provider = ProviderIds.All.FirstOrDefault(
            value => value.Equals(providerId, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
            return new ModelCatalogRefreshResult(false, [], "Unsupported provider.");
        if (string.IsNullOrWhiteSpace(apiKey))
            return new ModelCatalogRefreshResult(false, [], "At least one API key is required to reload models.");

        try
        {
            using var request = BuildRequest(provider, baseUrl, apiKey);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new ModelCatalogRefreshResult(
                    false,
                    [],
                    $"Model API returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
            }

            var models = ParseModels(provider, body);
            if (models.Count == 0)
                return new ModelCatalogRefreshResult(false, [], "Provider returned no text-generation model IDs.");

            await SaveProviderCatalogAsync(provider, models, cancellationToken);
            return new ModelCatalogRefreshResult(true, models, $"Loaded {models.Count} model(s) from {provider}.");
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            return new ModelCatalogRefreshResult(false, [], $"Cannot reload models: {exception.Message}");
        }
    }

    private static HttpRequestMessage BuildRequest(string provider, string? configuredBaseUrl, string apiKey)
    {
        if (provider == ProviderIds.Gemini)
        {
            var baseUrl = NormalizeBaseUrl(configuredBaseUrl, "https://generativelanguage.googleapis.com");
            return new HttpRequestMessage(
                HttpMethod.Get,
                $"{baseUrl}/v1beta/models?pageSize=1000&key={Uri.EscapeDataString(apiKey)}");
        }

        var defaultBaseUrl = provider switch
        {
            ProviderIds.DeepSeek => "https://api.deepseek.com",
            ProviderIds.TeamoRouter => "https://api.teamorouter.com/v1",
            ProviderIds.JustWoker => "https://api.justwoker.icu/v1",
            _ => "https://openrouter.ai/api/v1"
        };
        var endpoint = provider == ProviderIds.OpenRouter ? "/models/user" : "/models";
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            NormalizeBaseUrl(configuredBaseUrl, defaultBaseUrl) + endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return request;
    }

    private static IReadOnlyList<string> ParseModels(string provider, string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var models = new List<string>();

        if (provider == ProviderIds.Gemini)
        {
            if (!root.TryGetProperty("models", out var items) || items.ValueKind != JsonValueKind.Array)
                return [];

            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("supportedGenerationMethods", out var methods)
                    && methods.ValueKind == JsonValueKind.Array
                    && !methods.EnumerateArray().Any(value => value.GetString() == "generateContent"))
                    continue;

                var id = item.TryGetProperty("baseModelId", out var baseModelId)
                    ? baseModelId.GetString()
                    : item.TryGetProperty("name", out var name)
                        ? name.GetString()?.Replace("models/", string.Empty, StringComparison.Ordinal)
                        : null;
                if (!string.IsNullOrWhiteSpace(id)) models.Add(id);
            }
        }
        else
        {
            if (!root.TryGetProperty("data", out var items) || items.ValueKind != JsonValueKind.Array)
                return [];
            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("id", out var id) && !string.IsNullOrWhiteSpace(id.GetString()))
                    models.Add(id.GetString()!);
            }
        }

        return models.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private async Task SaveProviderCatalogAsync(
        string provider,
        IReadOnlyList<string> models,
        CancellationToken cancellationToken)
    {
        await _catalogGate.WaitAsync(cancellationToken);
        try
        {
            var catalog = await JsonSupport.ReadAsync<Dictionary<string, List<string>>>(
                layout.ModelCatalogPath,
                cancellationToken) ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            catalog[provider] = models.ToList();
            await JsonSupport.WriteAsync(layout.ModelCatalogPath, catalog, cancellationToken);
        }
        finally
        {
            _catalogGate.Release();
        }
    }

    private static string NormalizeBaseUrl(string? configured, string fallback) =>
        (string.IsNullOrWhiteSpace(configured) ? fallback : configured).TrimEnd('/');
}
