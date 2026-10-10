using System.Text.Json.Serialization;

namespace Rq1Experiment.Contracts.Subjects;

public class SubjectCandidate
{
    public string CandidateKey { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string Revision { get; set; } = string.Empty;
    public string ModulePath { get; set; } = ".";
    public string SourceFile { get; set; } = string.Empty;
    public string FocalClass { get; set; } = string.Empty;
    public string FocalMethodSignature { get; set; } = string.Empty;
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public int Loc { get; set; }
    public int CyclomaticComplexity { get; set; }
    public string ComplexityStratum { get; set; } = string.Empty;
    public string Visibility { get; set; } = string.Empty;
    public bool ExplicitExceptionPath { get; set; }
    public bool Eligible { get; set; }
    public List<string> ExclusionReasons { get; set; } = [];
    public string SourceSha256 { get; set; } = string.Empty;

    [JsonIgnore]
    public string SelectionLabel => Eligible
        ? $"{FocalMethodSignature}  ✓ eligible"
        : $"{FocalMethodSignature}  ✕ {string.Join(", ", ExclusionReasons)}";
}

public sealed class FocalMethodSubject : SubjectCandidate
{
    public string SubjectId { get; set; } = string.Empty;
    public string RepositoryPath { get; set; } = string.Empty;
    public string Split { get; set; } = string.Empty;
    public int SelectionSeed { get; set; }
    public string ManualReviewStatus { get; set; } = "PENDING";
}

public sealed record SubjectSelectionRequest(
    string ProjectsRoot,
    string OutputRoot,
    string WorkspaceRoot,
    string Revision,
    string JavaHome,
    int PilotCount,
    int OfficialCount,
    int MaxPerProject,
    int SelectionSeed);

public sealed record SmokeMethodDiscoveryRequest(
    string RepositoryRoot,
    string OutputRoot,
    string WorkspaceRoot,
    string Revision,
    string JavaHome,
    string ModulePath = ".");

public sealed record SmokeSubjectFreezeRequest(
    SmokeMethodDiscoveryRequest Discovery,
    string CandidateKey);

public sealed class SmokeMethodDiscoveryReport
{
    public string RepositoryRoot { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public int ParsedMethodCount { get; set; }
    public int EligibleMethodCount { get; set; }
    public List<SubjectCandidate> Candidates { get; set; } = [];
    public DateTimeOffset GeneratedAt { get; set; }
}

public sealed class SubjectSelectionReport
{
    public string Revision { get; set; } = string.Empty;
    public int ParsedMethodCount { get; set; }
    public int EligibleCandidateCount { get; set; }
    public int ExcludedCandidateCount { get; set; }
    public int ParseErrorCount { get; set; }
    public int PilotCount { get; set; }
    public int OfficialCount { get; set; }
    public int RequestedOfficialCount { get; set; }
    public int MaxPerProject { get; set; }
    public int SelectionSeed { get; set; }
    public bool PilotOfficialDisjoint { get; set; }
    public bool ExactSignaturesUnique { get; set; }
    public bool PilotCoversAvailableStrata { get; set; }
    public bool AutomatedSelectionComplete { get; set; }
    public bool ManualReviewComplete { get; set; }
    public int ManualReviewApprovedCount { get; set; }
    public int ManualReviewPendingCount { get; set; }
    public int ManualReviewRejectedCount { get; set; }
    public string? DocumentedShortfall { get; set; }
    public string CandidatePoolHash { get; set; } = string.Empty;
    public string PilotManifestHash { get; set; } = string.Empty;
    public string OfficialManifestHash { get; set; } = string.Empty;
    public string ManifestFingerprint { get; set; } = string.Empty;
    public DateTimeOffset GeneratedAt { get; set; }
}
