using RQ2Experiment.Core.Domain;
using RQ2Experiment.Infrastructure.Real;

namespace RQ2Experiment.Infrastructure.Diagnostics;

internal static class TreatmentIsolationDiagnostic
{
    public static string Run()
    {
        const string source = """
            class SampleTest {
                @org.junit.jupiter.api.Test
                void focal() { org.junit.jupiter.api.Assertions.assertEquals(1, 2); }
                @org.junit.jupiter.api.Test
                void existing() { org.junit.jupiter.api.Assertions.assertTrue(true); }
            }
            """;
        const string instructions = "Common immutable repair instructions.";
        const string evidence = "AssertionError: expected 1 but was 2.";
        const string production = "class Sample { int value() { return 1; } }";
        var task = new ExperimentTask(
            "PROMPT-AUDIT",
            "repository",
            "SCENARIO-UNIQUE",
            "TEST-UNIQUE",
            "focal",
            "UNIQUE-SCENARIO-CONTEXT-7D3E must return the fixed value.",
            "UNIQUE-TRACE-7D3E -> focal",
            evidence,
            TestFilePath: "SampleTest.java",
            TestClassName: "SampleTest");

        var tgslr = TreatmentPromptBuilder.Build(
            task, RepairStrategy.TraceabilityGuidedScenarioLocal, source, evidence, production, instructions, "tg.json");
        var ftmr = TreatmentPromptBuilder.Build(
            task, RepairStrategy.FailingTestMethod, source, evidence, production, instructions, "ft.json");
        var fcr = TreatmentPromptBuilder.Build(
            task, RepairStrategy.FullClassRegeneration, source, evidence, production, instructions, "fc.json");

        if (!tgslr.Audit.ContractSatisfied ||
            !ftmr.Audit.ContractSatisfied ||
            !fcr.Audit.ContractSatisfied ||
            !tgslr.Audit.ScenarioIncluded ||
            ftmr.Audit.ScenarioIncluded ||
            !fcr.Audit.FullTestClassIncluded ||
            tgslr.Audit.TargetSourceSha256 != ftmr.Audit.TargetSourceSha256 ||
            tgslr.Audit.ToolEvidenceSha256 != ftmr.Audit.ToolEvidenceSha256 ||
            tgslr.Audit.ProductionContextSha256 != ftmr.Audit.ProductionContextSha256 ||
            tgslr.Audit.InstructionsSha256 != ftmr.Audit.InstructionsSha256 ||
            ftmr.Prompt.Contains(task.ScenarioSpecification, StringComparison.Ordinal) ||
            ftmr.Prompt.Contains(task.TraceabilityLink, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Treatment prompt contracts are not isolated as designed.");
        }

        var contaminated = TreatmentPromptBuilder.Build(
            task,
            RepairStrategy.FailingTestMethod,
            source,
            $"{evidence} {task.ScenarioSpecification}",
            production,
            instructions,
            "contaminated.json");
        if (contaminated.Audit.ContractSatisfied || !contaminated.Audit.ForbiddenContextDetected)
        {
            throw new InvalidOperationException("FTMR scenario-context leakage was not rejected.");
        }

        return "Treatment isolation: TGSLR/FTMR matched local inputs, FCR scope contract, prompt fingerprints, and FTMR leakage rejection: OK";
    }
}
