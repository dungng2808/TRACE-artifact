namespace RQ3Evaluator.Wpf.Models;

public sealed record Phase6Settings(
    string WorkspaceRoot,
    string DatasetRoot,
    string ManifestPath,
    string EvidenceBundleRoot,
    string ProjectionRoot,
    string EvidenceFreezePath,
    string OutputRoot,
    string Endpoint,
    string Model,
    string B1Endpoint,
    string B1Model,
    string B2Endpoint,
    string B2Model,
    string StabilityEndpoint,
    string StabilityModel,
    string ApiKeyFilePath,
    bool RunB0,
    bool RunB1,
    bool RunB2,
    bool Score,
    int MaxTokens = 4000,
    int MaxConcurrency = 5,
    int RateLimitRpm = 0,
    int MaxRetries = 3,
    int RequestTimeoutSec = 120
);
