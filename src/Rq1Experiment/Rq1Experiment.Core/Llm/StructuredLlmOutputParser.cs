using System.Text.Json;
using System.Text.RegularExpressions;
using Rq1Experiment.Contracts.Generation;
using Rq1Experiment.Contracts.Targets;

namespace Rq1Experiment.Core.Llm;

public sealed record DirectOutputParseResult(
    bool IsValid,
    DirectGenerationOutput? Output,
    string? FailureCode,
    string NormalizedAssistantContent);

public sealed record ScenarioPlanParseResult(
    bool IsValid, ScenarioPlan? Plan, string? FailureCode, string NormalizedAssistantContent);

public sealed record PlannedOutputParseResult(
    bool IsValid, PlannedGenerationOutput? Output, string? FailureCode, string NormalizedAssistantContent);

public static partial class StructuredLlmOutputParser
{
    private static readonly HashSet<string> AllowedTechniques =
    ["PREDICATE_PARTITION", "BOUNDARY_VALUE", "BRANCH_OUTCOME", "EXCEPTION_PATH"];
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false
    };

    public static DirectOutputParseResult ParseDirect(
        string? response,
        int maximumTestMethods,
        string? requiredTestApi = null,
        string? focalMethodSignature = null,
        IReadOnlyCollection<string>? focalMethodOverloadSignatures = null,
        string? focalClass = null,
        string? expectedTestClassName = null)
    {
        if (string.IsNullOrWhiteSpace(response)) return Failure("EMPTY_RESPONSE", response ?? string.Empty);
        var normalized = StripSingleFence(response.Trim(), "json");
        try
        {
            using var document = JsonDocument.Parse(normalized);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return Failure("ROOT_NOT_OBJECT", normalized);
            var names = document.RootElement.EnumerateObject().Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
            if (!names.SetEquals(["testClassName", "sourceCode", "declaredTestMethods"]))
                return Failure("STRICT_SCHEMA_PROPERTY_VIOLATION", normalized);
            var output = JsonSerializer.Deserialize<DirectGenerationOutput>(normalized, Options);
            if (output is null || string.IsNullOrWhiteSpace(output.TestClassName)
                               || string.IsNullOrWhiteSpace(output.SourceCode)
                               || output.DeclaredTestMethods is null)
                return Failure("REQUIRED_VALUE_MISSING", normalized);
            if (!QualifiedJavaNameRegex().IsMatch(output.TestClassName))
                return Failure("INVALID_TEST_CLASS_NAME", normalized);
            output.SourceCode = StripSingleFence(output.SourceCode.Trim(), "java");
            var identityFailure = ValidateGeneratedTestIdentity(
                output.TestClassName, output.SourceCode, focalClass, expectedTestClassName);
            if (identityFailure is not null) return Failure(identityFailure, normalized);
            var testApiFailure = ValidateTestApi(output.SourceCode, requiredTestApi);
            if (testApiFailure is not null) return Failure(testApiFailure, normalized);
            if (HasAmbiguousBareNullInvocation(
                    output.SourceCode, focalMethodSignature, focalMethodOverloadSignatures))
                return Failure("AMBIGUOUS_BARE_NULL_OVERLOAD", normalized);
            if (output.DeclaredTestMethods.Any(string.IsNullOrWhiteSpace)
                || output.DeclaredTestMethods.Any(name => !JavaIdentifierRegex().IsMatch(name))
                || output.DeclaredTestMethods.Distinct(StringComparer.Ordinal).Count() != output.DeclaredTestMethods.Count)
                return Failure("INVALID_DECLARED_TEST_METHODS", normalized);
            if (output.DeclaredTestMethods.Count > maximumTestMethods)
                return Failure("MAX_TEST_METHODS_EXCEEDED", normalized);
            var annotationCount = TestAnnotationRegex().Matches(output.SourceCode).Count;
            if (annotationCount != output.DeclaredTestMethods.Count)
                return Failure("DECLARED_ACTUAL_TEST_COUNT_MISMATCH", normalized);
            return new DirectOutputParseResult(true, output, null, normalized);
        }
        catch (JsonException)
        {
            return Failure("INVALID_JSON", normalized);
        }
    }

    public static ScenarioPlanParseResult ParseScenarioPlan(
        string? response,
        string expectedSubjectId,
        IReadOnlySet<string> validTargetIds,
        MethodControlFlowGraph controlFlowGraph,
        int maximumScenarios)
    {
        if (string.IsNullOrWhiteSpace(response)) return PlanFailure("EMPTY_RESPONSE", response ?? string.Empty);
        var normalized = StripSingleFence(response.Trim(), "json");
        try
        {
            using var document = JsonDocument.Parse(normalized);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !ExactProperties(root, "subjectId", "scenarios"))
                return PlanFailure("STRICT_SCHEMA_PROPERTY_VIOLATION", normalized);
            if (root.GetProperty("scenarios").ValueKind != JsonValueKind.Array)
                return PlanFailure("SCENARIOS_NOT_ARRAY", normalized);
            foreach (var scenario in root.GetProperty("scenarios").EnumerateArray())
            {
                if (scenario.ValueKind != JsonValueKind.Object || !ExactProperties(scenario,
                        "scenarioId", "technique", "preconditions", "concreteInput", "expectedObservation",
                        "oracleProvenance", "targetIds", "cfgPathNodeIds"))
                    return PlanFailure("STRICT_SCENARIO_SCHEMA_VIOLATION", normalized);
                var provenance = scenario.GetProperty("oracleProvenance");
                if (provenance.ValueKind != JsonValueKind.Object
                    || !ExactProperties(provenance, "type", "sourceLines"))
                    return PlanFailure("STRICT_PROVENANCE_SCHEMA_VIOLATION", normalized);
            }
            var plan = JsonSerializer.Deserialize<ScenarioPlan>(normalized, Options);
            if (plan is null || plan.SubjectId != expectedSubjectId || plan.Scenarios is null || plan.Scenarios.Count == 0)
                return PlanFailure("SUBJECT_OR_REQUIRED_VALUE_INVALID", normalized);
            if (plan.Scenarios.Count > maximumScenarios)
                return PlanFailure("MAX_SCENARIOS_EXCEEDED", normalized);
            for (var index = 0; index < plan.Scenarios.Count; index++)
            {
                var scenario = plan.Scenarios[index];
                if (scenario.ScenarioId != $"S{index + 1:00}")
                    return PlanFailure("UNSTABLE_SCENARIO_ID", normalized);
                if (!AllowedTechniques.Contains(scenario.Technique))
                    return PlanFailure("UNKNOWN_TECHNIQUE", normalized);
                if (string.IsNullOrWhiteSpace(scenario.ConcreteInput)
                    || string.IsNullOrWhiteSpace(scenario.ExpectedObservation)
                    || scenario.Preconditions is null || scenario.Preconditions.Any(string.IsNullOrWhiteSpace)
                    || scenario.OracleProvenance is null
                    || scenario.OracleProvenance.Type != "IMPLEMENTATION_DERIVED"
                    || scenario.OracleProvenance.SourceLines is null
                    || scenario.OracleProvenance.SourceLines.Count == 0
                    || scenario.OracleProvenance.SourceLines.Any(line => line <= 0))
                    return PlanFailure("SCENARIO_REQUIRED_VALUE_INVALID", normalized);
                if (scenario.TargetIds is null || scenario.TargetIds.Count == 0
                    || scenario.TargetIds.Distinct(StringComparer.Ordinal).Count() != scenario.TargetIds.Count
                    || scenario.TargetIds.Any(target => !validTargetIds.Contains(target)))
                    return PlanFailure("UNKNOWN_OR_INVALID_TARGET_ID", normalized);
                if (!IsValidCfgPath(scenario.CfgPathNodeIds, controlFlowGraph))
                    return PlanFailure("INVALID_CFG_PATH", normalized);
            }
            return new ScenarioPlanParseResult(true, plan, null, normalized);
        }
        catch (JsonException) { return PlanFailure("INVALID_JSON", normalized); }
    }

    public static PlannedOutputParseResult ParsePlannedOutput(
        string? response,
        ScenarioPlan plan,
        int maximumTestMethods,
        string? requiredTestApi = null,
        string? focalMethodSignature = null,
        IReadOnlyCollection<string>? focalMethodOverloadSignatures = null,
        string? focalClass = null,
        string? expectedTestClassName = null)
    {
        if (string.IsNullOrWhiteSpace(response)) return PlannedFailure("EMPTY_RESPONSE", response ?? string.Empty);
        var normalized = StripSingleFence(response.Trim(), "json");
        try
        {
            using var document = JsonDocument.Parse(normalized);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !ExactProperties(root,
                    "testClassName", "sourceCode", "declaredTestMethods", "mappings"))
                return PlannedFailure("STRICT_SCHEMA_PROPERTY_VIOLATION", normalized);
            if (root.GetProperty("mappings").ValueKind != JsonValueKind.Array)
                return PlannedFailure("MAPPINGS_NOT_ARRAY", normalized);
            foreach (var mapping in root.GetProperty("mappings").EnumerateArray())
                if (mapping.ValueKind != JsonValueKind.Object
                    || !ExactProperties(mapping, "scenarioId", "testClass", "testMethod"))
                    return PlannedFailure("STRICT_MAPPING_SCHEMA_VIOLATION", normalized);
            var output = JsonSerializer.Deserialize<PlannedGenerationOutput>(normalized, Options);
            if (output is null || string.IsNullOrWhiteSpace(output.TestClassName)
                               || !QualifiedJavaNameRegex().IsMatch(output.TestClassName)
                               || string.IsNullOrWhiteSpace(output.SourceCode)
                               || output.DeclaredTestMethods is null || output.Mappings is null)
                return PlannedFailure("REQUIRED_VALUE_MISSING", normalized);
            output.SourceCode = StripSingleFence(output.SourceCode.Trim(), "java");
            var identityFailure = ValidateGeneratedTestIdentity(
                output.TestClassName, output.SourceCode, focalClass, expectedTestClassName);
            if (identityFailure is not null) return PlannedFailure(identityFailure, normalized);
            var testApiFailure = ValidateTestApi(output.SourceCode, requiredTestApi);
            if (testApiFailure is not null) return PlannedFailure(testApiFailure, normalized);
            if (HasAmbiguousBareNullInvocation(
                    output.SourceCode, focalMethodSignature, focalMethodOverloadSignatures))
                return PlannedFailure("AMBIGUOUS_BARE_NULL_OVERLOAD", normalized);
            if (ParameterizedAnnotationRegex().IsMatch(output.SourceCode))
                return PlannedFailure("PARAMETERIZED_TEST_FORBIDDEN", normalized);
            if (output.DeclaredTestMethods.Count > maximumTestMethods
                || output.DeclaredTestMethods.Count != plan.Scenarios.Count
                || output.Mappings.Count != plan.Scenarios.Count)
                return PlannedFailure("SCENARIO_TEST_COUNT_MISMATCH", normalized);
            if (TestAnnotationRegex().Matches(output.SourceCode).Count != output.DeclaredTestMethods.Count)
                return PlannedFailure("DECLARED_ACTUAL_TEST_COUNT_MISMATCH", normalized);
            var scenarioIds = plan.Scenarios.Select(item => item.ScenarioId).ToHashSet(StringComparer.Ordinal);
            var methodNames = output.DeclaredTestMethods.ToHashSet(StringComparer.Ordinal);
            if (methodNames.Count != output.DeclaredTestMethods.Count
                || output.DeclaredTestMethods.Any(name => !JavaIdentifierRegex().IsMatch(name))
                || output.DeclaredTestMethods.Any(name => !Regex.IsMatch(output.SourceCode,
                    @"\bvoid\s+" + Regex.Escape(name) + @"\s*\(\s*\)", RegexOptions.CultureInvariant)))
                return PlannedFailure("INVALID_DECLARED_TEST_METHODS", normalized);
            if (output.Mappings.Select(item => item.ScenarioId).ToHashSet(StringComparer.Ordinal).Count != output.Mappings.Count
                || output.Mappings.Select(item => item.TestMethod).ToHashSet(StringComparer.Ordinal).Count != output.Mappings.Count
                || output.Mappings.Any(mapping => !scenarioIds.Contains(mapping.ScenarioId)
                                                  || !methodNames.Contains(mapping.TestMethod)
                                                  || mapping.TestClass != output.TestClassName
                                                  || !mapping.TestMethod.Contains(mapping.ScenarioId, StringComparison.Ordinal)))
                return PlannedFailure("BIJECTION_VIOLATION", normalized);
            if (!output.Mappings.Select(item => item.ScenarioId).ToHashSet(StringComparer.Ordinal).SetEquals(scenarioIds)
                || !output.Mappings.Select(item => item.TestMethod).ToHashSet(StringComparer.Ordinal).SetEquals(methodNames))
                return PlannedFailure("BIJECTION_VIOLATION", normalized);
            return new PlannedOutputParseResult(true, output, null, normalized);
        }
        catch (JsonException) { return PlannedFailure("INVALID_JSON", normalized); }
    }

    public static string StripSingleFence(string value, string language)
    {
        var prefix = "```" + language;
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && value.EndsWith("```", StringComparison.Ordinal))
            return value[prefix.Length..^3].Trim();
        return value;
    }

    private static DirectOutputParseResult Failure(string code, string content) =>
        new(false, null, code, content);
    private static ScenarioPlanParseResult PlanFailure(string code, string content) =>
        new(false, null, code, content);
    private static PlannedOutputParseResult PlannedFailure(string code, string content) =>
        new(false, null, code, content);
    private static bool ExactProperties(JsonElement element, params string[] expected) =>
        element.EnumerateObject().Select(item => item.Name).ToHashSet(StringComparer.Ordinal).SetEquals(expected);

    private static bool IsValidCfgPath(
        IReadOnlyList<string>? path,
        MethodControlFlowGraph graph)
    {
        if (path is null || path.Count < 2 || graph is null
            || string.IsNullOrWhiteSpace(graph.EntryNodeId)
            || graph.ExitNodeIds is null || graph.Nodes is null || graph.Edges is null
            || path[0] != graph.EntryNodeId
            || !graph.ExitNodeIds.Contains(path[^1], StringComparer.Ordinal))
            return false;
        var nodeIds = graph.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        if (path.Any(nodeId => !nodeIds.Contains(nodeId))) return false;
        var transitions = graph.Edges
            .Select(edge => (edge.From, edge.To))
            .ToHashSet();
        return path.Zip(path.Skip(1), (from, to) => (from, to))
            .All(transitions.Contains);
    }

    private static string? ValidateTestApi(string sourceCode, string? requiredTestApi)
    {
        if (!string.Equals(requiredTestApi, "JUNIT_JUPITER", StringComparison.OrdinalIgnoreCase))
            return null;
        if (Junit4ImportRegex().IsMatch(sourceCode) || Junit4AnnotationRegex().IsMatch(sourceCode))
            return "JUNIT4_FORBIDDEN";
        return JupiterTestApiRegex().IsMatch(sourceCode) ? null : "JUNIT_JUPITER_REQUIRED";
    }

    private static string? ValidateGeneratedTestIdentity(
        string testClassName,
        string sourceCode,
        string? focalClass,
        string? expectedTestClassName)
    {
        if (!string.IsNullOrWhiteSpace(focalClass)
            && testClassName.Equals(focalClass, StringComparison.Ordinal))
            return "GENERATED_TEST_FQN_COLLIDES_WITH_FOCAL_CLASS";
        if (!testClassName.Split('.').Last().EndsWith("Test", StringComparison.Ordinal))
            return "GENERATED_TEST_CLASS_SUFFIX_REQUIRED";
        if (!string.IsNullOrWhiteSpace(expectedTestClassName)
            && !testClassName.Equals(expectedTestClassName, StringComparison.Ordinal))
            return "UNEXPECTED_TEST_CLASS_NAME";

        var package = PackageDeclarationRegex().Match(sourceCode) is { Success: true } packageMatch
            ? packageMatch.Groups[1].Value
            : string.Empty;
        var typeMatch = TopLevelTestTypeRegex().Match(sourceCode);
        if (!typeMatch.Success) return "TEST_SOURCE_TOP_LEVEL_TYPE_MISSING";
        var declaredFqn = string.IsNullOrWhiteSpace(package)
            ? typeMatch.Groups[1].Value
            : package + "." + typeMatch.Groups[1].Value;
        return declaredFqn.Equals(testClassName, StringComparison.Ordinal)
            ? null
            : "TEST_SOURCE_CLASS_IDENTITY_MISMATCH";
    }

    private static bool HasAmbiguousBareNullInvocation(
        string sourceCode,
        string? focalMethodSignature,
        IReadOnlyCollection<string>? overloadSignatures)
    {
        if (string.IsNullOrWhiteSpace(focalMethodSignature)
            || overloadSignatures is null
            || overloadSignatures.Distinct(StringComparer.Ordinal).Count() < 2)
            return false;
        var open = focalMethodSignature.IndexOf('(');
        if (open <= 0) return false;
        var methodName = focalMethodSignature[..open].Trim();
        if (!JavaIdentifierRegex().IsMatch(methodName)) return false;
        return Regex.IsMatch(sourceCode,
            @"\b" + Regex.Escape(methodName) + @"\s*\(\s*null\s*\)",
            RegexOptions.CultureInvariant);
    }

    [GeneratedRegex(@"(?m)^\s*@(?:org\.junit\.jupiter\.api\.)?Test\b")]
    private static partial Regex TestAnnotationRegex();

    [GeneratedRegex(@"(?m)^\s*import\s+org\.junit\.Test\s*;")]
    private static partial Regex Junit4ImportRegex();

    [GeneratedRegex(@"(?m)^\s*@org\.junit\.Test\b")]
    private static partial Regex Junit4AnnotationRegex();

    [GeneratedRegex(@"(?m)^\s*import\s+org\.junit\.jupiter\.api\.(?:Test|\*)\s*;|^\s*@org\.junit\.jupiter\.api\.Test\b")]
    private static partial Regex JupiterTestApiRegex();

    [GeneratedRegex(@"^[A-Za-z_$][A-Za-z0-9_$]*(?:\.[A-Za-z_$][A-Za-z0-9_$]*)*$")]
    private static partial Regex QualifiedJavaNameRegex();

    [GeneratedRegex(@"^[A-Za-z_$][A-Za-z0-9_$]*$")]
    private static partial Regex JavaIdentifierRegex();

    [GeneratedRegex(@"(?m)^\s*package\s+([A-Za-z_$][A-Za-z0-9_$]*(?:\.[A-Za-z_$][A-Za-z0-9_$]*)*)\s*;")]
    private static partial Regex PackageDeclarationRegex();

    [GeneratedRegex(@"(?m)^\s*(?:public\s+)?(?:(?:final|abstract|sealed|non-sealed)\s+)*(?:class|interface|enum|record)\s+([A-Za-z_$][A-Za-z0-9_$]*)\b")]
    private static partial Regex TopLevelTestTypeRegex();

    [GeneratedRegex(@"(?m)^\s*@(?:org\.junit\.jupiter\.(?:api|params)\.)?(?:ParameterizedTest|RepeatedTest|TestFactory|TestTemplate)\b")]
    private static partial Regex ParameterizedAnnotationRegex();
}
