namespace RQ3Evaluator.Wpf;
public static class B0ToolGate
{
    public static Prediction Evaluate(CaseInput c)
    {
        if(c.ToolEvidence is null) return WithHash(new(c.CaseId,"B0_BASIC_TOOL_GATE","ABSTAIN","INSUFFICIENT_EVIDENCE","REQUEST_TOOL_EVIDENCE",true,"No per-case tool evidence was supplied.",0),c);
        var e=c.ToolEvidence.Value;
        string Get(string p)
        {
            if(e.TryGetProperty(p,out var x)) return x.GetString()??"UNKNOWN";
            if(p is "compile" or "junit" && e.TryGetProperty("tool_runs",out var runs))
            {
                foreach(var run in runs.EnumerateArray()) if(run.TryGetProperty("phase",out var phase)&&phase.GetString()==p&&run.TryGetProperty("status",out var status)) return status.GetString()??"UNKNOWN";
            }
            if(p=="target_reached"&&e.TryGetProperty("coverage",out var coverage)&&coverage.TryGetProperty("target_reached",out var reached)) return reached.GetString()??"UNKNOWN";
            if(p=="relevant_mutant_survives"&&e.TryGetProperty("mutation",out var mutation)&&mutation.TryGetProperty("relevant_mutant_survives",out var survives)) return survives.GetString()??"UNKNOWN";
            return "UNKNOWN";
        }
        if(Get("compile") == "FAIL") return WithHash(new(c.CaseId,"B0_BASIC_TOOL_GATE","REPAIR_REQUIRED","COMPILATION_ERROR","LOCAL_TEST_REPAIR",false,"Compiler evidence reports failure.",0),c);
        if(Get("junit") == "FAIL") return WithHash(new(c.CaseId,"B0_BASIC_TOOL_GATE","REPAIR_REQUIRED","SETUP_OR_RUNTIME_ERROR","LOCAL_TEST_REPAIR",false,"JUnit evidence reports failure.",0),c);
        return WithHash(new(c.CaseId,"B0_BASIC_TOOL_GATE","VERIFIED","NONE","NONE",false,"Compilation and selected JUnit test did not fail; B0 intentionally ignores semantic, coverage, and mutation findings.",0),c);
    }

    private static Prediction WithHash(Prediction prediction,CaseInput input)=>prediction with { EvidenceEnvelopeHash=input.EvidenceEnvelopeHash };
}
