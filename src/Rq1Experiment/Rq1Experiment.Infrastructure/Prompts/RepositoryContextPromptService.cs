using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Rq1Experiment.Contracts.Prompts;
using Rq1Experiment.Contracts.Targets;
using Rq1Experiment.Core.Abstractions;
using Rq1Experiment.Core.Llm;
using Rq1Experiment.Infrastructure.Java;
using Rq1Experiment.Infrastructure.Persistence;

namespace Rq1Experiment.Infrastructure.Prompts;

public sealed class RepositoryContextPromptService(IProcessRunner processRunner) : IRepositoryContextPromptService
{
    private const string ExtractorVersion = "context-extractor-v4";
    private const string RetrievalRule = "DEPTH_ONE_STATIC_CALLS_AND_RELATED_API_SURFACES_V4";
    private static readonly JsonSerializerOptions CompactJson = new(JsonSupport.Options) { WriteIndented = false };

    public async Task<RepositoryContextPromptReport> RunAsync(
        RepositoryContextPromptRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var workspace = Path.GetFullPath(request.WorkspaceRoot);
        var projects = Path.GetFullPath(request.ProjectsRoot);
        var subjects = Path.GetFullPath(request.SubjectsRoot);
        var targets = Path.GetFullPath(request.TargetsRoot);
        var root = Path.GetFullPath(request.OutputRoot);
        var contextRoot = Path.Combine(root, "context");
        var promptRoot = Path.Combine(root, "prompts");
        var contractRoot = Path.Combine(promptRoot, "schemas");
        var renderedRoot = Path.Combine(promptRoot, "rendered");
        Directory.CreateDirectory(contextRoot);
        Directory.CreateDirectory(contractRoot);
        Directory.CreateDirectory(renderedRoot);

        var splits = new[] { ResolveSplit(request.ExperimentMode) };
        var subjectPaths = splits.ToDictionary(split => split,
            split => Path.Combine(subjects, $"subjects-{split}.jsonl"), StringComparer.Ordinal);
        var targetPaths = splits.ToDictionary(split => split,
            split => Path.Combine(targets, $"targets-{split}.jsonl"), StringComparer.Ordinal);
        foreach (var required in subjectPaths.Values.Concat(targetPaths.Values))
            if (!File.Exists(required)) throw new FileNotFoundException("Phase 3/4 frozen artifact is missing.", required);

        progress?.Report("Building deterministic repository-context extractor...");
        var analyzerRoot = Path.Combine(workspace, "tools", "rq1", "java-structural-analyzer");
        var analyzerJar = Path.Combine(analyzerRoot, "target", "java-structural-analyzer.jar");
        var environment = BuildJavaEnvironment(request.JavaHome);
        var build = await BuildAnalyzerAsync(workspace, analyzerRoot, environment, cancellationToken);
        await WriteLogAsync(contextRoot, "context-extractor-build.log", build, cancellationToken);
        if (!build.IsSuccess || !File.Exists(analyzerJar))
            throw new InvalidOperationException("Repository-context extractor build failed; inspect context-extractor-build.log.");

        var java = string.IsNullOrWhiteSpace(request.JavaHome) ? "java" :
            Path.Combine(request.JavaHome, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
        var outputs = new Dictionary<(string Split, int Run), string>();
        foreach (var split in splits)
        for (var run = 1; run <= 2; run++)
        {
            var manifest = subjectPaths[split];
            progress?.Report($"Extracting {split} common repository contexts ({run}/2)...");
            var output = Path.Combine(contextRoot, $"contexts-{split}.run-{run}.jsonl");
            var result = await processRunner.RunAsync(java,
                ["-cp", analyzerJar, "io.rq1.analyzer.ContextExtractorMain",
                    "--projects-root", projects, "--subjects", manifest, "--output", output],
                workspace, TimeSpan.FromMinutes(8), environment, cancellationToken);
            await WriteLogAsync(contextRoot, $"context-extractor-{split}-run-{run}.log", result, cancellationToken);
            if (!result.IsSuccess) throw new InvalidOperationException(
                $"Repository-context extraction failed for {split} run {run}; inspect retained logs.");
            outputs[(split, run)] = output;
        }

        var deterministic = splits.All(split =>
            Sha256File(outputs[(split, 1)]) == Sha256File(outputs[(split, 2)]));
        var allContexts = new List<RepositoryContext>();
        var contextCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var split in splits)
        {
            var raw = await ReadLinesAsync<RepositoryContext>(outputs[(split, 1)], cancellationToken);
            foreach (var context in raw)
            {
                NormalizeContext(context);
                context.Build = ReadBuildContext(Path.Combine(projects, context.ProjectId));
                context.ContextHash = string.Empty;
                context.ContextHash = Sha256Text(JsonSerializer.Serialize(context, CompactJson));
            }
            var finalPath = Path.Combine(contextRoot, $"contexts-{split}.jsonl");
            await WriteLinesAsync(finalPath, raw, cancellationToken);
            contextCounts[split] = raw.Count;
            allContexts.AddRange(raw);
        }
        allContexts = allContexts.OrderBy(item => item.SubjectId, StringComparer.Ordinal).ToList();

        progress?.Report("Writing and validating frozen prompt templates and strict JSON contracts...");
        var templates = PromptTemplateCatalog.LoadTemplates(workspace);
        foreach (var (name, content) in templates)
            await File.WriteAllTextAsync(Path.Combine(promptRoot, name), Normalize(content), cancellationToken);
        var schemas = PromptTemplateCatalog.LoadSchemas(workspace);
        foreach (var (name, content) in schemas)
            await File.WriteAllTextAsync(Path.Combine(contractRoot, name), Normalize(content), cancellationToken);
        var schemasValid = schemas.Values.All(IsStrictSchemaDocument);
        var promptContractsValid = ValidateTemplateContracts(templates);

        var allInventories = new List<StructuralTargetInventory>();
        foreach (var split in splits)
            allInventories.AddRange(await ReadLinesAsync<StructuralTargetInventory>(targetPaths[split], cancellationToken));
        var inventories = allInventories.ToDictionary(item => item.SubjectId, StringComparer.Ordinal);
        var treatmentRows = new List<object>();
        var directLeakFree = true;
        foreach (var context in allContexts)
        {
            if (!inventories.TryGetValue(context.SubjectId, out var inventory))
                throw new InvalidOperationException($"No frozen target inventory for {context.SubjectId}.");
            var contextJson = JsonSerializer.Serialize(context, JsonSupport.Options);
            var targetJson = JsonSerializer.Serialize(inventory, JsonSupport.Options);
            var requiredTestClassName = GeneratedTestIdentity.For(context);
            var rendered = Path.Combine(renderedRoot, context.SubjectId);
            Directory.CreateDirectory(rendered);
            await WriteRenderedAsync(rendered, "direct-system.md", templates["direct-system.md"], null, null, cancellationToken);
            var directUser = Render(templates["direct-user.md"], contextJson, null, null, requiredTestClassName);
            await File.WriteAllTextAsync(Path.Combine(rendered, "direct-user.md"), directUser, cancellationToken);
            await WriteRenderedAsync(rendered, "planner-system.md", templates["planner-system.md"], null, null, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(rendered, "planner-user.md"),
                Render(templates["planner-user.md"], contextJson, targetJson, null), cancellationToken);
            await WriteRenderedAsync(rendered, "generator-system.md", templates["generator-system.md"], null, null, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(rendered, "generator-user.pending-plan.md"),
                Render(templates["generator-user.md"], contextJson, null,
                    "{{FINALIZED_SCENARIO_PLAN_JSON}}", requiredTestClassName), cancellationToken);

            directLeakFree &= !directUser.Contains("targetIds", StringComparison.Ordinal)
                              && !directUser.Contains("structuralTarget", StringComparison.OrdinalIgnoreCase)
                              && !directUser.Contains("scenarioId", StringComparison.Ordinal);
            treatmentRows.Add(new
            {
                context.SubjectId,
                context.ContextHash,
                direct = new { commonContextHash = context.ContextHash, additions = Array.Empty<string>() },
                plannedPlanner = new { commonContextHash = context.ContextHash, additions = new[] { "FROZEN_STRUCTURAL_TARGET_INVENTORY" } },
                plannedGenerator = new { commonContextHash = context.ContextHash, additions = new[] { "FINALIZED_SCENARIO_PLAN" } }
            });
        }
        await WriteLinesAsync(Path.Combine(promptRoot, "treatment-context-manifest.jsonl"), treatmentRows, cancellationToken);

        var existingTestsExcluded = allContexts.All(context =>
            IsProductionPath(context.SourceFile)
            && context.DirectlyCalledProjectMethods.All(method => IsProductionPath(method.SourceFile)));
        var templateFingerprint = Fingerprint(templates.OrderBy(x => x.Key).Select(x => x.Key + "\n" + Normalize(x.Value)));
        var contextFingerprint = Fingerprint(allContexts.Select(x => x.SubjectId + "=" + x.ContextHash));
        var configurationFingerprint = Fingerprint([
            request.ProviderId.ToLowerInvariant(), request.BaseEndpoint.TrimEnd('/'), request.RequestedModelId,
            request.ExperimentMode.ToUpperInvariant(), ExtractorVersion, RetrievalRule, templateFingerprint, contextFingerprint
        ]);
        var lockAccepted = await ValidateOrCreateLockAsync(root, request, templateFingerprint,
            contextFingerprint, configurationFingerprint, cancellationToken);

        await File.WriteAllTextAsync(Path.Combine(promptRoot, "PROMPT_CONTRACT.md"),
            PromptTemplateCatalog.LoadContract(workspace), cancellationToken);
        var errorCount = Directory.EnumerateFiles(contextRoot, "*.errors.txt")
            .Sum(path => File.ReadLines(path).Count(line => !string.IsNullOrWhiteSpace(line)));
        var report = new RepositoryContextPromptReport
        {
            ExtractorVersion = ExtractorVersion,
            RetrievalRule = RetrievalRule,
            SubjectCount = allContexts.Count,
            PilotSubjectCount = allContexts.Count(x => x.Split.Equals("PILOT", StringComparison.OrdinalIgnoreCase)),
            OfficialSubjectCount = allContexts.Count(x => x.Split.Equals("OFFICIAL", StringComparison.OrdinalIgnoreCase)),
            SmokeSubjectCount = allContexts.Count(x => x.Split.Equals("SMOKE", StringComparison.OrdinalIgnoreCase)),
            ResolvedCalledMethodCount = allContexts.Sum(x => x.DirectlyCalledProjectMethods.Count),
            RelatedTypeApiCount = allContexts.Sum(x => x.RelatedTypeApis.Count),
            UnresolvedCallCount = allContexts.Sum(x => x.UnresolvedCalls.Count),
            ErrorCount = errorCount,
            DeterministicTwoRuns = deterministic,
            StrictSchemasValid = schemasValid,
            CommonContextSharedExactly = treatmentRows.Count == allContexts.Count,
            DirectPromptHasNoTargetLeakage = directLeakFree,
            ExistingTestBodiesExcluded = existingTestsExcluded,
            PromptContractsValid = promptContractsValid,
            HashLockAccepted = lockAccepted,
            ContextFingerprint = contextFingerprint,
            PromptTemplateFingerprint = templateFingerprint,
            ConfigurationFingerprint = configurationFingerprint,
            Complete = allContexts.Count > 0 && deterministic && schemasValid && promptContractsValid
                       && directLeakFree && existingTestsExcluded && lockAccepted && errorCount == 0,
            GeneratedAt = DateTimeOffset.UtcNow
        };
        await File.WriteAllTextAsync(Path.Combine(root, "phase5-context-prompt-report.json"),
            JsonSerializer.Serialize(report, JsonSupport.Options), cancellationToken);
        var modeReportName = $"phase5-context-prompt-report-{splits[0]}.json";
        await File.WriteAllTextAsync(Path.Combine(root, modeReportName),
            JsonSerializer.Serialize(report, JsonSupport.Options), cancellationToken);
        progress?.Report($"Phase 5 artifacts frozen: contexts={report.SubjectCount}, resolved calls={report.ResolvedCalledMethodCount}, context fingerprint={contextFingerprint[..12]}...");
        return report;
    }

    private static string ResolveSplit(string mode) => mode.Trim().ToUpperInvariant() switch
    {
        "SMOKE" => "smoke",
        "PILOT" => "pilot",
        "OFFICIAL" => "official",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode,
            "Experiment mode must be Smoke, Pilot, or Official.")
    };

    private async Task<ProcessRunResult> BuildAnalyzerAsync(string workspace, string analyzerRoot,
        IReadOnlyDictionary<string, string?> environment, CancellationToken cancellationToken)
    {
        var maven = BundledBuildToolResolver.ResolveMaven(workspace);
        var command = OperatingSystem.IsWindows() && maven.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            ? "cmd.exe" : maven;
        var pom = Path.Combine(analyzerRoot, "pom.xml");
        var args = new List<string>();
        if (command == "cmd.exe") args.AddRange(["/d", "/c", "call", Path.GetRelativePath(workspace, maven)]);
        args.AddRange(["-B", "-ntp", "package", "-f", OperatingSystem.IsWindows() ? Path.GetRelativePath(workspace, pom) : pom]);
        return await processRunner.RunAsync(command, args, workspace, TimeSpan.FromMinutes(5), environment, cancellationToken);
    }

    private static BuildContext ReadBuildContext(string projectRoot)
    {
        var pom = Directory.EnumerateFiles(projectRoot, "pom.xml", SearchOption.AllDirectories)
            .OrderBy(path => path.Length).ThenBy(path => path, StringComparer.Ordinal).FirstOrDefault();
        if (pom is not null)
        {
            try
            {
                var document = XDocument.Load(pom);
                var ns = document.Root?.Name.Namespace ?? XNamespace.None;
                string Value(XElement node, string name) => node.Element(ns + name)?.Value.Trim() ?? string.Empty;
                var dependencies = document.Descendants(ns + "dependency")
                    .Select(node => new DependencyContext(Value(node, "groupId"), Value(node, "artifactId"),
                        Value(node, "version"), string.IsNullOrWhiteSpace(Value(node, "scope")) ? "compile" : Value(node, "scope")))
                    .OrderBy(x => x.GroupId, StringComparer.Ordinal).ThenBy(x => x.ArtifactId, StringComparer.Ordinal).ToList();
                var properties = document.Root?.Element(ns + "properties");
                var java = properties?.Element(ns + "maven.compiler.release")?.Value
                           ?? properties?.Element(ns + "maven.compiler.source")?.Value
                           ?? properties?.Element(ns + "java.version")?.Value ?? "PROJECT_DEFAULT";
                var hasJupiter = dependencies.Any(x =>
                    x.GroupId.Equals("org.junit.jupiter", StringComparison.OrdinalIgnoreCase)
                    || x.ArtifactId.Contains("junit-jupiter", StringComparison.OrdinalIgnoreCase));
                var hasJunit4 = dependencies.Any(x =>
                    x.GroupId.Equals("junit", StringComparison.OrdinalIgnoreCase)
                    && x.ArtifactId.Equals("junit", StringComparison.OrdinalIgnoreCase));
                var framework = hasJupiter ? "JUnit 5" : hasJunit4 ? "JUnit 4" : "PROJECT_DECLARED_OR_UNKNOWN";
                return new BuildContext { BuildTool = "Maven", ModulePath = ".", JavaVersion = java,
                    TestFramework = framework,
                    RequiredTestApi = hasJupiter ? "JUNIT_JUPITER" : hasJunit4 ? "JUNIT_4" : "UNKNOWN",
                    TestAnnotation = hasJupiter ? "org.junit.jupiter.api.Test" : hasJunit4 ? "org.junit.Test" : "UNKNOWN",
                    ExecutionProvider = hasJupiter ? "JUnitPlatformProvider" : hasJunit4 ? "JUnit4Provider" : "UNKNOWN",
                    Dependencies = dependencies };
            }
            catch { /* report deterministic fallback */ }
        }
        var gradle = Directory.EnumerateFiles(projectRoot, "build.gradle*", SearchOption.AllDirectories).FirstOrDefault();
        return new BuildContext { BuildTool = gradle is null ? "UNKNOWN" : "Gradle", ModulePath = ".",
            JavaVersion = "PROJECT_DEFAULT", TestFramework = "PROJECT_DECLARED_OR_UNKNOWN" };
    }

    private static bool ValidateTemplateContracts(IReadOnlyDictionary<string, string> templates) =>
        templates.Count == 6
        && templates["direct-user.md"].Contains("{{COMMON_CONTEXT_JSON}}", StringComparison.Ordinal)
        && templates["direct-user.md"].Contains("{{REQUIRED_TEST_CLASS_FQN}}", StringComparison.Ordinal)
        && !templates["direct-user.md"].Contains("TARGET", StringComparison.OrdinalIgnoreCase)
        && !templates["direct-user.md"].Contains("SCENARIO", StringComparison.OrdinalIgnoreCase)
        && templates["planner-user.md"].Contains("{{COMMON_CONTEXT_JSON}}", StringComparison.Ordinal)
        && templates["planner-user.md"].Contains("{{STRUCTURAL_TARGET_INVENTORY_JSON}}", StringComparison.Ordinal)
        && templates["generator-user.md"].Contains("{{COMMON_CONTEXT_JSON}}", StringComparison.Ordinal)
        && templates["generator-user.md"].Contains("{{SCENARIO_PLAN_JSON}}", StringComparison.Ordinal)
        && templates["generator-user.md"].Contains("{{REQUIRED_TEST_CLASS_FQN}}", StringComparison.Ordinal)
        && templates["direct-system.md"].Contains("TEST-CODE SAFETY CONTRACT", StringComparison.Ordinal)
        && templates["direct-system.md"].Contains("private/inaccessible", StringComparison.Ordinal)
        && templates["direct-system.md"].Contains("implementation trace", StringComparison.Ordinal)
        && templates["direct-system.md"].Contains("JUNIT_JUPITER", StringComparison.Ordinal)
        && templates["direct-system.md"].Contains("focalMethodOverloadSignatures", StringComparison.Ordinal)
        && templates["direct-system.md"].Contains("must never reuse or shadow", StringComparison.Ordinal)
        && templates["direct-system.md"].Contains("checked-exception audit", StringComparison.Ordinal)
        && templates["direct-system.md"].Contains("setup state transition", StringComparison.Ordinal)
        && templates["planner-system.md"].Contains("PLANNING CONTRACT", StringComparison.Ordinal)
        && templates["planner-system.md"].Contains("runtime infrastructure", StringComparison.Ordinal)
        && templates["planner-system.md"].Contains("accessibleConstructorSources", StringComparison.Ordinal)
        && templates["generator-system.md"].Contains("TEST-CODE SAFETY CONTRACT", StringComparison.Ordinal)
        && templates["generator-system.md"].Contains("JUNIT_JUPITER", StringComparison.Ordinal)
        && templates["generator-system.md"].Contains("focalMethodOverloadSignatures", StringComparison.Ordinal)
        && templates["generator-system.md"].Contains("must never reuse or shadow", StringComparison.Ordinal)
        && templates["generator-system.md"].Contains("checked-exception audit", StringComparison.Ordinal)
        && templates["generator-system.md"].Contains("setup state transition", StringComparison.Ordinal)
        && templates["generator-system.md"].Contains("Every `mappings[].testClass` must exactly equal `testClassName`", StringComparison.Ordinal);

    private static bool IsStrictSchemaDocument(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return root.GetProperty("type").GetString() == "object"
                   && root.GetProperty("additionalProperties").ValueKind == JsonValueKind.False
                   && root.GetProperty("required").GetArrayLength() > 0;
        }
        catch { return false; }
    }

    private static async Task<bool> ValidateOrCreateLockAsync(string root, RepositoryContextPromptRequest request,
        string templates, string contexts, string configuration, CancellationToken cancellationToken)
    {
        var experimentHash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(request.ExperimentId.Trim()))).ToLowerInvariant();
        var path = Path.Combine(root, "state", "phase5-prompt-context-locks", experimentHash + ".json");
        var value = new { version = 1, extractorVersion = ExtractorVersion, retrievalRule = RetrievalRule,
            promptTemplateFingerprint = templates, contextFingerprint = contexts, configurationFingerprint = configuration,
            providerId = request.ProviderId, baseEndpoint = request.BaseEndpoint, requestedModelId = request.RequestedModelId };
        // Pilot/Smoke may reuse frozen templates, but must never replace an Official lock.
        if (!request.ExperimentMode.Equals("Official", StringComparison.OrdinalIgnoreCase)) return true;
        if (File.Exists(path) && request.ExperimentMode.Equals("Official", StringComparison.OrdinalIgnoreCase))
        {
            using var old = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken));
            if (old.RootElement.GetProperty("configurationFingerprint").GetString() == configuration) return true;
            // Prompt/context evolution is allowed only before the first semantic generation artifact exists.
            var experimentRuns = Path.Combine(root, "runs", request.ExperimentId);
            if (Directory.Exists(experimentRuns)
                && Directory.EnumerateFiles(experimentRuns, "run.json", SearchOption.AllDirectories)
                    .Any(run => run.Replace('\\', '/').Contains("/DIRECT/", StringComparison.Ordinal))) return false;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonSupport.Options), cancellationToken);
        return true;
    }

    private static string Render(string template, string? context, string? targets, string? plan,
        string? requiredTestClassName = null) => Normalize(template)
        .Replace("{{COMMON_CONTEXT_JSON}}", context ?? "{{COMMON_CONTEXT_JSON}}", StringComparison.Ordinal)
        .Replace("{{STRUCTURAL_TARGET_INVENTORY_JSON}}", targets ?? "{{STRUCTURAL_TARGET_INVENTORY_JSON}}", StringComparison.Ordinal)
        .Replace("{{SCENARIO_PLAN_JSON}}", plan ?? "{{SCENARIO_PLAN_JSON}}", StringComparison.Ordinal)
        .Replace("{{REQUIRED_TEST_CLASS_FQN}}",
            requiredTestClassName ?? "{{REQUIRED_TEST_CLASS_FQN}}", StringComparison.Ordinal);
    private static async Task WriteRenderedAsync(string root, string name, string template, string? context,
        string? targets, CancellationToken token) =>
        await File.WriteAllTextAsync(Path.Combine(root, name), Render(template, context, targets, null), token);
    private static bool IsProductionPath(string path) => path.Replace('\\', '/').Contains("/src/main/java/", StringComparison.Ordinal)
                                                         || path.Replace('\\', '/').StartsWith("src/main/java/", StringComparison.Ordinal);
    private static string Normalize(string value) => value.Replace("\r\n", "\n").Trim() + "\n";
    private static void NormalizeContext(RepositoryContext context)
    {
        static string Text(string value) => value.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        context.SourceFile = context.SourceFile.Replace('\\', '/');
        context.Imports = context.Imports.Select(Text).ToList();
        context.EnclosingTypeDeclaration = Text(context.EnclosingTypeDeclaration);
        context.Fields = context.Fields.Select(Text).ToList();
        context.Constructors = context.Constructors.Select(Text).ToList();
        context.FocalMethodOverloadSignatures = context.FocalMethodOverloadSignatures
            .Select(Text).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToList();
        context.FocalMethodSource = Text(context.FocalMethodSource);
        foreach (var type in context.RelatedTypeApis)
        {
            type.SourceFile = type.SourceFile.Replace('\\', '/');
            type.TypeDeclaration = Text(type.TypeDeclaration);
            type.AccessibleConstructors = type.AccessibleConstructors.Select(Text)
                .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToList();
            type.AccessibleConstructorSources = type.AccessibleConstructorSources.Select(Text)
                .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToList();
            type.AccessibleMethods = type.AccessibleMethods.Select(Text)
                .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToList();
            type.AccessibleFields = type.AccessibleFields.Select(Text)
                .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToList();
            type.SelectionReasons = type.SelectionReasons.Select(Text)
                .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToList();
        }
        foreach (var method in context.DirectlyCalledProjectMethods)
        {
            method.SourceFile = method.SourceFile.Replace('\\', '/');
            method.Source = Text(method.Source);
        }
        foreach (var call in context.UnresolvedCalls) call.Expression = Text(call.Expression);
    }
    private static string Fingerprint(IEnumerable<string> values) => Sha256Text(string.Join("\n---\n", values));
    private static string Sha256Text(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static IReadOnlyDictionary<string, string?> BuildJavaEnvironment(string javaHome) =>
        string.IsNullOrWhiteSpace(javaHome) ? new Dictionary<string, string?>() : new Dictionary<string, string?>
        { ["JAVA_HOME"] = javaHome, ["PATH"] = Path.Combine(javaHome, "bin") + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH") };

    private static async Task<List<T>> ReadLinesAsync<T>(string path, CancellationToken token)
    {
        var result = new List<T>();
        foreach (var line in await File.ReadAllLinesAsync(path, token))
            if (!string.IsNullOrWhiteSpace(line)) result.Add(JsonSerializer.Deserialize<T>(line, JsonSupport.Options)
                ?? throw new InvalidDataException("Invalid JSONL row: " + path));
        return result;
    }
    private static async Task WriteLinesAsync<T>(string path, IEnumerable<T> rows, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var text = string.Join('\n', rows.Select(row => JsonSerializer.Serialize(row, CompactJson))) + "\n";
        await File.WriteAllTextAsync(path, text, token);
    }
    private static async Task WriteLogAsync(string root, string name, ProcessRunResult result, CancellationToken token) =>
        await File.WriteAllTextAsync(Path.Combine(root, name),
            $"ExitCode: {result.ExitCode}\nTimedOut: {result.TimedOut}\nDuration: {result.Duration}\n\nSTDOUT\n{result.StandardOutput}\n\nSTDERR\n{result.StandardError}", token);
}
