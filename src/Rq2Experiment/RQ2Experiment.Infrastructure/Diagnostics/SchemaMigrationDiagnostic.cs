using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using RQ2Experiment.Core.Domain;
using RQ2Experiment.Infrastructure.Persistence;

namespace RQ2Experiment.Infrastructure.Diagnostics;

internal static class SchemaMigrationDiagnostic
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Run()
    {
        const string legacyJson = """
            {
              "schemaVersion": 6,
              "runId": "11111111-1111-1111-1111-111111111111",
              "taskId": "LEGACY-TASK",
              "scenarioId": "LEGACY-SCENARIO",
              "strategy": "TraceabilityGuidedScenarioLocal",
              "repetition": 2,
              "status": "Success",
              "startedAt": "2026-08-17T10:00:00+00:00",
              "completedAt": "2026-08-17T10:01:00+00:00",
              "iterations": [{
                "iteration": 1,
                "startedAt": "2026-08-17T10:00:10+00:00",
                "result": {
                  "inputTokens": 100,
                  "outputTokens": 20,
                  "totalTokens": 120,
                  "apiCalls": 1,
                  "changedLines": 3,
                  "touchedMethods": 1,
                  "regressedTests": 0,
                  "focalTestPassed": true,
                  "lineCoverage": 0.8,
                  "mutationScore": 0.7,
                  "summary": "legacy iteration",
                  "adequacyVerified": true,
                  "buggyOracleRequired": true,
                  "bugReproduced": true,
                  "coverageStatus": "Succeeded",
                  "mutationStatus": "Succeeded"
                }
              }],
              "totalTokens": 120,
              "totalInputTokens": 100,
              "totalOutputTokens": 20,
              "totalApiCalls": 1,
              "totalChangedLines": 3,
              "regressedTests": 0,
              "finalLineCoverage": 0.8,
              "finalMutationScore": 0.7,
              "stopReason": "verified",
              "buggyOracleRequired": true,
              "finalBugReproduced": true,
              "finalCoverageStatus": "Succeeded",
              "finalMutationStatus": "Succeeded",
              "batchId": "legacy-batch",
              "providerName": "provider",
              "modelName": "model",
              "apiProtocol": "ChatCompletions",
              "configuredTokenBudget": 1000,
              "configuredMaxIterations": 4,
              "scheduleSeed": 42,
              "scheduleIndex": 2,
              "blockId": "LEGACY-TASK::R002",
              "treatmentPosition": 1,
              "expectedBatchRuns": 3,
              "tokenAccountingComplete": true,
              "projectId": "owner/project",
              "datasetId": "dataset",
              "artifactId": "artifact"
            }
            """;

        var migrated = ExperimentRunResultSchemaMigrator.Deserialize(legacyJson, JsonOptions);
        if (migrated.SchemaVersion != 11 ||
            migrated.SourceSchemaVersion != 6 ||
            migrated.TaskId != "LEGACY-TASK" ||
            migrated.TotalTokens != 120 ||
            migrated.Iterations.Single().Result.Summary != "legacy iteration" ||
            migrated.ProjectId != "owner/project" ||
            !migrated.FinalBugReproduced ||
            !migrated.Stages.InferredFromLegacy ||
            migrated.Stages.Compilation.Status != RepairStageStatus.Succeeded ||
            !migrated.PromptAudit.InferredFromLegacy)
        {
            throw new InvalidOperationException("Legacy schema migration lost result evidence.");
        }

        var schema11Native = migrated with
        {
            Provenance = migrated.Provenance with { SourceSchemaVersion = 11 },
            Design = migrated.Design with
            {
                StudyPlan = new StudyPlanEvidence(
                    StudyId: "SCHEMA-DIAGNOSTIC",
                    SamplingPlanSha256: "SAMPLING-HASH",
                    PowerPlanSha256: "POWER-HASH",
                    OutcomePlanSha256: "OUTCOME-HASH",
                    OutcomeContractVersion: "RQ2-OUTCOME-PLAN-1",
                    SuccessNonInferiorityMargin: 0.05,
                    CoverageNonInferiorityMargin: 0.05,
                    MutationNonInferiorityMargin: 0.05,
                    OutcomePlanDeclared: true,
                    OutcomePlanProvisional: false)
            }
        };
        var schema11Json = JsonSerializer.Serialize(schema11Native, JsonOptions);
        using var schema11Document = JsonDocument.Parse(schema11Json);
        if (!schema11Document.RootElement.TryGetProperty("identity", out _) ||
            !schema11Document.RootElement.TryGetProperty("provenance", out _) ||
            schema11Document.RootElement.TryGetProperty("runId", out _) ||
            !schema11Document.RootElement.TryGetProperty("stages", out _) ||
            !schema11Document.RootElement.TryGetProperty("promptAudit", out _))
        {
            throw new InvalidOperationException("Schema 11 did not serialize grouped domain, stage, or prompt-audit evidence.");
        }

        var roundTrip = ExperimentRunResultSchemaMigrator.Deserialize(schema11Json, JsonOptions);
        if (roundTrip.RunId != migrated.RunId ||
            roundTrip.TotalTokens != migrated.TotalTokens ||
            roundTrip.Iterations.Single().Result.LineCoverage != 0.8 ||
            !roundTrip.StudyPlan.OutcomePlanDeclared ||
            roundTrip.StudyPlan.OutcomePlanProvisional)
        {
            throw new InvalidOperationException("Schema-11 round trip changed result evidence.");
        }

        var schema7Node = JsonNode.Parse(schema11Json)?.AsObject()
            ?? throw new InvalidOperationException("Could not create schema-7 upgrade fixture.");
        schema7Node["schemaVersion"] = 7;
        schema7Node.Remove("stages");
        schema7Node.Remove("promptAudit");
        foreach (var iterationNode in schema7Node["iterations"]?.AsArray() ?? [])
        {
            iterationNode?["result"]?.AsObject().Remove("stages");
            iterationNode?["result"]?.AsObject().Remove("promptAudit");
        }

        var upgraded = ExperimentRunResultSchemaMigrator.Deserialize(schema7Node.ToJsonString(JsonOptions), JsonOptions);
        if (upgraded.SchemaVersion != 11 ||
            upgraded.SourceSchemaVersion != 7 ||
            !upgraded.Stages.InferredFromLegacy ||
            upgraded.Stages.Compilation.Status != RepairStageStatus.Succeeded ||
            !upgraded.PromptAudit.InferredFromLegacy)
        {
            throw new InvalidOperationException("Schema-7 stage-evidence upgrade failed.");
        }

        var schema8Node = JsonNode.Parse(schema11Json)?.AsObject()
            ?? throw new InvalidOperationException("Could not create schema-8 upgrade fixture.");
        schema8Node["schemaVersion"] = 8;
        schema8Node.Remove("promptAudit");
        foreach (var iterationNode in schema8Node["iterations"]?.AsArray() ?? [])
        {
            iterationNode?["result"]?.AsObject().Remove("promptAudit");
        }
        var schema8Upgrade = ExperimentRunResultSchemaMigrator.Deserialize(schema8Node.ToJsonString(JsonOptions), JsonOptions);
        if (schema8Upgrade.SchemaVersion != 11 ||
            schema8Upgrade.SourceSchemaVersion != 8 ||
            !schema8Upgrade.PromptAudit.InferredFromLegacy)
        {
            throw new InvalidOperationException("Schema-8 prompt-audit upgrade failed.");
        }

        var schema9Node = JsonNode.Parse(schema11Json)?.AsObject()
            ?? throw new InvalidOperationException("Could not create schema-9 upgrade fixture.");
        schema9Node["schemaVersion"] = 9;
        schema9Node["design"]?.AsObject().Remove("studyPlan");
        var schema9Upgrade = ExperimentRunResultSchemaMigrator.Deserialize(schema9Node.ToJsonString(JsonOptions), JsonOptions);
        if (schema9Upgrade.SchemaVersion != 11 ||
            schema9Upgrade.SourceSchemaVersion != 9 ||
            schema9Upgrade.StudyPlan.SamplingPlanComplete)
        {
            throw new InvalidOperationException("Schema-9 study-plan upgrade failed.");
        }

        var schema10Node = JsonNode.Parse(schema11Json)?.AsObject()
            ?? throw new InvalidOperationException("Could not create schema-10 upgrade fixture.");
        schema10Node["schemaVersion"] = 10;
        schema10Node["design"]?["studyPlan"]?.AsObject().Remove("outcomePlanSha256");
        schema10Node["design"]?["studyPlan"]?.AsObject().Remove("outcomeContractVersion");
        schema10Node["design"]?["studyPlan"]?.AsObject().Remove("successNonInferiorityMargin");
        schema10Node["design"]?["studyPlan"]?.AsObject().Remove("coverageNonInferiorityMargin");
        schema10Node["design"]?["studyPlan"]?.AsObject().Remove("mutationNonInferiorityMargin");
        schema10Node["design"]?["studyPlan"]?.AsObject().Remove("outcomePlanDeclared");
        schema10Node["design"]?["studyPlan"]?.AsObject().Remove("outcomePlanProvisional");
        var schema10Upgrade = ExperimentRunResultSchemaMigrator.Deserialize(schema10Node.ToJsonString(JsonOptions), JsonOptions);
        if (schema10Upgrade.SchemaVersion != 11 ||
            schema10Upgrade.SourceSchemaVersion != 10 ||
            schema10Upgrade.StudyPlan.OutcomePlanDeclared)
        {
            throw new InvalidOperationException("Schema-10 outcome-plan upgrade failed.");
        }

        return "Schema 5/6 migration, schema-7/8/9/10 upgrade, and schema-11 outcome-plan round trip: OK";
    }
}
