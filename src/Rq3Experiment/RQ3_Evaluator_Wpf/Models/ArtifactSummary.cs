namespace RQ3Evaluator.Wpf.Models;

public sealed record ArtifactSummary(
    string Name,
    string RelativePath,
    string FullPath,
    bool Exists,
    long SizeBytes,
    string? Sha256,
    string Description
);
