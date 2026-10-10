using System;
using System.IO;
using FullChain.Contracts;

namespace FullChain.Infrastructure.LLM;

public interface IApiKeyResolver
{
    string Resolve(LlmConfiguration configuration, string? inMemoryApiKey = null);
}

/// <summary>
/// Resolves secrets without serializing or logging them. The resolver has one
/// deterministic precedence order so every RQ client uses the same credential.
/// </summary>
public sealed class ApiKeyResolver : IApiKeyResolver
{
    private readonly string _baseDirectory;

    public ApiKeyResolver(string? baseDirectory = null)
    {
        _baseDirectory = Path.GetFullPath(baseDirectory ?? Directory.GetCurrentDirectory());
    }

    public string Resolve(LlmConfiguration configuration, string? inMemoryApiKey = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (!string.IsNullOrWhiteSpace(inMemoryApiKey))
        {
            return inMemoryApiKey.Trim();
        }

        if (!string.IsNullOrWhiteSpace(configuration.ApiKeyEnvironmentVariable))
        {
            var environmentValue = Environment.GetEnvironmentVariable(configuration.ApiKeyEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(environmentValue))
            {
                return environmentValue.Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(configuration.ApiKeyFilePath))
        {
            return string.Empty;
        }

        var candidatePath = Path.IsPathRooted(configuration.ApiKeyFilePath)
            ? configuration.ApiKeyFilePath
            : Path.Combine(_baseDirectory, configuration.ApiKeyFilePath);

        if (!File.Exists(candidatePath))
        {
            return string.Empty;
        }

        return File.ReadAllText(candidatePath).Trim();
    }
}
