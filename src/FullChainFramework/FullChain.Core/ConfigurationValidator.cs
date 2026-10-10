using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using FullChain.Contracts;

namespace FullChain.Core;

public sealed record ConfigValidationResult(
    bool IsValid,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings
);

public static class ConfigurationValidator
{
    private static readonly string[] SecretIndicatingTokens =
    [
        "sk-", "AIzaSy", "key-", "secret", "bearer"
    ];

    public static ConfigValidationResult ValidateJson(string jsonContent, string basePath = ".")
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        // 1. Strict check: reject embedded secrets in raw JSON
        using var doc = JsonDocument.Parse(jsonContent);
        InspectElementForSecrets(doc.RootElement, "$", errors);

        FullChainConfiguration? config;
        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            config = JsonSerializer.Deserialize<FullChainConfiguration>(jsonContent, options);
        }
        catch (Exception ex)
        {
            errors.Add($"JSON deserialization failed: {ex.Message}");
            return new ConfigValidationResult(false, errors, warnings);
        }

        if (config == null)
        {
            errors.Add("Configuration could not be loaded (null).");
            return new ConfigValidationResult(false, errors, warnings);
        }

        // 2. Validate paths
        var manifestPath = ResolvePath(basePath, config.CohortManifestPath);
        if (!File.Exists(manifestPath))
        {
            errors.Add($"Cohort manifest file does not exist: {manifestPath}");
        }

        var toolLockPath = ResolvePath(basePath, config.ToolLockPath);
        if (!File.Exists(toolLockPath))
        {
            errors.Add($"Tool lock file does not exist: {toolLockPath}");
        }

        var datasetRoot = ResolvePath(basePath, config.DatasetProjectsRoot);
        if (!Directory.Exists(datasetRoot))
        {
            warnings.Add($"Dataset projects root not found at: {datasetRoot}");
        }

        // 3. Validate Repetitions
        if (config.Repetitions == null || config.Repetitions.Count == 0)
        {
            errors.Add("Repetitions must not be empty. Official requires [42, 43, 44].");
        }

        // 4. Validate Budget
        if (config.Budget.HardWallClockSeconds <= 0)
        {
            errors.Add("Budget.HardWallClockSeconds must be greater than 0.");
        }
        if (config.Budget.FullChain.MaxRepairRounds > 2)
        {
            errors.Add("Frozen protocol allows at most 2 repair rounds.");
        }
        if (config.Budget.FullChain.MaxCumulativeTokens <= 0 || config.Budget.FullChain.MaxCumulativeTokens > 128000)
        {
            errors.Add("FullChain cumulative token cap must be between 1 and 128000.");
        }

        // 5. Validate LLM settings
        if (string.IsNullOrWhiteSpace(config.Llm.Endpoint))
        {
            errors.Add("LLM endpoint must be specified.");
        }
        if (string.IsNullOrWhiteSpace(config.Llm.ModelId))
        {
            errors.Add("LLM ModelId must be specified.");
        }

        return new ConfigValidationResult(errors.Count == 0, errors, warnings);
    }

    public static ConfigValidationResult ValidateConfiguration(FullChainConfiguration config, string basePath = ".")
    {
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        return ValidateJson(json, basePath);
    }

    private static void InspectElementForSecrets(JsonElement element, string path, List<string> errors)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    var propName = prop.Name.ToLowerInvariant();
                    if (propName.Contains("apikey") || propName.Contains("secret") || propName.Contains("token") || propName.Contains("password"))
                    {
                        // Allowed properties are references to env vars or file paths
                        if (propName.EndsWith("environmentvariable") || propName.EndsWith("filepath") || propName.EndsWith("path"))
                        {
                            continue;
                        }

                        if (prop.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(prop.Value.GetString()))
                        {
                            errors.Add($"Security violation: Raw secret embedded in JSON at {path}.{prop.Name}. Use ApiKeyEnvironmentVariable or ApiKeyFilePath.");
                        }
                    }

                    InspectElementForSecrets(prop.Value, $"{path}.{prop.Name}", errors);
                }
                break;

            case JsonValueKind.Array:
                int idx = 0;
                foreach (var item in element.EnumerateArray())
                {
                    InspectElementForSecrets(item, $"{path}[{idx++}]", errors);
                }
                break;

            case JsonValueKind.String:
                var val = element.GetString();
                if (!string.IsNullOrEmpty(val))
                {
                    foreach (var prefix in SecretIndicatingTokens)
                    {
                        if (val.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            errors.Add($"Security violation: Sensitive token prefix detected at {path}. Embedding secrets in config is strictly forbidden.");
                            break;
                        }
                    }
                }
                break;
        }
    }

    private static string ResolvePath(string basePath, string targetPath)
    {
        if (Path.IsPathRooted(targetPath)) return targetPath;
        return Path.GetFullPath(Path.Combine(basePath, targetPath));
    }
}
