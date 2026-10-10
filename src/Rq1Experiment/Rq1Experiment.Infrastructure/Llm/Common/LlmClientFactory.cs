using Rq1Experiment.Contracts.Config;
using Rq1Experiment.Core.Abstractions;
using Rq1Experiment.Core.Llm;

namespace Rq1Experiment.Infrastructure.Llm.Common;

public sealed class LlmClientFactory : ILlmClientFactory
{
    public ILlmProviderClient Create(string providerId) => providerId.ToLowerInvariant() switch
    {
        ProviderIds.CodexCli => new ProviderClient(ProviderIds.CodexCli),
        ProviderIds.Gemini => new ProviderClient(ProviderIds.Gemini),
        ProviderIds.DeepSeek => new ProviderClient(ProviderIds.DeepSeek),
        ProviderIds.OpenRouter => new ProviderClient(ProviderIds.OpenRouter),
        ProviderIds.TeamoRouter => new ProviderClient(ProviderIds.TeamoRouter),
        ProviderIds.JustWoker => new ProviderClient(ProviderIds.JustWoker),
        _ => throw new ArgumentException("Unsupported provider.", nameof(providerId))
    };

    private sealed record ProviderClient(string ProviderId) : ILlmProviderClient
    {
        public LlmRequest CreateRequest(
            string modelId,
            string role,
            string systemPrompt,
            string userPrompt) =>
            LlmRequestFactory.Create(ProviderId, modelId, role, systemPrompt, userPrompt);
    }
}
