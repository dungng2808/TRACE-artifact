using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using FullChain.Contracts;

namespace FullChain.Wpf;

public sealed record FullChainOperatorSettings
{
    public string Endpoint { get; init; } = "https://openrouter.ai/api/v1/chat/completions";
    public string ModelId { get; init; } = "google/gemini-2.5-flash";
    public string ApiKey { get; init; } = string.Empty;
    public string JavaHome { get; init; } = string.Empty;
    public string MavenHome { get; init; } = string.Empty;
    public string EvosuiteJar { get; init; } = string.Empty;
    public string EvosuiteRuntimeJar { get; init; } = string.Empty;
    public string TargetManifestPath { get; init; } = "experiments/full-chain/manifests/full-chain-rq1-150.v1.json";
    public string SelectedSubjectId { get; init; } = "PT-O-001";
    public int SelectedRepetition { get; init; } = 42;
}

public static class LocalSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static FullChainOperatorSettings LoadSettings(string repoRoot)
    {
        var localConfigPath = Path.Combine(repoRoot, "experiments", "full-chain", "configs", "full-chain.local.json");
        var exampleConfigPath = Path.Combine(repoRoot, "experiments", "full-chain", "configs", "full-chain.example.json");
        var localKeyPath = Path.Combine(repoRoot, "experiments", "full-chain", "configs", "local-key.txt");

        string endpoint = "https://openrouter.ai/api/v1/chat/completions";
        string modelId = "google/gemini-2.5-flash";
        string apiKey = string.Empty;

        if (File.Exists(localKeyPath))
        {
            apiKey = File.ReadAllText(localKeyPath).Trim();
        }
        if (string.IsNullOrEmpty(apiKey))
        {
            apiKey = Environment.GetEnvironmentVariable("FULLCHAIN_LLM_KEY")
                     ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                     ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                     ?? string.Empty;
        }
        if (string.IsNullOrEmpty(apiKey))
        {
            var rq1KeyPath = Path.Combine(repoRoot, "src", "Rq1Experiment", "Rq1Experiment.Wpf", "data", "api-keys", "openrouter.txt");
            if (File.Exists(rq1KeyPath))
            {
                apiKey = File.ReadAllText(rq1KeyPath).Trim();
            }
        }

        string javaHome = Environment.GetEnvironmentVariable("JAVA_HOME") ?? string.Empty;
        string mavenHome = Environment.GetEnvironmentVariable("MAVEN_HOME") ?? string.Empty;
        string evosuiteJar = string.Empty;
        string evosuiteRuntimeJar = string.Empty;
        string manifestPath = "experiments/full-chain/manifests/full-chain-rq1-150.v1.json";

        string configToRead = File.Exists(localConfigPath) ? localConfigPath : exampleConfigPath;

        if (File.Exists(configToRead))
        {
            try
            {
                var json = File.ReadAllText(configToRead);
                var config = JsonSerializer.Deserialize<FullChainConfiguration>(json, JsonOptions);
                if (config != null)
                {
                    if (!string.IsNullOrWhiteSpace(config.Llm.Endpoint)) endpoint = config.Llm.Endpoint;
                    if (!string.IsNullOrWhiteSpace(config.Llm.ModelId)) modelId = config.Llm.ModelId;
                    if (!string.IsNullOrWhiteSpace(config.Tools.JavaHome)) javaHome = config.Tools.JavaHome;
                    if (!string.IsNullOrWhiteSpace(config.Tools.MavenHome)) mavenHome = config.Tools.MavenHome;
                    if (!string.IsNullOrWhiteSpace(config.Tools.EvosuiteJar)) evosuiteJar = config.Tools.EvosuiteJar;
                    if (!string.IsNullOrWhiteSpace(config.Tools.EvosuiteRuntimeJar)) evosuiteRuntimeJar = config.Tools.EvosuiteRuntimeJar;
                    if (!string.IsNullOrWhiteSpace(config.CohortManifestPath))
                    {
                        var resolvedManifest = Path.IsPathRooted(config.CohortManifestPath) ? config.CohortManifestPath : Path.Combine(repoRoot, config.CohortManifestPath);
                        if (File.Exists(resolvedManifest))
                        {
                            manifestPath = config.CohortManifestPath;
                        }
                    }
                }
            }
            catch
            {
                // Fallback to defaults
            }
        }

        // Auto-resolve tool paths if relative
        if (!Path.IsPathRooted(javaHome) && !string.IsNullOrEmpty(javaHome))
            javaHome = Path.GetFullPath(Path.Combine(repoRoot, javaHome));
        if (!Path.IsPathRooted(mavenHome) && !string.IsNullOrEmpty(mavenHome))
            mavenHome = Path.GetFullPath(Path.Combine(repoRoot, mavenHome));
        if (!Path.IsPathRooted(evosuiteJar) && !string.IsNullOrEmpty(evosuiteJar))
            evosuiteJar = Path.GetFullPath(Path.Combine(repoRoot, evosuiteJar));
        if (!Path.IsPathRooted(evosuiteRuntimeJar) && !string.IsNullOrEmpty(evosuiteRuntimeJar))
            evosuiteRuntimeJar = Path.GetFullPath(Path.Combine(repoRoot, evosuiteRuntimeJar));

        return new FullChainOperatorSettings
        {
            Endpoint = endpoint,
            ModelId = modelId,
            ApiKey = apiKey,
            JavaHome = javaHome,
            MavenHome = mavenHome,
            EvosuiteJar = evosuiteJar,
            EvosuiteRuntimeJar = evosuiteRuntimeJar,
            TargetManifestPath = manifestPath
        };
    }

    public static void SaveSettings(string repoRoot, FullChainOperatorSettings settings)
    {
        var configDir = Path.Combine(repoRoot, "experiments", "full-chain", "configs");
        Directory.CreateDirectory(configDir);

        // Save key to gitignored local-key.txt
        var localKeyPath = Path.Combine(configDir, "local-key.txt");
        if (!string.IsNullOrEmpty(settings.ApiKey))
        {
            File.WriteAllText(localKeyPath, settings.ApiKey.Trim());
        }

        // Save config to gitignored full-chain.local.json without embedded raw secret
        var localConfigPath = Path.Combine(configDir, "full-chain.local.json");
        var config = BuildConfiguration(repoRoot, settings);
        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(localConfigPath, json);
    }

    public static FullChainConfiguration BuildConfiguration(string repoRoot, FullChainOperatorSettings settings)
    {
        var localKeyPath = Path.Combine(repoRoot, "experiments", "full-chain", "configs", "local-key.txt");

        return new FullChainConfiguration(
            SchemaVersion: "full-chain-config-v1",
            ExperimentId: "FULL-CHAIN-OPERATOR",
            Mode: "Pilot",
            CohortManifestPath: settings.TargetManifestPath,
            DatasetProjectsRoot: "datasets/rq1/sf110/java-ready/projects",
            OutputRoot: "experiments/full-chain/runs",
            ToolLockPath: "experiments/full-chain/tool-lock.json",
            Repetitions: new[] { settings.SelectedRepetition, 43, 44 },
            Budget: new BudgetPolicy(
                HardWallClockSeconds: 900,
                FullChain: new FullChainBudgetPolicy(
                    GenerationTimeoutSeconds: 180,
                    RepairTotalTimeoutSeconds: 240,
                    EvaluationTotalTimeoutSeconds: 180,
                    VerificationTotalTimeoutSeconds: 240,
                    OrchestrationTimeoutSeconds: 60,
                    MaxRepairRounds: 2,
                    MaxVerificationPasses: 3,
                    MaxProviderRequests: 13,
                    MaxTransientRetriesPerRequest: 2,
                    MaxCumulativeTokens: 128000
                ),
                EvoSuite: new EvoSuiteBudgetPolicy(
                    SearchTimeoutSeconds: 420,
                    EvaluationTotalTimeoutSeconds: 180,
                    VerificationTotalTimeoutSeconds: 240,
                    OrchestrationTimeoutSeconds: 60
                )
            ),
            Llm: new LlmConfiguration(
                Provider: "openai-compatible",
                Endpoint: settings.Endpoint,
                ModelId: settings.ModelId,
                Temperature: 0.0,
                TimeoutSeconds: 120,
                ApiKeyEnvironmentVariable: "FULLCHAIN_LLM_KEY",
                ApiKeyFilePath: localKeyPath
            ),
            Tools: new ToolConfiguration(
                JavaHome: settings.JavaHome,
                MavenHome: settings.MavenHome,
                EvosuiteJar: settings.EvosuiteJar,
                EvosuiteRuntimeJar: settings.EvosuiteRuntimeJar,
                JacocoVersion: "0.8.15",
                PitVersion: "1.25.3"
            )
        );
    }

    public static string Redact(string? input, string? apiKey)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;

        var text = input;

        if (!string.IsNullOrEmpty(apiKey) && apiKey.Length >= 4)
        {
            text = text.Replace(apiKey, "[REDACTED_API_KEY]");
        }

        text = Regex.Replace(text, @"Bearer\s+[A-Za-z0-9_\-\.]{8,}", "Bearer [REDACTED_BEARER_TOKEN]");
        text = Regex.Replace(text, @"(sk-[A-Za-z0-9_\-]{8,})", "[REDACTED_API_KEY]");
        text = Regex.Replace(text, @"(AIzaSy[A-Za-z0-9_\-]{20,})", "[REDACTED_GOOGLE_API_KEY]");

        return text;
    }

    public static IDisposable AcquireCampaignLock(string repoRoot, string holderName = "WPF_Operator")
    {
        var runsDir = Path.Combine(repoRoot, "experiments", "full-chain", "runs");
        Directory.CreateDirectory(runsDir);
        var lockFilePath = Path.Combine(runsDir, "campaign.lock");

        try
        {
            var stream = new FileStream(
                lockFilePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.DeleteOnClose);

            using var writer = new StreamWriter(stream, leaveOpen: true);
            writer.WriteLine($"Locked by: {holderName} (PID: {Environment.ProcessId}) at {DateTime.UtcNow:o}");
            writer.Flush();

            return new CampaignLockToken(stream, lockFilePath);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"Another campaign or operator instance holds the campaign lock ({lockFilePath}). Concurrent runs are prevented.", ex);
        }
    }

    private sealed class CampaignLockToken : IDisposable
    {
        private FileStream? _stream;
        private readonly string _path;

        public CampaignLockToken(FileStream stream, string path)
        {
            _stream = stream;
            _path = path;
        }

        public void Dispose()
        {
            if (_stream != null)
            {
                try
                {
                    _stream.Dispose();
                    if (File.Exists(_path))
                    {
                        File.Delete(_path);
                    }
                }
                catch
                {
                    // Best effort cleanup
                }
                finally
                {
                    _stream = null;
                }
            }
        }
    }
}
