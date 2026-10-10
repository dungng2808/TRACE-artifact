namespace Rq1Experiment.Contracts.Experiments;

public sealed record PreflightRequest(
    string ProjectId,
    string RepositoryRoot,
    string ExperimentOutputRoot,
    int CompileTimeoutSeconds,
    int TestTimeoutSeconds,
    int JacocoTimeoutSeconds,
    int PitTimeoutSeconds,
    string JacocoVersion,
    string PitVersion,
    string JavaHome = "",
    string ModulePath = ".");

public sealed record PreflightProgress(
    string CheckId,
    string Message,
    int CompletedChecks,
    int TotalChecks);

public sealed class PreflightReport
{
    public string ProjectId { get; set; } = string.Empty;
    public string RepositoryRoot { get; set; } = string.Empty;
    public string ModulePath { get; set; } = ".";
    public string Revision { get; set; } = string.Empty;
    public string BuildTool { get; set; } = "unknown";
    public string BuildToolVersion { get; set; } = string.Empty;
    public string JavaVersion { get; set; } = string.Empty;
    public string JavaHome { get; set; } = string.Empty;
    public int ProductionJavaFileCount { get; set; }
    public string SourceCheck { get; set; } = "NOT_RUN";
    public string CleanCompile { get; set; } = "NOT_RUN";
    public List<string> ExistingTests { get; set; } = [];
    public string ExternalServices { get; set; } = "NOT_RUN";
    public string JacocoSmoke { get; set; } = "NOT_RUN";
    public string PitSmoke { get; set; } = "NOT_RUN";
    public bool Eligible { get; set; }
    public string? ExclusionReason { get; set; }
    public string EvidenceDirectory { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
}
