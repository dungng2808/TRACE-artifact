namespace Rq1Experiment.Contracts.Prompts;

public sealed class RepositoryContext
{
    public string SubjectId { get; set; } = string.Empty;
    public string Split { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string SourceFile { get; set; } = string.Empty;
    public string FocalClass { get; set; } = string.Empty;
    public string FocalMethodSignature { get; set; } = string.Empty;
    public string SourceSha256 { get; set; } = string.Empty;
    public string ExtractorVersion { get; set; } = string.Empty;
    public string RetrievalRule { get; set; } = string.Empty;
    public string PackageName { get; set; } = string.Empty;
    public List<string> Imports { get; set; } = [];
    public string EnclosingTypeDeclaration { get; set; } = string.Empty;
    public List<string> Fields { get; set; } = [];
    public List<string> Constructors { get; set; } = [];
    public List<string> FocalMethodOverloadSignatures { get; set; } = [];
    public string FocalMethodSource { get; set; } = string.Empty;
    public List<CalledProjectMethod> DirectlyCalledProjectMethods { get; set; } = [];
    public List<RelatedTypeApi> RelatedTypeApis { get; set; } = [];
    public List<UnresolvedProjectCall> UnresolvedCalls { get; set; } = [];
    public BuildContext Build { get; set; } = new();
    public string ContextHash { get; set; } = string.Empty;
}

public sealed class RelatedTypeApi
{
    public string ClassName { get; set; } = string.Empty;
    public string SourceFile { get; set; } = string.Empty;
    public string TypeDeclaration { get; set; } = string.Empty;
    public List<string> AccessibleConstructors { get; set; } = [];
    public List<string> AccessibleConstructorSources { get; set; } = [];
    public List<string> AccessibleMethods { get; set; } = [];
    public List<string> AccessibleFields { get; set; } = [];
    public List<string> SelectionReasons { get; set; } = [];
    public bool Truncated { get; set; }
}

public sealed class CalledProjectMethod
{
    public string FocalClass { get; set; } = string.Empty;
    public string Signature { get; set; } = string.Empty;
    public string SourceFile { get; set; } = string.Empty;
    public int StartLine { get; set; }
    public string Source { get; set; } = string.Empty;
    public string ResolutionRule { get; set; } = string.Empty;
}

public sealed class UnresolvedProjectCall
{
    public string Expression { get; set; } = string.Empty;
    public int Line { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public sealed class BuildContext
{
    public string BuildTool { get; set; } = string.Empty;
    public string ModulePath { get; set; } = ".";
    public string JavaVersion { get; set; } = string.Empty;
    public string TestFramework { get; set; } = string.Empty;
    public string RequiredTestApi { get; set; } = string.Empty;
    public string TestAnnotation { get; set; } = string.Empty;
    public string ExecutionProvider { get; set; } = string.Empty;
    public List<DependencyContext> Dependencies { get; set; } = [];
}

public sealed record DependencyContext(string GroupId, string ArtifactId, string Version, string Scope);

public sealed record RepositoryContextPromptRequest(
    string WorkspaceRoot,
    string ProjectsRoot,
    string SubjectsRoot,
    string TargetsRoot,
    string OutputRoot,
    string JavaHome,
    string ProviderId,
    string BaseEndpoint,
    string RequestedModelId,
    string ExperimentMode,
    string ExperimentId);

public sealed class RepositoryContextPromptReport
{
    public string ExtractorVersion { get; set; } = string.Empty;
    public string RetrievalRule { get; set; } = string.Empty;
    public int SubjectCount { get; set; }
    public int PilotSubjectCount { get; set; }
    public int OfficialSubjectCount { get; set; }
    public int SmokeSubjectCount { get; set; }
    public int ResolvedCalledMethodCount { get; set; }
    public int RelatedTypeApiCount { get; set; }
    public int UnresolvedCallCount { get; set; }
    public int ErrorCount { get; set; }
    public bool DeterministicTwoRuns { get; set; }
    public bool StrictSchemasValid { get; set; }
    public bool CommonContextSharedExactly { get; set; }
    public bool DirectPromptHasNoTargetLeakage { get; set; }
    public bool ExistingTestBodiesExcluded { get; set; }
    public bool PromptContractsValid { get; set; }
    public bool HashLockAccepted { get; set; }
    public string ContextFingerprint { get; set; } = string.Empty;
    public string PromptTemplateFingerprint { get; set; } = string.Empty;
    public string ConfigurationFingerprint { get; set; } = string.Empty;
    public bool Complete { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }
}
