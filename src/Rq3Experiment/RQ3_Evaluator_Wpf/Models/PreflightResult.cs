namespace RQ3Evaluator.Wpf.Models;

public sealed record PreflightResult(
    bool IsValid,
    string ManifestStatus,
    int TotalCasesExpected,
    int CasesValidated,
    bool PublicCaseHashesValid,
    bool EvidenceBundleHashesValid,
    bool ProjectionHashesValid,
    bool EvidenceFreezeValid,
    bool ApiKeyAvailable,
    int ApiKeyCount,
    bool DockerAvailable,
    string? ErrorMessage,
    IReadOnlyList<string> ValidationLogs
);
