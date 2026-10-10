using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FullChain.Contracts;

namespace FullChain.Infrastructure.Rq3;

public sealed record MaterializedEnvelope(
    string CaseId,
    string EnvelopeJson,
    string EnvelopeSha256,
    string SourceSha256
);

public static class VerificationEnvelopeFactory
{
    public static MaterializedEnvelope CreateBlindedEnvelope(VerificationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Target);
        ArgumentNullException.ThrowIfNull(request.Evaluation);

        if (string.IsNullOrWhiteSpace(request.TestClassFqn))
        {
            throw new ArgumentException("TestClassFqn must be provided for RQ3 verification.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.ArtifactRelativePath))
        {
            throw new ArgumentException("ArtifactRelativePath must be provided for RQ3 verification.", nameof(request));
        }

        var testClassName = request.TestClassFqn.Split('.').Last();
        var sourceHashPrefix = request.Evaluation.TestSourceSha256[..Math.Min(8, request.Evaluation.TestSourceSha256.Length)];
        var caseId = $"CASE-{request.Target.SubjectId}-{sourceHashPrefix}";

        return CreateBlindedEnvelope(
            request.Target,
            caseId,
            testClassName,
            request.Evaluation.TestSourceCode,
            request.ArtifactRelativePath,
            request.Evaluation,
            currentVerificationPass: request.CurrentVerificationPass,
            remainingRepairRounds: request.RemainingRepairRounds,
            scenarioToMethodMap: request.ScenarioToMethodMap,
            planJson: request.PlanJson);
    }

    public static MaterializedEnvelope CreateBlindedEnvelope(
        TargetDescriptor target,
        string caseId,
        string testClassName,
        string testSourceCode,
        string artifactRelativePath,
        CommonEnvelope commonEvaluation,
        string? compileOutput = null,
        string? testRunOutput = null,
        int? currentVerificationPass = null,
        int? remainingRepairRounds = null,
        IReadOnlyDictionary<string, string>? scenarioToMethodMap = null,
        string? planJson = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(testClassName);
        ArgumentNullException.ThrowIfNull(testSourceCode);

        using var sha = SHA256.Create();
        var sourceSha256 = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(testSourceCode))).ToUpperInvariant();
        var plannedScenarioScope = CreateScenarioPlanScope(planJson, scenarioToMethodMap);
        var obligationCoverage = CreateObligationCoverage(commonEvaluation.ObligationCoverage);
        var focalMutations = CreateFocalMutations(commonEvaluation.FocalMutations);

        var envelopeObj = new JsonObject
        {
            ["schema_version"] = "rq3-evidence-envelope-v2",
            ["case_id"] = caseId,
            ["public_case"] = new JsonObject
            {
                ["schema_version"] = "rq3-public-case-v2",
                ["case_id"] = caseId,
                ["split"] = "EVAL",
                ["repository"] = new JsonObject
                {
                    ["id"] = target.ProjectId,
                    ["project"] = target.ProjectId,
                    ["repository_url"] = ""
                },
                ["revision"] = new JsonObject
                {
                    ["revision_spec"] = target.Revision,
                    ["resolved_revision"] = target.Revision
                },
                ["case_origin"] = "CANDIDATE_EVALUATION",
                ["base_test"] = new JsonObject
                {
                    ["test_id"] = testClassName,
                    ["source_kind"] = "SYNTHESIZED",
                    ["artifact_path"] = artifactRelativePath,
                    ["artifact_sha256"] = sourceSha256
                },
                ["focal_mapping"] = new JsonObject
                {
                    ["candidate_class"] = target.FocalClass,
                    ["candidate_method"] = target.FocalMethodSignature,
                    ["status"] = "CONFIRMED"
                },
                ["scenario"] = new JsonObject
                {
                    ["summary"] = $"Target method unit test execution for {target.FocalClass}",
                    ["input_preconditions"] = $"Setup fixtures and invoke {target.FocalMethodSignature}",
                    ["expected_observation"] = "Assertions and state checks pass without error",
                    ["structural_target"] = target.FocalClass
                },
                ["scenario_plan_scope"] = plannedScenarioScope,
                ["status"] = "READY_FOR_EXPLORATORY_V2"
            },
            ["generated_test"] = new JsonObject
            {
                ["language"] = "java",
                ["test_id"] = testClassName,
                ["source_text"] = testSourceCode,
                ["source_sha256"] = sourceSha256,
                ["materialized_from"] = artifactRelativePath
            },
            ["tool_evidence"] = new JsonObject
            {
                ["schema_version"] = "rq3-tool-evidence-v2",
                ["case_id"] = caseId,
                ["repository"] = new JsonObject
                {
                    ["project"] = target.ProjectId,
                    ["revision"] = target.Revision
                },
                ["test_id"] = testClassName,
                ["focal_class"] = target.FocalClass,
                ["tool_runs"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["phase"] = "compile",
                        ["status"] = commonEvaluation.CompileSuccess ? "PASS" : "FAIL",
                        ["exit_code"] = commonEvaluation.CompileSuccess ? 0 : 1,
                        ["output"] = compileOutput ?? (commonEvaluation.CompileSuccess ? "Compilation successful." : "Compilation failed.")
                    },
                    new JsonObject
                    {
                        ["phase"] = "test",
                        ["status"] = commonEvaluation.TestExecutionSuccess ? "PASS" : "FAIL",
                        ["exit_code"] = commonEvaluation.TestExecutionSuccess ? 0 : 1,
                        ["output"] = testRunOutput ?? (commonEvaluation.TestExecutionSuccess ? "All tests passed." : "Test execution failures detected.")
                    }
                },
                ["metrics"] = new JsonObject
                {
                    ["branch_coverage"] = commonEvaluation.BranchCoverage,
                    ["mutation_score"] = commonEvaluation.MutationScore,
                    ["structural_target_satisfaction"] = commonEvaluation.StructuralTargetSatisfaction
                },
                ["target_obligation_coverage"] = obligationCoverage,
                ["mutation_evidence"] = new JsonObject
                {
                    ["available"] = commonEvaluation.MutationEvidenceAvailable,
                    ["focal_mutation_count"] = commonEvaluation.FocalMutations?.Count ?? 0,
                    ["focal_mutations"] = focalMutations
                }
            },
            ["verification_context"] = new JsonObject
            {
                ["current_verification_pass"] = currentVerificationPass,
                ["remaining_repair_rounds"] = remainingRepairRounds
            },
            ["note"] = "No private gold, injection operator, or relevance adjudication is included."
        };

        var json = envelopeObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var envelopeSha256 = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(json))).ToUpperInvariant();

        return new MaterializedEnvelope(caseId, json, envelopeSha256, sourceSha256);
    }

    private static JsonObject CreateScenarioPlanScope(
        string? planJson,
        IReadOnlyDictionary<string, string>? scenarioToMethodMap)
    {
        var testMethodMap = new JsonObject();
        if (scenarioToMethodMap != null)
        {
            foreach (var (scenarioId, methodName) in scenarioToMethodMap.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                testMethodMap[scenarioId] = methodName;
            }
        }

        if (string.IsNullOrWhiteSpace(planJson))
        {
            return new JsonObject
            {
                ["scope"] = "FULL_FOCAL_INVENTORY",
                ["scenario_plan_available"] = false,
                ["scenario_to_test_method"] = testMethodMap
            };
        }

        try
        {
            var parsedPlan = JsonNode.Parse(planJson);
            if (parsedPlan is JsonObject planObject && planObject["scenarios"] is JsonArray)
            {
                return new JsonObject
                {
                    ["scope"] = "PLANNED_TARGET_IDS_ONLY",
                    ["scenario_plan_available"] = true,
                    ["scenario_plan"] = planObject.DeepClone(),
                    ["scenario_to_test_method"] = testMethodMap
                };
            }
        }
        catch (JsonException)
        {
            // A malformed plan is retained as unavailable evidence. The B2
            // specialist will abstain rather than infer an untrusted scope.
        }

        return new JsonObject
        {
            ["scope"] = "FULL_FOCAL_INVENTORY",
            ["scenario_plan_available"] = false,
            ["scenario_to_test_method"] = testMethodMap
        };
    }

    private static JsonArray CreateObligationCoverage(IReadOnlyList<ObligationCoverageEvidence>? evidence)
    {
        var result = new JsonArray();
        foreach (var item in evidence ?? Array.Empty<ObligationCoverageEvidence>())
        {
            result.Add(new JsonObject
            {
                ["target_id"] = item.TargetId,
                ["type"] = item.Type,
                ["line"] = item.Line,
                ["covered_branches"] = item.CoveredBranches,
                ["missed_branches"] = item.MissedBranches,
                ["covered_instructions"] = item.CoveredInstructions,
                ["missed_instructions"] = item.MissedInstructions,
                ["satisfied"] = item.Satisfied
            });
        }
        return result;
    }

    private static JsonArray CreateFocalMutations(IReadOnlyList<MutationEvidence>? mutations)
    {
        var result = new JsonArray();
        foreach (var mutation in mutations ?? Array.Empty<MutationEvidence>())
        {
            result.Add(new JsonObject
            {
                ["line"] = mutation.Line,
                ["mutator"] = mutation.Mutator,
                ["description"] = mutation.Description,
                ["status"] = mutation.Status,
                ["detected"] = mutation.Detected,
                ["killing_test"] = mutation.KillingTest
            });
        }
        return result;
    }

}
