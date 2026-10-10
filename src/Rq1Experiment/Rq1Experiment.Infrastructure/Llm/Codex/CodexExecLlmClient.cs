using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rq1Experiment.Core.Abstractions;

namespace Rq1Experiment.Infrastructure.Llm.Codex;

/// <summary>
/// Reproducibility-sensitive settings for a non-interactive Codex CLI invocation.
/// Defaults deliberately isolate the run from repository/user instructions and
/// disable stateful or online capabilities that would contaminate an experiment.
/// </summary>
public sealed record CodexExecLlmClientOptions
{
    public string ExecutablePath { get; init; } = "codex";
    public string ExpectedCliVersion { get; init; } = "codex-cli 0.151.0-alpha.7.1";
    public bool EnforceExpectedCliVersion { get; init; } = true;
    public string? WorkingDirectory { get; init; }
    public string SandboxMode { get; init; } = "read-only";
    public string ReasoningEffort { get; init; } = "low";
    public string ServiceTier { get; init; } = "default";
    public string WebSearchMode { get; init; } = "disabled";
    public string ForcedLoginMethod { get; init; } = "chatgpt";
    public bool StrictConfig { get; init; } = true;
    public bool Ephemeral { get; init; } = true;
    public bool IgnoreUserConfig { get; init; } = true;
    public bool IgnoreRules { get; init; } = true;
    public bool SkipGitRepositoryCheck { get; init; } = true;
    public bool DisableMemories { get; init; } = true;
    public IReadOnlyList<string> AdditionalArguments { get; init; } = [];
}

/// <summary>
/// Executes one isolated Codex CLI turn and adapts its structured result to the
/// experiment's existing <see cref="IFailoverLlmClient"/> contract.
///
/// This class intentionally does not rotate API keys: Codex CLI owns ChatGPT/API
/// authentication. Callers retain the existing semantic-call and timeout rules.
/// </summary>
public sealed class CodexExecLlmClient : IFailoverLlmClient
{
    public const string HarnessVersion = "rq1-codex-exec-v1";

    private static readonly JsonSerializerOptions RawJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly CodexExecLlmClientOptions _options;
    private readonly object _versionProbeLock = new();
    private Task<string?>? _versionProbeTask;

    public CodexExecLlmClient(CodexExecLlmClientOptions? options = null)
    {
        _options = options ?? new CodexExecLlmClientOptions();
        ValidateOptions(_options);
    }

    public async Task<LlmCallResult> SendAsync(
        LlmRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ModelId))
            return Failure(request, "CODEX_MODEL_ID_MISSING", attempts: 0);

        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var fullPrompt = BuildPrompt(request);
        var fullPromptSha256 = Sha256(fullPrompt);

        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "rq1-codex-exec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);

        var schemaPath = Path.Combine(temporaryDirectory, "output-schema.json");
        var finalResponsePath = Path.Combine(temporaryDirectory, "final-response.json");
        Process? process = null;

        try
        {
            var actualCliVersion = await GetCliVersionAsync().WaitAsync(cancellationToken);
            if (_options.EnforceExpectedCliVersion
                && !string.Equals(actualCliVersion, _options.ExpectedCliVersion, StringComparison.Ordinal))
            {
                return Failure(request, "CODEX_CLI_VERSION_MISMATCH", attempts: 0,
                    rawResponse: SerializePreflightFailure(
                        request, "Codex CLI version did not match the frozen experiment profile.",
                        actualCliVersion, startedAt, stopwatch.Elapsed, fullPromptSha256));
            }

            if (request.RequireJsonObject)
            {
                if (string.IsNullOrWhiteSpace(request.ResponseJsonSchema))
                    return Failure(request, "CODEX_OUTPUT_SCHEMA_MISSING", attempts: 0);

                try
                {
                    using var _ = JsonDocument.Parse(request.ResponseJsonSchema);
                }
                catch (JsonException exception)
                {
                    return Failure(request, "CODEX_OUTPUT_SCHEMA_INVALID", attempts: 0,
                        rawResponse: SerializePreflightFailure(
                            request, exception.Message, actualCliVersion,
                            startedAt, stopwatch.Elapsed, fullPromptSha256));
                }

                await File.WriteAllTextAsync(
                    schemaPath,
                    request.ResponseJsonSchema,
                    Encoding.UTF8,
                    cancellationToken);
            }

            var startInfo = CreateStartInfo(request, temporaryDirectory, schemaPath, finalResponsePath);
            process = new Process { StartInfo = startInfo };
            try
            {
                if (!process.Start())
                    return Failure(request, "CODEX_CLI_START_FAILED", attempts: 0);
            }
            catch (Exception exception) when (exception is Win32Exception
                                              or InvalidOperationException
                                              or IOException)
            {
                return Failure(request, "CODEX_CLI_START_FAILED", attempts: 0,
                    rawResponse: SerializePreflightFailure(
                        request, exception.Message, actualCliVersion,
                        startedAt, stopwatch.Elapsed, fullPromptSha256));
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            try
            {
                await process.StandardInput.WriteAsync(fullPrompt.AsMemory(), cancellationToken);
                await process.StandardInput.FlushAsync(cancellationToken);
                process.StandardInput.Close();
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                await DrainAfterCancellationAsync(stdoutTask, stderrTask);
                throw;
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            var parsed = CodexExecJsonlParser.Parse(stdout);
            var finalResponse = await ReadFinalResponseAsync(finalResponsePath, parsed.LastAgentMessage);
            var rawResponse = SerializeRawResponse(
                request,
                process.ExitCode,
                parsed,
                stderr,
                actualCliVersion,
                startedAt,
                DateTimeOffset.UtcNow,
                stopwatch.Elapsed,
                fullPromptSha256);

            if (process.ExitCode != 0)
            {
                var failureCode = ClassifyFailure(process.ExitCode, stderr, parsed.ErrorMessage);
                return Failure(
                    request,
                    failureCode,
                    attempts: 1,
                    rawResponse: rawResponse,
                    usage: parsed,
                    isWaitingForApiKey: IsResumableAccountFailure(failureCode));
            }

            if (string.IsNullOrWhiteSpace(finalResponse))
            {
                return Failure(
                    request,
                    "CODEX_EMPTY_FINAL_RESPONSE",
                    attempts: 1,
                    rawResponse: rawResponse,
                    usage: parsed);
            }

            return new LlmCallResult(
                true,
                false,
                request.RequestHash,
                finalResponse,
                parsed.ResponseModelId ?? request.ModelId,
                1,
                1,
                ["CHATGPT_ACCOUNT"],
                null)
            {
                RawResponseText = rawResponse,
                InputTokens = parsed.InputTokens,
                OutputTokens = parsed.OutputTokens,
                TotalTokens = parsed.TotalTokens,
                FinishReason = parsed.TurnCompleted ? "completed" : "process_exit_0",
                HttpStatusCode = null
            };
        }
        finally
        {
            process?.Dispose();
            TryDeleteTemporaryDirectory(temporaryDirectory);
        }
    }

    private ProcessStartInfo CreateStartInfo(
        LlmRequest request,
        string temporaryDirectory,
        string schemaPath,
        string finalResponsePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.ExecutablePath,
            WorkingDirectory = string.IsNullOrWhiteSpace(_options.WorkingDirectory)
                ? temporaryDirectory
                : Path.GetFullPath(_options.WorkingDirectory),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.Environment["NO_COLOR"] = "1";
        RemoveApiCredentialEnvironment(startInfo);

        AddArgument(startInfo, "exec");
        AddArgument(startInfo, "--json");
        if (_options.StrictConfig) AddArgument(startInfo, "--strict-config");
        if (_options.Ephemeral) AddArgument(startInfo, "--ephemeral");
        if (_options.IgnoreUserConfig) AddArgument(startInfo, "--ignore-user-config");
        if (_options.IgnoreRules) AddArgument(startInfo, "--ignore-rules");
        if (_options.SkipGitRepositoryCheck) AddArgument(startInfo, "--skip-git-repo-check");

        AddArgument(startInfo, "--model", request.ModelId);
        AddArgument(startInfo, "--sandbox", _options.SandboxMode);
        AddArgument(startInfo, "--output-last-message", finalResponsePath);
        if (request.RequireJsonObject)
            AddArgument(startInfo, "--output-schema", schemaPath);

        AddConfig(startInfo, "model_reasoning_effort", TomlString(_options.ReasoningEffort));
        AddConfig(startInfo, "service_tier", TomlString(_options.ServiceTier));
        AddConfig(startInfo, "web_search", TomlString(_options.WebSearchMode));
        AddConfig(startInfo, "forced_login_method", TomlString(_options.ForcedLoginMethod));
        if (_options.DisableMemories)
        {
            AddConfig(startInfo, "features.memories", "false");
            AddConfig(startInfo, "memories.use_memories", "false");
            AddConfig(startInfo, "memories.generate_memories", "false");
        }

        foreach (var argument in _options.AdditionalArguments)
            AddArgument(startInfo, argument);

        return startInfo;
    }

    private static string BuildPrompt(LlmRequest request) => $$"""
        CONTROLLED_EXPERIMENT_ROLE: {{request.Role}}

        This is one isolated generation stage in a controlled experiment.
        Do not use shell commands, files, repository inspection, web search, MCP,
        plugins, memories, or any information outside the two supplied prompts.
        Do not repair, retry, critique, or improve an answer after tool feedback.
        Produce exactly one final answer. The final answer must contain only the
        JSON value required by the CLI output schema, without Markdown fences.

        <SYSTEM_PROMPT>
        {{request.SystemPrompt}}
        </SYSTEM_PROMPT>

        <USER_PROMPT>
        {{request.UserPrompt}}
        </USER_PROMPT>
        """;

    private static async Task<string?> ReadFinalResponseAsync(string path, string? fallback)
    {
        try
        {
            if (File.Exists(path))
            {
                var value = await File.ReadAllTextAsync(path);
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The JSONL agent-message event remains an independent fallback.
        }
        return string.IsNullOrWhiteSpace(fallback) ? null : fallback.Trim();
    }

    private static LlmCallResult Failure(
        LlmRequest request,
        string errorCode,
        int attempts,
        string? rawResponse = null,
        CodexExecJsonlResult? usage = null,
        bool isWaitingForApiKey = false) => new(
        false,
        isWaitingForApiKey,
        request.RequestHash,
        null,
        usage?.ResponseModelId ?? request.ModelId,
        attempts > 0 ? 1 : 0,
        attempts,
        attempts > 0 ? ["CHATGPT_ACCOUNT"] : [],
        errorCode)
    {
        RawResponseText = rawResponse,
        InputTokens = usage?.InputTokens,
        OutputTokens = usage?.OutputTokens,
        TotalTokens = usage?.TotalTokens,
        FinishReason = usage?.TurnCompleted == true ? "completed_without_response" : "failed",
        HttpStatusCode = null
    };

    private string SerializeRawResponse(
        LlmRequest request,
        int exitCode,
        CodexExecJsonlResult parsed,
        string stderr,
        string? actualCliVersion,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        TimeSpan duration,
        string fullPromptSha256) => JsonSerializer.Serialize(new
        {
            harnessVersion = HarnessVersion,
            transport = "codex-exec-jsonl",
            fullPromptSha256,
            request.RequestHash,
            requestedModelId = request.ModelId,
            requestedMaxOutputTokens = request.MaxOutputTokens,
            requestedTemperature = request.Temperature,
            responseModelId = parsed.ResponseModelId ?? request.ModelId,
            cliVersion = new
            {
                expected = _options.ExpectedCliVersion,
                actual = actualCliVersion,
                enforced = _options.EnforceExpectedCliVersion
            },
            profile = ProfileEvidence(),
            startedAt,
            completedAt,
            durationSeconds = duration.TotalSeconds,
            exitCode,
            usage = new
            {
                inputTokens = parsed.InputTokens,
                cachedInputTokens = parsed.CachedInputTokens,
                cacheWriteInputTokens = parsed.CacheWriteInputTokens,
                outputTokens = parsed.OutputTokens,
                reasoningOutputTokens = parsed.ReasoningOutputTokens,
                totalTokens = parsed.TotalTokens
            },
            parsed.TurnCompleted,
            parsed.ErrorMessage,
            events = parsed.Events,
            unparsedStdoutLines = parsed.UnparsedLines,
            standardError = stderr
        }, RawJsonOptions);

    private string SerializePreflightFailure(
        LlmRequest request,
        string message,
        string? actualCliVersion,
        DateTimeOffset startedAt,
        TimeSpan duration,
        string fullPromptSha256) =>
        JsonSerializer.Serialize(new
        {
            harnessVersion = HarnessVersion,
            transport = "codex-exec-jsonl",
            fullPromptSha256,
            request.RequestHash,
            requestedModelId = request.ModelId,
            cliVersion = new
            {
                expected = _options.ExpectedCliVersion,
                actual = actualCliVersion,
                enforced = _options.EnforceExpectedCliVersion
            },
            profile = ProfileEvidence(),
            startedAt,
            completedAt = DateTimeOffset.UtcNow,
            durationSeconds = duration.TotalSeconds,
            preflightError = message
        }, RawJsonOptions);

    private object ProfileEvidence() => new
    {
        _options.ReasoningEffort,
        _options.ServiceTier,
        _options.WebSearchMode,
        _options.ForcedLoginMethod,
        _options.DisableMemories,
        _options.SandboxMode,
        _options.StrictConfig,
        _options.Ephemeral,
        _options.IgnoreUserConfig,
        _options.IgnoreRules,
        _options.SkipGitRepositoryCheck,
        workingDirectory = string.IsNullOrWhiteSpace(_options.WorkingDirectory)
            ? "per-call-empty-temp-directory"
            : Path.GetFullPath(_options.WorkingDirectory),
        additionalArguments = _options.AdditionalArguments,
        authenticationSlot = "CHATGPT_ACCOUNT",
        automaticSemanticRetries = false
    };

    private static string ClassifyFailure(int exitCode, string stderr, string? eventError)
    {
        var detail = (stderr + "\n" + eventError).ToLowerInvariant();
        if (detail.Contains("rate limit", StringComparison.Ordinal)
            || detail.Contains("rate_limit", StringComparison.Ordinal))
            return "CODEX_RATE_LIMITED";
        if (detail.Contains("quota", StringComparison.Ordinal)
            || detail.Contains("usage limit", StringComparison.Ordinal))
            return "CODEX_QUOTA_LIMITED";
        if (detail.Contains("login", StringComparison.Ordinal)
            || detail.Contains("not logged in", StringComparison.Ordinal)
            || detail.Contains("sign in", StringComparison.Ordinal)
            || detail.Contains("auth.json", StringComparison.Ordinal)
            || detail.Contains("token expired", StringComparison.Ordinal)
            || detail.Contains("authentication", StringComparison.Ordinal)
            || detail.Contains("unauthorized", StringComparison.Ordinal))
            return "CODEX_AUTH_REQUIRED";
        if (detail.Contains("output schema", StringComparison.Ordinal)
            || detail.Contains("output_schema", StringComparison.Ordinal))
            return "CODEX_OUTPUT_SCHEMA_ERROR";
        return "CODEX_CLI_EXIT_" + exitCode;
    }

    private static bool IsResumableAccountFailure(string errorCode) =>
        errorCode is "CODEX_RATE_LIMITED" or "CODEX_QUOTA_LIMITED" or "CODEX_AUTH_REQUIRED";

    private Task<string?> GetCliVersionAsync()
    {
        lock (_versionProbeLock)
            return _versionProbeTask ??= ProbeCliVersionAsync();
    }

    private async Task<string?> ProbeCliVersionAsync()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.ExecutablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        RemoveApiCredentialEnvironment(startInfo);
        AddArgument(startInfo, "--version");

        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start()) return null;
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var stdout = (await stdoutTask).Trim();
            await stderrTask;
            return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout) ? stdout : null;
        }
        catch (Exception exception) when (exception is Win32Exception
                                          or InvalidOperationException
                                          or IOException)
        {
            return null;
        }
    }

    private static void RemoveApiCredentialEnvironment(ProcessStartInfo startInfo)
    {
        startInfo.Environment.Remove("OPENAI_API_KEY");
        startInfo.Environment.Remove("CODEX_API_KEY");
        startInfo.Environment.Remove("CODEX_ACCESS_TOKEN");
    }

    private static void AddConfig(ProcessStartInfo startInfo, string key, string value) =>
        AddArgument(startInfo, "--config", key + "=" + value);

    private static void AddArgument(ProcessStartInfo startInfo, params string[] arguments)
    {
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
    }

    private static string TomlString(string value) =>
        '"' + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + '"';

    private static string Sha256(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or NotSupportedException
                                          or Win32Exception)
        {
            // Best-effort cleanup; cancellation remains the authoritative outcome.
        }
    }

    private static async Task DrainAfterCancellationAsync(Task<string> stdout, Task<string> stderr)
    {
        try
        {
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception exception) when (exception is TimeoutException
                                          or IOException
                                          or ObjectDisposedException)
        {
            // The process has already been terminated and the caller is cancelling.
        }
    }

    private static void TryDeleteTemporaryDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A stale per-call temp folder is safer than masking the experiment result.
        }
    }

    private static void ValidateOptions(CodexExecLlmClientOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ExecutablePath))
            throw new ArgumentException("Codex executable path is required.", nameof(options));
        if (options.SandboxMode is not ("read-only" or "workspace-write" or "danger-full-access"))
            throw new ArgumentException("Unsupported Codex sandbox mode.", nameof(options));
        if (!string.IsNullOrWhiteSpace(options.WorkingDirectory)
            && !Directory.Exists(options.WorkingDirectory))
            throw new DirectoryNotFoundException(options.WorkingDirectory);
        foreach (var value in new[]
                 {
                     options.ReasoningEffort,
                     options.ServiceTier,
                     options.WebSearchMode,
                     options.ForcedLoginMethod
                 })
        {
            if (string.IsNullOrWhiteSpace(value)
                || value.Any(character => !(char.IsLetterOrDigit(character)
                                             || character is '-' or '_')))
                throw new ArgumentException("Codex profile values must be simple identifiers.", nameof(options));
        }
    }
}

internal sealed class CodexExecJsonlResult
{
    public List<JsonElement> Events { get; } = [];
    public List<string> UnparsedLines { get; } = [];
    public string? LastAgentMessage { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ResponseModelId { get; set; }
    public int? InputTokens { get; set; }
    public int? CachedInputTokens { get; set; }
    public int? CacheWriteInputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int? ReasoningOutputTokens { get; set; }
    public int? TotalTokens { get; set; }
    public bool TurnCompleted { get; set; }
}

internal static class CodexExecJsonlParser
{
    public static CodexExecJsonlResult Parse(string jsonLines)
    {
        var result = new CodexExecJsonlResult();
        long input = 0, cachedInput = 0, cacheWriteInput = 0, output = 0, reasoningOutput = 0;
        var sawInput = false;
        var sawCachedInput = false;
        var sawCacheWriteInput = false;
        var sawOutput = false;
        var sawReasoningOutput = false;

        using var reader = new StringReader(jsonLines);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonElement root;
            try
            {
                using var document = JsonDocument.Parse(line);
                root = document.RootElement.Clone();
                result.Events.Add(root);
            }
            catch (JsonException)
            {
                result.UnparsedLines.Add(line);
                continue;
            }

            var type = String(root, "type");
            if (type == "item.completed"
                && root.TryGetProperty("item", out var item)
                && String(item, "type") == "agent_message")
            {
                result.LastAgentMessage = String(item, "text")
                                          ?? String(item, "content")
                                          ?? result.LastAgentMessage;
            }

            if (type == "turn.completed")
            {
                result.TurnCompleted = true;
                if (root.TryGetProperty("usage", out var usage))
                {
                    Add(usage, "input_tokens", ref input, ref sawInput);
                    Add(usage, "cached_input_tokens", ref cachedInput, ref sawCachedInput);
                    Add(usage, "cache_write_input_tokens", ref cacheWriteInput, ref sawCacheWriteInput);
                    Add(usage, "output_tokens", ref output, ref sawOutput);
                    Add(usage, "reasoning_output_tokens", ref reasoningOutput, ref sawReasoningOutput);
                }
            }

            if (type is "error" or "turn.failed")
                result.ErrorMessage = ExtractError(root) ?? result.ErrorMessage;

            result.ResponseModelId ??= String(root, "model");
        }

        result.InputTokens = ToInt(input, sawInput);
        result.CachedInputTokens = ToInt(cachedInput, sawCachedInput);
        result.CacheWriteInputTokens = ToInt(cacheWriteInput, sawCacheWriteInput);
        result.OutputTokens = ToInt(output, sawOutput);
        result.ReasoningOutputTokens = ToInt(reasoningOutput, sawReasoningOutput);
        result.TotalTokens = sawInput && sawOutput ? ToInt(input + output, true) : null;
        return result;
    }

    private static void Add(JsonElement root, string name, ref long total, ref bool seen)
    {
        if (!root.TryGetProperty(name, out var value) || !value.TryGetInt64(out var number)) return;
        total += number;
        seen = true;
    }

    private static int? ToInt(long value, bool seen) => !seen
        ? null
        : value > int.MaxValue ? int.MaxValue
        : value < int.MinValue ? int.MinValue
        : (int)value;

    private static string? ExtractError(JsonElement root)
    {
        if (String(root, "message") is { Length: > 0 } message) return message;
        if (!root.TryGetProperty("error", out var error)) return null;
        return error.ValueKind == JsonValueKind.String
            ? error.GetString()
            : String(error, "message") ?? error.ToString();
    }

    private static string? String(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
