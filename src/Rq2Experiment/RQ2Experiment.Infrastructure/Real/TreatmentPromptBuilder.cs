using System.Security.Cryptography;
using System.Text;
using RQ2Experiment.Core.Domain;
using RQ2Experiment.Infrastructure.Execution;

namespace RQ2Experiment.Infrastructure.Real;

internal sealed record TreatmentPromptBuild(string Prompt, PromptAuditEvidence Audit);

internal static class TreatmentPromptBuilder
{
    public const string ContractVersion = PromptAuditEvidence.CurrentContractVersion;

    public static TreatmentPromptBuild Build(
        ExperimentTask task,
        RepairStrategy strategy,
        string currentTestSource,
        string toolEvidence,
        string productionContext,
        string instructions,
        string auditArtifact)
    {
        var scenarioExpected = strategy is not RepairStrategy.FailingTestMethod;
        var fullClassExpected = strategy is RepairStrategy.FullClassRegeneration;
        var targetSource = fullClassExpected
            ? currentTestSource
            : JavaMethodEditor.ExtractMethod(currentTestSource, task.FailingTestMethod);

        var builder = new StringBuilder();
        builder.AppendLine($"Treatment contract: {ContractVersion}");
        builder.AppendLine($"Repair strategy: {strategy.ToDisplayName()}");
        builder.AppendLine($"Generated test file: {task.TestFilePath}");
        builder.AppendLine($"Failing test: {task.TestClassName}#{task.FailingTestMethod}");
        if (task.RequiresBuggyOracle)
        {
            builder.AppendLine(
                "Dual-version oracle: the repaired test must pass on the fixed revision and fail on the buggy revision.");
        }

        builder.AppendLine();
        builder.AppendLine("[TOOL_EVIDENCE]");
        builder.AppendLine(toolEvidence);

        if (scenarioExpected)
        {
            builder.AppendLine();
            builder.AppendLine("[SCENARIO_CONTEXT]");
            builder.AppendLine(task.ScenarioSpecification);
            builder.AppendLine("[TRACEABILITY_CONTEXT]");
            builder.AppendLine(task.TraceabilityLink);
        }

        builder.AppendLine();
        builder.AppendLine(fullClassExpected ? "[EDIT_TARGET_FULL_TEST_CLASS]" : "[EDIT_TARGET_FOCAL_METHOD]");
        if (fullClassExpected)
        {
            builder.AppendLine("Regenerate the complete Java test compilation unit below.");
            builder.AppendLine("Preserve every non-focal test and assertion. replacementSource must be the complete Java file.");
        }
        else
        {
            builder.AppendLine("Repair only the failing test method below.");
            builder.AppendLine("replacementSource must contain the complete method and annotations, but no class wrapper.");
        }
        builder.AppendLine(targetSource);

        if (!string.IsNullOrWhiteSpace(productionContext))
        {
            builder.AppendLine();
            builder.AppendLine("[PRODUCTION_SOURCE_CONTEXT]");
            builder.AppendLine(productionContext);
        }

        var prompt = builder.ToString();
        var forbiddenContextDetected = strategy == RepairStrategy.FailingTestMethod &&
                                       (ContainsNonEmpty(prompt, task.ScenarioSpecification) ||
                                        ContainsNonEmpty(prompt, task.TraceabilityLink));
        var flagsSatisfied = scenarioExpected == prompt.Contains("[SCENARIO_CONTEXT]", StringComparison.Ordinal) &&
                             scenarioExpected == prompt.Contains("[TRACEABILITY_CONTEXT]", StringComparison.Ordinal) &&
                             fullClassExpected == prompt.Contains("[EDIT_TARGET_FULL_TEST_CLASS]", StringComparison.Ordinal) &&
                             (!fullClassExpected) == prompt.Contains("[EDIT_TARGET_FOCAL_METHOD]", StringComparison.Ordinal) &&
                             prompt.Contains("[TOOL_EVIDENCE]", StringComparison.Ordinal);
        var violation = forbiddenContextDetected
            ? "FTMR prompt contains scenario or traceability content outside its allowed context policy."
            : flagsSatisfied
                ? ""
                : "Prompt sections do not match the selected treatment contract.";

        return new TreatmentPromptBuild(
            prompt,
            new PromptAuditEvidence
            {
                ContractVersion = ContractVersion,
                ContextPolicy = scenarioExpected ? "scenario+traceability" : "failure-evidence-only",
                EditScope = fullClassExpected ? "full-test-class" : "focal-method",
                ScenarioIncluded = scenarioExpected,
                TraceabilityIncluded = scenarioExpected,
                FocalMethodIncluded = !fullClassExpected,
                FullTestClassIncluded = fullClassExpected,
                ToolEvidenceIncluded = true,
                ProductionContextIncluded = !string.IsNullOrWhiteSpace(productionContext),
                ForbiddenContextDetected = forbiddenContextDetected,
                ContractSatisfied = flagsSatisfied && !forbiddenContextDetected,
                PromptSha256 = Hash(prompt),
                InstructionsSha256 = Hash(instructions),
                ToolEvidenceSha256 = Hash(toolEvidence),
                TargetSourceSha256 = Hash(targetSource),
                ProductionContextSha256 = Hash(productionContext),
                AuditArtifact = auditArtifact,
                Violation = violation
            });
    }

    private static bool ContainsNonEmpty(string prompt, string value) =>
        !string.IsNullOrWhiteSpace(value) && prompt.Contains(value.Trim(), StringComparison.Ordinal);

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? "")));
}
