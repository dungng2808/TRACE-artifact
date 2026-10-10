namespace RQ3Evaluator.Wpf;

public static class B2Aggregator
{
    private static readonly string[] Priority =
    {
        "COMPILATION_ERROR",
        "SHARED_INFRASTRUCTURE_ERROR",
        "SETUP_OR_RUNTIME_ERROR",
        "TARGET_NOT_REACHED",
        "WEAK_ORACLE",
        "PLAN_TEST_MISMATCH",
        "ASSERTION_ERROR"
    };

    public static Prediction Aggregate(string caseId,string envelopeHash,IReadOnlyList<SpecialistFinding> findings)
    {
        foreach(var cause in Priority)
        {
            var finding=findings.FirstOrDefault(x=>x.Status=="DETECTED"&&x.Cause==cause&&x.ExecutionStatus=="OK");
            if(finding is not null) return Build(caseId,envelopeHash,findings,"REPAIR_REQUIRED",cause,RouteFor(cause),false,$"PRIORITY_{cause}",finding.Rationale);
        }
        var allClear=findings.Count==3&&findings.All(x=>x.Status=="CLEAR"&&x.ExecutionStatus=="OK");
        if(allClear) return Build(caseId,envelopeHash,findings,"VERIFIED","NONE","NONE",false,"UNANIMOUS_CLEAR","All three specialists returned CLEAR.");
        return Build(caseId,envelopeHash,findings,"ABSTAIN","INSUFFICIENT_EVIDENCE","REQUEST_TOOL_EVIDENCE",true,"SAFE_ABSTENTION","At least one specialist was insufficient or invalid and no valid defect finding existed.");
    }

    public static string RouteFor(string cause)=>cause switch
    {
        "COMPILATION_ERROR" or "SETUP_OR_RUNTIME_ERROR" or "ASSERTION_ERROR" or "WEAK_ORACLE"=>"LOCAL_TEST_REPAIR",
        "PLAN_TEST_MISMATCH" or "TARGET_NOT_REACHED"=>"REGENERATE_TEST",
        "SHARED_INFRASTRUCTURE_ERROR"=>"SHARED_INFRASTRUCTURE_REPAIR",
        _=>"REQUEST_TOOL_EVIDENCE"
    };

    private static Prediction Build(string caseId,string envelopeHash,IReadOnlyList<SpecialistFinding> findings,string decision,string cause,string route,bool abstained,string rule,string rationale)
    {
        return new Prediction(caseId,"B2_SPECIALIZED_MULTI_AGENT",decision,cause,route,abstained,rationale,
            findings.Sum(x=>x.LatencyMs),findings.Sum(x=>x.InputTokens),findings.Sum(x=>x.OutputTokens),
            findings.All(x=>x.ExecutionStatus=="OK")?"OK":"PARTIAL_INVALID_OUTPUT",
            RequestHash:Hash(findings.Select(x=>x.RequestHash)),ResponseHash:Hash(findings.Select(x=>x.ResponseHash)),
            ApiCalls:findings.Sum(x=>x.ApiCalls),CostUsd:findings.Sum(x=>x.CostUsd))
        {
            EvidenceEnvelopeHash=envelopeHash,
            AggregationRule=rule,
            SpecialistFindings=findings
        };
    }

    private static string Hash(IEnumerable<string?> values)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|',values))));
}
