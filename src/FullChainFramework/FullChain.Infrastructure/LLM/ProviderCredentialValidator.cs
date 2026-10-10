using System;
using FullChain.Contracts;

namespace FullChain.Infrastructure.LLM;

/// <summary>
/// Performs non-secret credential shape checks at composition time. It never
/// returns or logs the supplied credential.
/// </summary>
public static class ProviderCredentialValidator
{
    public static void EnsureUsable(LlmConfiguration configuration, string? apiKey)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (string.Equals(configuration.Provider, "agy", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ProviderFailureException(
                "No LLM API key was resolved. Configure an in-memory key, the configured environment variable, or the configured key file.");
        }

        if (string.Equals(configuration.Provider, "openrouter", StringComparison.OrdinalIgnoreCase)
            && !apiKey.StartsWith("sk-or-", StringComparison.Ordinal))
        {
            throw new ProviderFailureException(
                "The configured OpenRouter credential has an unsupported format. Configure an OpenRouter key beginning with 'sk-or-'; the credential value was not recorded.");
        }
    }
}
