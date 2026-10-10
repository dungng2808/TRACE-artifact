using System;
using System.Collections.Generic;
using System.Text.Json;

namespace FullChain.Core.Planning;

public sealed record ValidatedScenarioPlan(
    string SubjectId,
    IReadOnlyList<PlannedScenario> Scenarios,
    string RawJson
);

public sealed record PlannedScenario(
    string ScenarioId,
    string Technique,
    IReadOnlyList<string> Preconditions,
    string ConcreteInput,
    string ExpectedObservation,
    IReadOnlyList<string> TargetIds
);

public static class ScenarioPlanValidator
{
    public static ValidatedScenarioPlan Validate(string planJson, string expectedSubjectId, int maxScenarios = 3)
    {
        if (string.IsNullOrWhiteSpace(planJson))
        {
            throw new ArgumentException("Scenario plan JSON is empty.", nameof(planJson));
        }

        var trimmed = planJson.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            throw new FormatException("Plan JSON contains markdown code fences, which violates the strict JSON contract.");
        }

        using var doc = JsonDocument.Parse(trimmed);
        var root = doc.RootElement;

        if (!root.TryGetProperty("scenarios", out var scenariosElem) || scenariosElem.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("Plan JSON must contain a top-level 'scenarios' array.");
        }

        var scenarios = new List<PlannedScenario>();
        foreach (var sc in scenariosElem.EnumerateArray())
        {
            var id = sc.GetProperty("scenarioId").GetString() ?? throw new FormatException("Scenario missing scenarioId.");
            var tech = sc.TryGetProperty("technique", out var tElem) ? tElem.GetString() ?? "PREDICATE_PARTITION" : "PREDICATE_PARTITION";
            var obs = sc.GetProperty("expectedObservation").GetString() ?? throw new FormatException("Scenario missing expectedObservation.");
            var input = sc.TryGetProperty("concreteInput", out var inElem) ? inElem.GetString() ?? "" : "";

            var preconds = new List<string>();
            if (sc.TryGetProperty("preconditions", out var preElem) && preElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in preElem.EnumerateArray())
                {
                    if (p.GetString() is { } ps) preconds.Add(ps);
                }
            }

            var targets = new List<string>();
            if (sc.TryGetProperty("targetIds", out var tgtElem) && tgtElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var tg in tgtElem.EnumerateArray())
                {
                    if (tg.GetString() is { } ts) targets.Add(ts);
                }
            }

            scenarios.Add(new PlannedScenario(id, tech, preconds, input, obs, targets));
        }

        if (scenarios.Count == 0)
        {
            throw new InvalidOperationException("Plan contains zero scenarios.");
        }

        if (scenarios.Count > maxScenarios)
        {
            throw new InvalidOperationException($"Plan exceeds maximum scenario limit ({scenarios.Count} > {maxScenarios}).");
        }

        return new ValidatedScenarioPlan(expectedSubjectId, scenarios, trimmed);
    }
}

