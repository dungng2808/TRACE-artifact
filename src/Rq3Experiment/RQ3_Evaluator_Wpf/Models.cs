namespace RQ3Evaluator.Wpf;

public sealed record RunOptions(
    string DatasetRoot,
    string OutputRoot,
    bool RunB0,
    bool RunB1,
    bool RunB2,
    bool Score,
    string Endpoint,
    string Model,
    IReadOnlyList<string> ApiKeys,
    string? B1Model = null,
    string? B2Model = null,
    string? B1Endpoint = null,
    string? B2Endpoint = null,
    int MaxTokens = 4000,
    int MaxConcurrency = 5,
    int RateLimitRpm = 0,
    int MaxRetries = 3,
    int RequestTimeoutSec = 120
);
public sealed record CaseInput(
    string CaseId,
    string PublicPath,
    string? GoldPath,
    JsonElement PublicCase,
    JsonElement? ToolEvidence,
    JsonElement EvidenceEnvelope,
    string EvidenceEnvelopeHash);

public sealed record SpecialistFinding(
    string Role,
    string Status,
    string Cause,
    string Rationale,
    string ExecutionStatus = "OK",
    int InputTokens = 0,
    int OutputTokens = 0,
    int ApiCalls = 0,
    double CostUsd = 0,
    double LatencyMs = 0,
    string? RequestHash = null,
    string? ResponseHash = null);

public sealed record Prediction(string CaseId,string Verifier,string Decision,string Cause,string Route,bool Abstained,string Rationale,double LatencyMs,int InputTokens=0,int OutputTokens=0,string ExecutionStatus="OK",string? RequestHash=null,string? ResponseHash=null,int ApiCalls=0,double CostUsd=0)
{
    public string? EvidenceEnvelopeHash { get; init; }
    public string? AggregationRule { get; init; }
    public IReadOnlyList<SpecialistFinding>? SpecialistFindings { get; init; }
}

public sealed record Gold(string Category,string Decision,string Cause,IReadOnlyList<string> AcceptableRoutes,bool FalsePassing)
{
    public string Route => AcceptableRoutes.FirstOrDefault() ?? "NONE";
}
