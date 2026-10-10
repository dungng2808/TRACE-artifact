using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using FullChain.Contracts;
using FullChain.Core.Planning;
using FullChain.Infrastructure.LLM;

namespace FullChain.Infrastructure.Rq1;

public sealed class Rq1PlannedGenerationAdapter : ITestGenerator
{
    private readonly Rq1ContextAdapter _contextAdapter;
    private readonly ILlmClient _llmClient;

    public Rq1PlannedGenerationAdapter(Rq1ContextAdapter contextAdapter, ILlmClient llmClient)
    {
        _contextAdapter = contextAdapter;
        _llmClient = llmClient;
    }

    public async Task<GeneratedTestArtifact> GenerateTestsAsync(
        TargetDescriptor target,
        string contextJson,
        IGlobalBudgetGovernor budget,
        CancellationToken ct = default)
    {
        // 1. Load context and prompts
        var contextPackage = await _contextAdapter.LoadContextAsync(target, ct);

        // 2. Call Planner
        var plannerResponse = await _llmClient.SendPromptAsync(
            contextPackage.PlannerSystemPrompt,
            contextPackage.PlannerUserPrompt,
            budget,
            ct);

        var validatedPlan = ScenarioPlanValidator.Validate(plannerResponse.Content, target.SubjectId, maxScenarios: 3);
        var planHash = ComputeSha256(validatedPlan.RawJson);

        // 3. Prepare Generator Prompt
        using var ctxDoc = JsonDocument.Parse(contextPackage.CommonContextJson);
        var pkg = ctxDoc.RootElement.TryGetProperty("packageName", out var pkgElem) ? pkgElem.GetString() ?? "" : "";
        var simpleName = target.FocalClass.Split('.').Last().Split('$').Last() + "GeneratedTest";
        var requiredFqn = string.IsNullOrWhiteSpace(pkg) ? simpleName : $"{pkg}.{simpleName}";

        var genUserPrompt = RenderGeneratorPrompt(
            contextPackage.GeneratorUserPromptTemplate,
            contextPackage.CommonContextJson,
            validatedPlan.RawJson,
            requiredFqn);

        // 4. Call Generator
        var generatorResponse = await _llmClient.SendPromptAsync(
            contextPackage.GeneratorSystemPrompt,
            genUserPrompt,
            budget,
            ct);

        // 5. Parse Generator Response
        using var genDoc = JsonDocument.Parse(generatorResponse.Content);
        var root = genDoc.RootElement;

        var rawClassName = root.GetProperty("testClassName").GetString()
            ?? throw new FormatException("Generator response missing testClassName");
        var testClassName = rawClassName.Contains('.') ? rawClassName.Split('.').Last().Trim() : rawClassName.Trim();

        var sourceCode = root.GetProperty("sourceCode").GetString()
            ?? throw new FormatException("Generator response missing sourceCode");

        // Clean source code if any markdown remains
        sourceCode = CleanJavaMarkdownFences(sourceCode);

        // Derive authoritative package from source code if declared
        var pkgMatch = Regex.Match(sourceCode, @"^\s*package\s+([a-zA-Z0-9_.]+)\s*;", RegexOptions.Multiline);
        if (pkgMatch.Success)
        {
            pkg = pkgMatch.Groups[1].Value.Trim();
        }

        var mappings = new Dictionary<string, string>();
        if (root.TryGetProperty("mappings", out var mapElem) && mapElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in mapElem.EnumerateArray())
            {
                var sId = m.GetProperty("scenarioId").GetString()!;
                var mName = m.GetProperty("testMethod").GetString()!;
                mappings[sId] = mName;
            }
        }

        ValidateGeneratedContract(root, sourceCode, requiredFqn, validatedPlan, mappings);

        var sourceSha256 = ComputeSha256(sourceCode);

        return new GeneratedTestArtifact(
            TestClassName: testClassName,
            TestPackageName: pkg,
            TestSourceCode: sourceCode,
            SourceSha256: sourceSha256,
            PlanJson: validatedPlan.RawJson,
            PlanHash: planHash,
            ScenarioToMethodMap: mappings,
            PromptTokens: plannerResponse.PromptTokens + generatorResponse.PromptTokens,
            CompletionTokens: plannerResponse.CompletionTokens + generatorResponse.CompletionTokens,
            LatencyMs: plannerResponse.LatencyMs + generatorResponse.LatencyMs
        );
    }

    private static string CleanJavaMarkdownFences(string code)
    {
        var trimmed = code.Trim();
        if (trimmed.StartsWith("```java", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed["```java".Length..].TrimStart();
        }
        else if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            trimmed = trimmed["```".Length..].TrimStart();
        }

        if (trimmed.EndsWith("```", StringComparison.Ordinal))
        {
            trimmed = trimmed[..^3].TrimEnd();
        }

        return trimmed.Trim();
    }

    /// <summary>
    /// Renders the RQ1 generator contract without allowing the finalized plan
    /// to be silently omitted. The legacy token is retained only to make an
    /// old checked-in prompt fail safe while all active prompts use the
    /// FINALIZED form.
    /// </summary>
    internal static string RenderGeneratorPrompt(
        string template,
        string commonContextJson,
        string finalizedPlanJson,
        string requiredTestClassFqn)
    {
        var rendered = template
            .Replace("{{COMMON_CONTEXT_JSON}}", commonContextJson)
            .Replace("{{FINALIZED_SCENARIO_PLAN_JSON}}", finalizedPlanJson)
            .Replace("{{SCENARIO_PLAN_JSON}}", finalizedPlanJson)
            .Replace("{{REQUIRED_TEST_CLASS_FQN}}", requiredTestClassFqn);

        if (Regex.IsMatch(rendered, @"\{\{[A-Z0-9_]+\}\}"))
        {
            throw new FormatException("RQ1 generator prompt contains an unresolved template token.");
        }

        if (!rendered.Contains(finalizedPlanJson, StringComparison.Ordinal))
        {
            throw new FormatException("RQ1 generator prompt omitted the finalized scenario plan.");
        }

        return rendered;
    }

    private static void ValidateGeneratedContract(
        JsonElement root,
        string sourceCode,
        string requiredTestClassFqn,
        ValidatedScenarioPlan finalizedPlan,
        IReadOnlyDictionary<string, string> mappings)
    {
        if (!root.TryGetProperty("declaredTestMethods", out var declaredMethodsElement)
            || declaredMethodsElement.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("Generator response missing declaredTestMethods required by the RQ1 contract.");
        }

        var declaredMethods = declaredMethodsElement
            .EnumerateArray()
            .Select(element => element.GetString()?.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);

        var expectedScenarioIds = finalizedPlan.Scenarios
            .Select(scenario => scenario.ScenarioId)
            .ToHashSet(StringComparer.Ordinal);

        if (!expectedScenarioIds.SetEquals(mappings.Keys))
        {
            throw new FormatException("Generator mappings must cover every finalized scenario exactly once and no others.");
        }

        if (declaredMethods.Count != mappings.Count || !declaredMethods.SetEquals(mappings.Values))
        {
            throw new FormatException("Generator declaredTestMethods and scenario mappings must form a bijection.");
        }

        var className = requiredTestClassFqn.Split('.').Last();
        if (!Regex.IsMatch(sourceCode, $@"\bclass\s+{Regex.Escape(className)}\b", RegexOptions.CultureInvariant))
        {
            throw new FormatException($"Generator source does not declare required test class '{requiredTestClassFqn}'.");
        }

        foreach (var methodName in declaredMethods)
        {
            if (!Regex.IsMatch(sourceCode, $@"\b{Regex.Escape(methodName)}\s*\(", RegexOptions.CultureInvariant))
            {
                throw new FormatException($"Generator source omits declared test method '{methodName}'.");
            }
        }
    }

    private static string ComputeSha256(string content)
    {
        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(content);
        return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
    }
}
