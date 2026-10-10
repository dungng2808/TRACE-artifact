using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using RQ2Experiment.Core.Domain;
using RQ2Experiment.Infrastructure.Execution;
using RQ2Experiment.Infrastructure.LLM;
using RQ2Experiment.Infrastructure.OpenAI;

namespace RQ2Experiment.Infrastructure.Diagnostics;

public static class InfrastructureDiagnostics
{
    public static async Task<IReadOnlyList<string>> RunAsync(CancellationToken cancellationToken)
    {
        var messages = new List<string>();
        messages.Add(await CoordinatorAcceptanceDiagnostic.RunAsync(cancellationToken));
        messages.Add(SchemaMigrationDiagnostic.Run());
        messages.Add(TreatmentIsolationDiagnostic.Run());
        messages.Add(await ExperimentalDesignDiagnostic.RunAsync(cancellationToken));
        messages.Add(RepairPayloadParserDiagnostic.Run());
        messages.Add(MeasurementClassificationDiagnostic.Run());

        const string javaSource = """
            package example;

            class SampleTest {
                @org.junit.jupiter.api.Test
                void shouldWork() {
                    var text = "{not a brace}";
                    // }
                    assert true;
                }
            }
            """;
        var method = JavaMethodEditor.ExtractMethod(javaSource, "shouldWork");
        if (!method.Contains("@org.junit.jupiter.api.Test", StringComparison.Ordinal) ||
            !method.Contains("assert true", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Java method extraction diagnostic failed.");
        }

        var replaced = JavaMethodEditor.ReplaceMethod(
            javaSource,
            "shouldWork",
            "@org.junit.jupiter.api.Test\n    void shouldWork() {\n        assert false;\n    }");
        if (!replaced.Contains("assert false", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Java method replacement diagnostic failed.");
        }

        const string baselineTestClass = """
            import org.junit.Test;
            import static org.junit.Assert.assertEquals;
            class ConformanceTest {
                @Test
                public void focal() { assertEquals(1, 2); }
                @Test
                public void existing() { assertEquals("expected", "expected"); }
            }
            """;
        const string corruptedTestClass = """
            import org.junit.Test;
            import static org.junit.Assert.assertEquals;
            class ConformanceTest {
                @Test
                public void focal() { assertEquals(1, 1); }
                @Test
                public void existing() { assertEquals("expected", new Object()); }
            }
            """;
        var conformance = JavaTestClassConformance.Analyze(
            baselineTestClass,
            corruptedTestClass,
            "focal");
        if (conformance.IsConformant ||
            !conformance.ModifiedTests.Contains("existing", StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Full-class test preservation diagnostic failed.");
        }

        messages.Add("Java method editing and full-class test preservation: OK");

        var maven = await ProcessRunner.RunAsync(
            OperatingSystem.IsWindows() ? "mvn.cmd" : "mvn",
            ["-version"],
            Environment.CurrentDirectory,
            TimeSpan.FromMinutes(1),
            cancellationToken);
        if (!maven.Succeeded || !maven.CombinedOutput.Contains("Apache Maven", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Maven process diagnostic failed with exit code {maven.ExitCode}: {maven.CombinedOutput}");
        }

        messages.Add("Maven process execution: OK");

        var javaPipelineMessage = await RunJavaPipelineDiagnosticAsync(cancellationToken);
        messages.Add(javaPipelineMessage);

        var llmMessage = await RunLlmProtocolDiagnosticAsync(cancellationToken);
        messages.Add(llmMessage);
        messages.Add(await LlmResilienceDiagnostic.RunAsync(cancellationToken));
        return messages;
    }

    private static async Task<string> RunJavaPipelineDiagnosticAsync(CancellationToken cancellationToken)
    {
        var fixtureRoot = Path.Combine(
            Environment.CurrentDirectory,
            "artifacts",
            "diagnostics-java",
            Guid.NewGuid().ToString("N"));
        var mainDirectory = Path.Combine(fixtureRoot, "src", "main", "java", "example");
        var testDirectory = Path.Combine(fixtureRoot, "src", "test", "java", "example");
        var logDirectory = Path.Combine(fixtureRoot, "diagnostic-logs");
        Directory.CreateDirectory(mainDirectory);
        Directory.CreateDirectory(testDirectory);
        Directory.CreateDirectory(logDirectory);

        await File.WriteAllTextAsync(Path.Combine(fixtureRoot, "pom.xml"), """
            <project xmlns="http://maven.apache.org/POM/4.0.0"
                     xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                     xsi:schemaLocation="http://maven.apache.org/POM/4.0.0 https://maven.apache.org/xsd/maven-4.0.0.xsd">
              <modelVersion>4.0.0</modelVersion>
              <groupId>diagnostic</groupId>
              <artifactId>rq2-java-diagnostic</artifactId>
              <version>1.0.0</version>
              <properties>
                <maven.compiler.release>17</maven.compiler.release>
                <project.build.sourceEncoding>UTF-8</project.build.sourceEncoding>
              </properties>
              <dependencies>
                <dependency>
                  <groupId>junit</groupId>
                  <artifactId>junit</artifactId>
                  <version>4.13.2</version>
                  <scope>test</scope>
                </dependency>
              </dependencies>
            </project>
            """, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(mainDirectory, "Calculator.java"), """
            package example;
            public final class Calculator {
                public int add(int left, int right) { return left + right; }
            }
            """, cancellationToken);
        var testPath = Path.Combine(testDirectory, "CalculatorTest.java");
        await File.WriteAllTextAsync(testPath, """
            package example;
            import org.junit.Test;
            import static org.junit.Assert.assertEquals;

            public class CalculatorTest {
                @Test
                public void shouldAdd() {
                    assertEquals(5, new Calculator().add(2, 2));
                }

                @Test
                public void shouldKeepWorking() {
                    assertEquals(2, new Calculator().add(1, 1));
                }
            }
            """, cancellationToken);

        var task = new ExperimentTask(
            "DIAGNOSTIC",
            fixtureRoot,
            "S-DIAGNOSTIC",
            "T-DIAGNOSTIC",
            "shouldAdd",
            "2 + 2 must equal 4.",
            "S-DIAGNOSTIC -> T-DIAGNOSTIC",
            "Expected 5 but was 4.",
            "src/test/java/example/CalculatorTest.java",
            "example.CalculatorTest",
            BuildSystem: "maven",
            FocalSourceFiles: ["src/main/java/example/Calculator.java"],
            PreviouslyVerifiedTests: ["example.CalculatorTest#shouldKeepWorking"]);
        var buildRunner = new JavaBuildRunner(task, fixtureRoot, logDirectory);
        var compile = await buildRunner.CompileAsync(0, cancellationToken);
        if (!compile.Succeeded) throw new InvalidOperationException("Java diagnostic compilation failed.");
        var initialFocal = await buildRunner.RunFocalTestAsync(0, cancellationToken);
        if (initialFocal.Succeeded) throw new InvalidOperationException("Diagnostic focal test should fail initially.");

        var originalSource = await File.ReadAllTextAsync(testPath, cancellationToken);
        var repairedSource = JavaMethodEditor.ReplaceMethod(
            originalSource,
            "shouldAdd",
            "@Test\n    public void shouldAdd() {\n        assertEquals(4, new Calculator().add(2, 2));\n    }");
        await File.WriteAllTextAsync(testPath, repairedSource, cancellationToken);
        var repairedFocal = await buildRunner.RunFocalTestAsync(1, cancellationToken);
        var regression = await buildRunner.RunRegressionAsync(1, cancellationToken);
        if (!repairedFocal.Succeeded || regression.RegressionCount != 0)
        {
            throw new InvalidOperationException("Repaired Java focal/regression diagnostic failed.");
        }

        var corruptedSource = repairedSource.Replace(
            "assertEquals(2, new Calculator().add(1, 1));",
            "assertEquals(3, new Calculator().add(1, 1));",
            StringComparison.Ordinal);
        await File.WriteAllTextAsync(testPath, corruptedSource, cancellationToken);
        var detectedRegression = await buildRunner.RunRegressionAsync(2, cancellationToken);
        if (detectedRegression.RegressionCount == 0)
        {
            throw new InvalidOperationException("Complete test-class regression diagnostic failed to detect a broken existing test.");
        }
        await File.WriteAllTextAsync(testPath, repairedSource, cancellationToken);

        var coverage = await buildRunner.RunCoverageAsync(1, cancellationToken);
        var mutation = await buildRunner.RunMutationAsync(1, cancellationToken);
        if (coverage.Status != MeasurementStatus.Succeeded ||
            mutation.Status != MeasurementStatus.Succeeded ||
            !coverage.Value.HasValue ||
            !mutation.Value.HasValue)
        {
            throw new InvalidOperationException(
                $"Java quality measurement diagnostic failed: coverage={coverage.Status}, mutation={mutation.Status}.");
        }

        return $"Real Maven/JUnit repair and scoped JaCoCo/PIT measurement: OK " +
            $"(coverage={coverage.Value.Value:P1}, mutation={mutation.Value.Value:P1})";
    }

    private static async Task<string> RunLlmProtocolDiagnosticAsync(CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            for (var index = 0; index < 4; index++)
            {
                using var connection = await listener.AcceptTcpClientAsync(cancellationToken);
                await HandleDiagnosticRequestAsync(connection, cancellationToken);
            }
        }, cancellationToken);

        try
        {
            using var client = new CompatibleLlmClient(
                "diagnostic-secret",
                $"http://127.0.0.1:{port}/v1/",
                LlmApiProtocol.Responses,
                requiresApiKey: true,
                useJsonMode: true);
            await client.ValidateCredentialsAsync("key", cancellationToken);
            var models = await client.ListTextModelsAsync(cancellationToken);
            if (!models.Contains("gpt-5.6-terra", StringComparer.Ordinal) ||
                !models.Contains("anthropic/claude-sonnet", StringComparer.Ordinal) ||
                models.Contains("text-embedding-3-small", StringComparer.Ordinal))
            {
                throw new InvalidOperationException("Multi-provider model filtering diagnostic failed.");
            }

            var artifactDirectory = Path.Combine(
                Environment.CurrentDirectory,
                "artifacts",
                "diagnostics-openai",
                Guid.NewGuid().ToString("N"));
            var response = await client.GenerateRepairAsync(
                "gpt-5.6-terra",
                "medium",
                "Return a repair.",
                "Repair the sample.",
                artifactDirectory,
                1,
                cancellationToken);
            if (response.InputTokens != 123 ||
                response.OutputTokens != 45 ||
                response.CachedInputTokens != 20 ||
                response.CacheWriteInputTokens != 5 ||
                response.ReasoningTokens != 7 ||
                !response.ReplacementSource.Contains("shouldWork", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Responses parsing diagnostic failed.");
            }

            var savedRequest = await File.ReadAllTextAsync(
                Path.Combine(artifactDirectory, "iteration-01-request.json"),
                cancellationToken);
            if (savedRequest.Contains("diagnostic-secret", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("API key leaked into the request artifact.");
            }

            var chatArtifactDirectory = Path.Combine(
                Environment.CurrentDirectory,
                "artifacts",
                "diagnostics-chat",
                Guid.NewGuid().ToString("N"));
            using var chatClient = new CompatibleLlmClient(
                string.Empty,
                $"http://127.0.0.1:{port}/v1/",
                LlmApiProtocol.ChatCompletions,
                requiresApiKey: false,
                useJsonMode: true);
            var chatResponse = await chatClient.GenerateRepairAsync(
                "anthropic/claude-sonnet",
                "medium",
                "Return a repair.",
                "Repair the sample.",
                chatArtifactDirectory,
                1,
                cancellationToken);
            if (chatResponse.InputTokens != 80 || chatResponse.OutputTokens != 30 ||
                !chatResponse.ReplacementSource.Contains("shouldWork", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Chat Completions parsing diagnostic failed.");
            }

            await serverTask;
            return "Multi-provider models, Responses/Chat protocols, and secret isolation: OK";
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task HandleDiagnosticRequestAsync(
        TcpClient client,
        CancellationToken cancellationToken)
    {
        await using var stream = client.GetStream();
        var request = await ReadHttpRequestAsync(stream, cancellationToken);
        object body;
        if (request.Path.EndsWith("/key", StringComparison.Ordinal))
        {
            body = new { data = new { label = "diagnostic-key" } };
        }
        else if (request.Path.EndsWith("/models", StringComparison.Ordinal))
        {
            body = new
            {
                data = new object[]
                {
                    new { id = "gpt-5.6-terra" },
                    new { id = "anthropic/claude-sonnet" },
                    new { id = "text-embedding-3-small" }
                }
            };
        }
        else if (request.Path.EndsWith("/chat/completions", StringComparison.Ordinal))
        {
            using var requestDocument = JsonDocument.Parse(request.Body);
            if (!requestDocument.RootElement.TryGetProperty("messages", out _) ||
                !requestDocument.RootElement.TryGetProperty("response_format", out _))
            {
                throw new InvalidOperationException("Chat request is missing messages/JSON mode configuration.");
            }

            body = new
            {
                id = "chatcmpl_diagnostic",
                model = "anthropic/claude-sonnet",
                choices = new[]
                {
                    new
                    {
                        message = new
                        {
                            role = "assistant",
                            content = "{\"replacementSource\":\"@Test void shouldWork() {}\",\"explanation\":\"fixed\"}"
                        }
                    }
                },
                usage = new { prompt_tokens = 80, completion_tokens = 30 }
            };
        }
        else
        {
            using var requestDocument = JsonDocument.Parse(request.Body);
            if (!requestDocument.RootElement.TryGetProperty("text", out _) ||
                !requestDocument.RootElement.TryGetProperty("reasoning", out _))
            {
                throw new InvalidOperationException("Responses request is missing text/reasoning configuration.");
            }

            body = new
            {
                id = "resp_diagnostic",
                model = "gpt-5.6-terra",
                output = new[]
                {
                    new
                    {
                        type = "message",
                        content = new[]
                        {
                            new
                            {
                                type = "output_text",
                                text = "{\"replacementSource\":\"@Test void shouldWork() {}\",\"explanation\":\"fixed\"}"
                            }
                        }
                    }
                },
                usage = new
                {
                    input_tokens = 123,
                    output_tokens = 45,
                    input_tokens_details = new { cached_tokens = 20, cache_write_tokens = 5 },
                    output_tokens_details = new { reasoning_tokens = 7 }
                }
            };
        }

        var json = JsonSerializer.Serialize(body);
        var payload = Encoding.UTF8.GetBytes(json);
        var headers = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(headers, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
    }

    internal static async Task<(string Path, string Body, string Authorization)> ReadHttpRequestAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var buffer = new List<byte>();
        var singleByte = new byte[1];
        while (buffer.Count < 64 * 1024)
        {
            var read = await stream.ReadAsync(singleByte, cancellationToken);
            if (read == 0) break;
            buffer.Add(singleByte[0]);
            if (buffer.Count >= 4 &&
                buffer[^4] == '\r' && buffer[^3] == '\n' &&
                buffer[^2] == '\r' && buffer[^1] == '\n')
            {
                break;
            }
        }

        var headerText = Encoding.ASCII.GetString(buffer.ToArray());
        var headerLines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var requestParts = headerLines[0].Split(' ');
        var contentLengthLine = headerLines.FirstOrDefault(line =>
            line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        var authorizationLine = headerLines.FirstOrDefault(line =>
            line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase));
        var contentLength = contentLengthLine is null
            ? 0
            : int.Parse(contentLengthLine.Split(':', 2)[1].Trim(), System.Globalization.CultureInfo.InvariantCulture);
        var bodyBuffer = new byte[contentLength];
        var offset = 0;
        while (offset < contentLength)
        {
            var read = await stream.ReadAsync(bodyBuffer.AsMemory(offset, contentLength - offset), cancellationToken);
            if (read == 0) break;
            offset += read;
        }

        return (
            requestParts[1],
            Encoding.UTF8.GetString(bodyBuffer, 0, offset),
            authorizationLine?.Split(':', 2)[1].Trim() ?? string.Empty);
    }
}
