using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FullChain.Contracts;
using FullChain.Core;
using FullChain.Core.Verification;
using FullChain.Infrastructure.LLM;

namespace FullChain.Infrastructure.Rq3;

public sealed record SpecialistFinding(
    string Role,
    string Status,
    string Cause,
    string Rationale,
    string ExecutionStatus = "OK",
    int InputTokens = 0,
    int OutputTokens = 0,
    double LatencyMs = 0
);

public sealed record VerificationOutcome(
    string CaseId,
    string EnvelopeHash,
    string Decision,
    string Cause,
    string Route,
    bool Abstained,
    string Rationale,
    VerificationVerdict Verdict,
    bool CanLoopToRepair,
    double TotalLatencyMs,
    int TotalTokens,
    IReadOnlyList<SpecialistFinding> Findings
);

public sealed class Rq3VerifierAdapter : IVerifierAdapter
{
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> DetectedCausesByRole =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["CONFORMANCE"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "PLAN_TEST_MISMATCH", "ASSERTION_ERROR"
            },
            ["RUNTIME"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "COMPILATION_ERROR", "SETUP_OR_RUNTIME_ERROR", "SHARED_INFRASTRUCTURE_ERROR"
            },
            ["COVERAGE_MUTATION"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "TARGET_NOT_REACHED", "WEAK_ORACLE"
            }
        };

    private static readonly string[] PriorityOrder =
    [
        "COMPILATION_ERROR",
        "SHARED_INFRASTRUCTURE_ERROR",
        "SETUP_OR_RUNTIME_ERROR",
        "TARGET_NOT_REACHED",
        "WEAK_ORACLE",
        "PLAN_TEST_MISMATCH",
        "ASSERTION_ERROR"
    ];

    private readonly ILlmClient _llmClient;

    public Rq3VerifierAdapter(ILlmClient llmClient)
    {
        _llmClient = llmClient ?? throw new ArgumentNullException(nameof(llmClient));
    }

    public async Task<VerificationOutcome> VerifyEnvelopeAsync(
        MaterializedEnvelope envelope,
        int currentVerificationPass,
        int remainingRepairRounds,
        IGlobalBudgetGovernor budgetGovernor,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var stopwatch = Stopwatch.StartNew();

        // Run the 3 specialists against the common blinded envelope
        var conformanceFinding = await RunSpecialistAsync(
            role: "CONFORMANCE",
            systemPrompt: "You are the RQ3 conformance specialist. Compare exact test source with the declared focal mapping, inputs, and expected observation. Return exactly one JSON object {\"status\":\"DETECTED\"|\"CLEAR\"|\"INSUFFICIENT\", \"cause\":\"PLAN_TEST_MISMATCH\"|\"ASSERTION_ERROR\"|\"NONE\"|\"INSUFFICIENT_EVIDENCE\", \"rationale\":\"...\"}; no Markdown. status is DETECTED, CLEAR, or INSUFFICIENT. CLEAR uses NONE. INSUFFICIENT uses INSUFFICIENT_EVIDENCE. Never return a system decision or repair route.",
            envelopeJson: envelope.EnvelopeJson,
            budgetGovernor: budgetGovernor,
            ct: ct);

        var runtimeFinding = await RunSpecialistAsync(
            role: "RUNTIME",
            systemPrompt: "You are the RQ3 runtime specialist. Classify compiler, selected-test runtime, and shared-infrastructure evidence only. Return exactly one JSON object {\"status\":\"DETECTED\"|\"CLEAR\"|\"INSUFFICIENT\", \"cause\":\"COMPILATION_ERROR\"|\"SETUP_OR_RUNTIME_ERROR\"|\"SHARED_INFRASTRUCTURE_ERROR\"|\"NONE\"|\"INSUFFICIENT_EVIDENCE\", \"rationale\":\"...\"}; no Markdown. status is DETECTED, CLEAR, or INSUFFICIENT. CLEAR uses NONE. INSUFFICIENT uses INSUFFICIENT_EVIDENCE. Never return a system decision or repair route.",
            envelopeJson: envelope.EnvelopeJson,
            budgetGovernor: budgetGovernor,
            ct: ct);

        var coverageMutationFinding = await RunSpecialistAsync(
            role: "COVERAGE_MUTATION",
            systemPrompt: "You are the RQ3 coverage/mutation specialist. Judge only the evidence envelope. First inspect public_case.scenario_plan_scope. If scope is PLANNED_TARGET_IDS_ONLY, evaluate only targetIds declared by those planned scenarios; omitted inventory targets and diagnostic-only targets are not failures. If scope is FULL_FOCAL_INVENTORY, evaluate all OFFICIAL focal obligations. Use tool_evidence.target_obligation_coverage: a planned or in-scope target with satisfied=false is TARGET_NOT_REACHED. Do not infer target failure from project-wide coverage alone. If mutation_evidence.available=false, return INSUFFICIENT/INSUFFICIENT_EVIDENCE. Otherwise use the focal mutation transformations: a surviving focal mutation may be WEAK_ORACLE; all killed focal mutations and all in-scope obligations satisfied are CLEAR. Return exactly one JSON object {\"status\":\"DETECTED\"|\"CLEAR\"|\"INSUFFICIENT\", \"cause\":\"TARGET_NOT_REACHED\"|\"WEAK_ORACLE\"|\"NONE\"|\"INSUFFICIENT_EVIDENCE\", \"rationale\":\"...\"}; no Markdown. status is DETECTED, CLEAR, or INSUFFICIENT. CLEAR uses NONE. INSUFFICIENT uses INSUFFICIENT_EVIDENCE. Never return a system decision or repair route.",
            envelopeJson: envelope.EnvelopeJson,
            budgetGovernor: budgetGovernor,
            ct: ct);

        var findings = new List<SpecialistFinding> { conformanceFinding, runtimeFinding, coverageMutationFinding };
        stopwatch.Stop();

        // Fixed-priority deterministic aggregation
        string decision;
        string cause;
        string route;
        string rationale;
        bool abstained = false;

        var detectedFinding = (SpecialistFinding?)null;
        foreach (var p in PriorityOrder)
        {
            var match = findings.FirstOrDefault(f => f.Status == "DETECTED" && f.Cause == p && f.ExecutionStatus == "OK");
            if (match != null)
            {
                detectedFinding = match;
                break;
            }
        }

        if (detectedFinding != null)
        {
            decision = VerificationVerdicts.RepairRequired;
            cause = detectedFinding.Cause;
            route = RouteFor(cause);
            rationale = $"Specialist {detectedFinding.Role} detected {cause}: {detectedFinding.Rationale}";
        }
        else if (findings.Count == 3 && findings.All(f => f.Status == "CLEAR" && f.ExecutionStatus == "OK"))
        {
            decision = VerificationVerdicts.Verified;
            cause = "NONE";
            route = VerificationRoutes.None;
            rationale = "All three specialists returned CLEAR.";
        }
        else
        {
            decision = VerificationVerdicts.Abstain;
            cause = "INSUFFICIENT_EVIDENCE";
            route = VerificationRoutes.RequestToolEvidence;
            abstained = true;
            rationale = "Specialist findings were insufficient or contained execution anomalies.";
        }

        // Evaluate against route policy
        var policyDecision = VerificationRoutePolicy.EvaluateVerdict(
            decision: decision,
            cause: cause,
            route: route,
            rationale: rationale,
            currentVerificationPass: currentVerificationPass,
            remainingRepairBudgetRounds: remainingRepairRounds);

        var totalTokens = findings.Sum(f => f.InputTokens + f.OutputTokens);

        return new VerificationOutcome(
            CaseId: envelope.CaseId,
            EnvelopeHash: envelope.EnvelopeSha256,
            Decision: decision,
            Cause: cause,
            Route: route,
            Abstained: abstained,
            Rationale: rationale,
            Verdict: policyDecision.Verdict,
            CanLoopToRepair: policyDecision.CanLoopToRepair,
            TotalLatencyMs: stopwatch.Elapsed.TotalMilliseconds,
            TotalTokens: totalTokens,
            Findings: findings
        );
    }

    private async Task<SpecialistFinding> RunSpecialistAsync(
        string role,
        string systemPrompt,
        string envelopeJson,
        IGlobalBudgetGovernor budgetGovernor,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var response = await _llmClient.SendPromptAsync(systemPrompt, envelopeJson, budgetGovernor, ct);
            sw.Stop();

            // Parse response
            var json = response.Content.Trim();
            if (json.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
            {
                json = json[7..];
            }
            if (json.StartsWith("```", StringComparison.OrdinalIgnoreCase))
            {
                json = json[3..];
            }
            if (json.EndsWith("```", StringComparison.OrdinalIgnoreCase))
            {
                json = json[..^3];
            }
            json = json.Trim();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var status = root.TryGetProperty("status", out var s) ? s.GetString() ?? "INSUFFICIENT" : "INSUFFICIENT";
            var cause = root.TryGetProperty("cause", out var c) ? c.GetString() ?? "INSUFFICIENT_EVIDENCE" : "INSUFFICIENT_EVIDENCE";
            var rationale = root.TryGetProperty("rationale", out var r) ? r.GetString() ?? "" : "";

            return NormalizeFinding(
                role,
                status,
                cause,
                rationale,
                response.PromptTokens,
                response.CompletionTokens,
                sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new SpecialistFinding(
                Role: role,
                Status: "INSUFFICIENT",
                Cause: "INSUFFICIENT_EVIDENCE",
                Rationale: $"Specialist failed with exception: {ex.Message}",
                ExecutionStatus: "ERROR",
                LatencyMs: sw.Elapsed.TotalMilliseconds
            );
        }
    }

    private static SpecialistFinding NormalizeFinding(
        string role,
        string status,
        string cause,
        string rationale,
        int inputTokens,
        int outputTokens,
        double latencyMs)
    {
        var normalizedStatus = status.Trim().ToUpperInvariant();
        var normalizedCause = cause.Trim().ToUpperInvariant();
        var isValid = normalizedStatus switch
        {
            "CLEAR" => normalizedCause == "NONE",
            "INSUFFICIENT" => normalizedCause == "INSUFFICIENT_EVIDENCE",
            "DETECTED" => DetectedCausesByRole.TryGetValue(role, out var allowedCauses)
                            && allowedCauses.Contains(normalizedCause),
            _ => false
        };

        if (!isValid)
        {
            return new SpecialistFinding(
                Role: role,
                Status: "INSUFFICIENT",
                Cause: "INSUFFICIENT_EVIDENCE",
                Rationale: $"Invalid specialist schema: status='{normalizedStatus}', cause='{normalizedCause}'. {rationale}".Trim(),
                ExecutionStatus: "OK",
                InputTokens: inputTokens,
                OutputTokens: outputTokens,
                LatencyMs: latencyMs);
        }

        return new SpecialistFinding(
            Role: role,
            Status: normalizedStatus,
            Cause: normalizedCause,
            Rationale: rationale,
            ExecutionStatus: "OK",
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            LatencyMs: latencyMs);
    }

    public static string RouteFor(string cause) => cause switch
    {
        "COMPILATION_ERROR" or "SETUP_OR_RUNTIME_ERROR" or "ASSERTION_ERROR" or "WEAK_ORACLE" => VerificationRoutes.LocalTestRepair,
        "PLAN_TEST_MISMATCH" or "TARGET_NOT_REACHED" => VerificationRoutes.RegenerateTest,
        "SHARED_INFRASTRUCTURE_ERROR" => VerificationRoutes.SharedInfrastructureRepair,
        _ => VerificationRoutes.RequestToolEvidence
    };

    public async Task<FullChain.Contracts.VerificationDecision> VerifyBlindedEnvelopeAsync(
        VerificationRequest request,
        IGlobalBudgetGovernor budget,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.CurrentVerificationPass <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "CurrentVerificationPass must be positive.");
        }
        if (request.RemainingRepairRounds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "RemainingRepairRounds cannot be negative.");
        }

        var materialized = VerificationEnvelopeFactory.CreateBlindedEnvelope(request);
        var outcome = await VerifyEnvelopeAsync(
            materialized,
            request.CurrentVerificationPass,
            request.RemainingRepairRounds,
            budget,
            ct);

        var findings = outcome.Findings.Select(f => new FullChain.Contracts.SpecialistFinding(
            SpecialistId: f.Role,
            Verdict: f.Status switch { "DETECTED" => "FAIL", "CLEAR" => "PASS", _ => "ABSTAIN" },
            Confidence: 1.0,
            DetailedReason: $"{f.Cause}: {f.Rationale}"
        )).ToList();

        string aggregateVerdict = outcome.Decision switch
        {
            VerificationVerdicts.Verified => VerificationVerdicts.Verified,
            VerificationVerdicts.RepairRequired => VerificationVerdicts.RepairRequired,
            VerificationVerdicts.Abstain => VerificationVerdicts.Abstain,
            _ => VerificationVerdicts.Unverified
        };

        return new FullChain.Contracts.VerificationDecision(
            AggregateVerdict: aggregateVerdict,
            ActionableRoute: outcome.Route,
            SpecialistFindings: findings,
            EnvelopeSha256: outcome.EnvelopeHash,
            LatencyMs: (long)outcome.TotalLatencyMs,
            Timestamp: DateTime.UtcNow.ToString("o")
        );
    }
}
