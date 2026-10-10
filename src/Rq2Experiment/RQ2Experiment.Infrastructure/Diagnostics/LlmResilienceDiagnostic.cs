using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using RQ2Experiment.Core.Domain;
using RQ2Experiment.Infrastructure.LLM;
using RQ2Experiment.Infrastructure.OpenAI;

namespace RQ2Experiment.Infrastructure.Diagnostics;

internal static class LlmResilienceDiagnostic
{
    public static async Task<string> RunAsync(CancellationToken cancellationToken)
    {
        await Verify503RetriesSameKeyAsync(cancellationToken);
        await VerifyKeyFailoverAsync(HttpStatusCode.TooManyRequests, cancellationToken);
        await VerifyKeyFailoverAsync(HttpStatusCode.Unauthorized, cancellationToken);
        await VerifyRoundRobinAcrossRunsAsync(cancellationToken);
        await VerifyRequestPacingAsync(cancellationToken);
        await VerifyInvalidOutputUsageAccountingAsync(cancellationToken);
        return "LLM resilience: sticky round-robin, 503 same-key retry, 429 cooldown failover, " +
               "401 disable failover, global request pacing, failed-output usage accounting, and secret isolation: OK";
    }

    private static async Task Verify503RetriesSameKeyAsync(CancellationToken cancellationToken)
    {
        var authorizations = new List<string>();
        var (baseUrl, serverTask) = StartServerAsync(
            2,
            (attempt, authorization) =>
            {
                authorizations.Add(authorization);
                return attempt == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK;
            },
            cancellationToken);
        var artifacts = DiagnosticArtifactDirectory("503");
        using var client = CreateClient(["diagnostic-key-a"], baseUrl);
        var response = await GenerateAsync(client, artifacts, cancellationToken);
        await serverTask;

        if (response.ApiCalls != 2 || response.RetryCount != 1 || response.KeyFailoverCount != 0 ||
            authorizations.Count != 2 || authorizations.Any(value => value != "Bearer diagnostic-key-a"))
        {
            throw new InvalidOperationException("503 resilience diagnostic did not retry on the same key.");
        }

        EnsureSecretsAbsent(artifacts, "diagnostic-key-a");
    }

    private static async Task VerifyKeyFailoverAsync(
        HttpStatusCode firstStatus,
        CancellationToken cancellationToken)
    {
        var authorizations = new List<string>();
        var (baseUrl, serverTask) = StartServerAsync(
            2,
            (attempt, authorization) =>
            {
                authorizations.Add(authorization);
                return attempt == 1 ? firstStatus : HttpStatusCode.OK;
            },
            cancellationToken);
        var artifacts = DiagnosticArtifactDirectory(((int)firstStatus).ToString());
        using var client = CreateClient(["diagnostic-key-a", "diagnostic-key-b"], baseUrl);
        var response = await GenerateAsync(client, artifacts, cancellationToken);
        await serverTask;

        if (response.ApiCalls != 2 || response.KeyFailoverCount != 1 ||
            authorizations.Count != 2 ||
            authorizations[0] != "Bearer diagnostic-key-a" ||
            authorizations[1] != "Bearer diagnostic-key-b")
        {
            throw new InvalidOperationException($"{(int)firstStatus} resilience diagnostic did not fail over to the next key.");
        }

        EnsureSecretsAbsent(artifacts, "diagnostic-key-a", "diagnostic-key-b");
    }

    private static async Task VerifyRoundRobinAcrossRunsAsync(CancellationToken cancellationToken)
    {
        var authorizations = new List<string>();
        var (baseUrl, serverTask) = StartServerAsync(
            2,
            (_, authorization) =>
            {
                authorizations.Add(authorization);
                return HttpStatusCode.OK;
            },
            cancellationToken);
        var root = DiagnosticArtifactDirectory("round-robin");
        using var client = CreateClient(["diagnostic-key-a", "diagnostic-key-b"], baseUrl);
        await GenerateAsync(client, Path.Combine(root, "run-1"), cancellationToken);
        await GenerateAsync(client, Path.Combine(root, "run-2"), cancellationToken);
        await serverTask;

        if (authorizations.Count != 2 ||
            authorizations[0] != "Bearer diagnostic-key-a" ||
            authorizations[1] != "Bearer diagnostic-key-b")
        {
            throw new InvalidOperationException("Run-level round-robin did not assign consecutive keys.");
        }

        EnsureSecretsAbsent(root, "diagnostic-key-a", "diagnostic-key-b");
    }

    private static async Task VerifyInvalidOutputUsageAccountingAsync(CancellationToken cancellationToken)
    {
        var (baseUrl, serverTask) = StartServerAsync(
            1,
            (_, _) => HttpStatusCode.OK,
            cancellationToken,
            invalidOutput: true);
        var artifacts = DiagnosticArtifactDirectory("invalid-output-accounting");
        using var client = CreateClient(["diagnostic-key-a"], baseUrl);
        try
        {
            await GenerateAsync(client, artifacts, cancellationToken);
            throw new InvalidOperationException("Invalid model output was unexpectedly accepted.");
        }
        catch (ExperimentExecutionException exception) when (
            exception.Category == RunFailureCategory.InvalidModelOutput)
        {
            if (exception.InputTokens != 10 ||
                exception.OutputTokens != 5 ||
                exception.TotalTokens != 15 ||
                exception.ApiCalls != 1 ||
                exception.UnreportedUsageResponses != 0)
            {
                throw new InvalidOperationException(
                    "Invalid-output accounting did not preserve provider-reported usage.");
            }
        }

        await serverTask;
        var accountingPath = Directory.EnumerateFiles(artifacts, "*-accounting.json").Single();
        var accounting = await File.ReadAllTextAsync(accountingPath, cancellationToken);
        if (!accounting.Contains("\"accepted\": false", StringComparison.Ordinal) ||
            !accounting.Contains("\"totalTokens\": 15", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Failed-response accounting artifact is incomplete.");
        }

        EnsureSecretsAbsent(artifacts, "diagnostic-key-a");
    }

    private static async Task VerifyRequestPacingAsync(CancellationToken cancellationToken)
    {
        var observedAt = new List<DateTimeOffset>();
        var (baseUrl, serverTask) = StartServerAsync(
            2,
            (_, _) =>
            {
                observedAt.Add(DateTimeOffset.UtcNow);
                return HttpStatusCode.OK;
            },
            cancellationToken);
        var root = DiagnosticArtifactDirectory("request-pacing");
        using var client = CreateClient(
            ["diagnostic-key-a"],
            baseUrl,
            minimumRequestInterval: TimeSpan.FromMilliseconds(150));
        await Task.WhenAll(
            GenerateAsync(client, Path.Combine(root, "run-1"), cancellationToken),
            GenerateAsync(client, Path.Combine(root, "run-2"), cancellationToken));
        await serverTask;

        if (observedAt.Count != 2 || observedAt[1] - observedAt[0] < TimeSpan.FromMilliseconds(100))
        {
            throw new InvalidOperationException("Global request pacing did not separate concurrent LLM requests.");
        }
    }

    private static ResilientLlmClient CreateClient(
        IReadOnlyList<string> keys,
        string baseUrl,
        TimeSpan? minimumRequestInterval = null) => new(
        keys,
        baseUrl,
        LlmApiProtocol.ChatCompletions,
        requiresApiKey: true,
        useJsonMode: true,
        new LlmResilienceOptions(
            MaxTransientRetries: 2,
            MaxRateLimitWaits: 0,
            BaseDelay: TimeSpan.Zero,
            MaximumDelay: TimeSpan.Zero,
            DefaultRateLimitDelay: TimeSpan.Zero,
            MinimumRequestInterval: minimumRequestInterval ?? TimeSpan.Zero));

    private static Task<LlmRepairResponse> GenerateAsync(
        ResilientLlmClient client,
        string artifacts,
        CancellationToken cancellationToken) => client.GenerateRepairAsync(
        "diagnostic-model",
        "medium",
        "Return a repair.",
        "Repair the test.",
        artifacts,
        1,
        cancellationToken);

    private static (string BaseUrl, Task ServerTask) StartServerAsync(
        int requestCount,
        Func<int, string, HttpStatusCode> statusSelector,
        CancellationToken cancellationToken,
        bool invalidOutput = false)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            try
            {
                for (var attempt = 1; attempt <= requestCount; attempt++)
                {
                    using var connection = await listener.AcceptTcpClientAsync(cancellationToken);
                    await using var stream = connection.GetStream();
                    var request = await InfrastructureDiagnostics.ReadHttpRequestAsync(stream, cancellationToken);
                    var status = statusSelector(attempt, request.Authorization);
                    await WriteResponseAsync(stream, status, cancellationToken, invalidOutput);
                }
            }
            finally
            {
                listener.Stop();
            }
        }, cancellationToken);
        return ($"http://127.0.0.1:{port}/v1/", serverTask);
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        HttpStatusCode status,
        CancellationToken cancellationToken,
        bool invalidOutput)
    {
        object body = status == HttpStatusCode.OK
            ? new
            {
                id = "chatcmpl_resilience",
                model = "diagnostic-model",
                choices = new[]
                {
                    new
                    {
                        message = new
                        {
                            role = "assistant",
                            content = invalidOutput
                                ? "{\"replacementSource\":\"\",\"explanation\":\"missing source\"}"
                                : "{\"replacementSource\":\"@Test void repaired() {}\",\"explanation\":\"fixed\"}"
                        }
                    }
                },
                usage = new { prompt_tokens = 10, completion_tokens = 5 }
            }
            : new { error = new { code = (int)status, message = "diagnostic failure" } };
        var json = JsonSerializer.Serialize(body);
        var payload = Encoding.UTF8.GetBytes(json);
        var reason = status switch
        {
            HttpStatusCode.TooManyRequests => "Too Many Requests",
            HttpStatusCode.Unauthorized => "Unauthorized",
            HttpStatusCode.ServiceUnavailable => "Service Unavailable",
            _ => "OK"
        };
        var retryHeader = status == HttpStatusCode.TooManyRequests ? "Retry-After: 0\r\n" : string.Empty;
        var headers = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {(int)status} {reason}\r\nContent-Type: application/json\r\n" +
            retryHeader +
            $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(headers, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
    }

    private static string DiagnosticArtifactDirectory(string scenario) => Path.Combine(
        Environment.CurrentDirectory,
        "artifacts",
        "diagnostics-resilience",
        scenario,
        Guid.NewGuid().ToString("N"));

    private static void EnsureSecretsAbsent(string artifactDirectory, params string[] secrets)
    {
        foreach (var file in Directory.EnumerateFiles(artifactDirectory, "*", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(file);
            if (secrets.Any(secret => content.Contains(secret, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException($"API secret leaked into resilience artifact {file}.");
            }
        }
    }
}
