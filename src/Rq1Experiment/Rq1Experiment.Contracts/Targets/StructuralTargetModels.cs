namespace Rq1Experiment.Contracts.Targets;

public sealed class StructuralTarget
{
    public string TargetId { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string MeasurementRole { get; set; } = string.Empty;
    public string MeasurementSemantics { get; set; } = string.Empty;
    public int Line { get; set; }
    public string SourceRange { get; set; } = string.Empty;
    public string? Predicate { get; set; }
    public string? Statement { get; set; }
    public string? ExceptionType { get; set; }
}

public sealed class ControlFlowNode
{
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public int Line { get; set; }
    public string SourceRange { get; set; } = string.Empty;
    public string Statement { get; set; } = string.Empty;
}

public sealed class ControlFlowEdge
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}

public sealed class MethodControlFlowGraph
{
    public string EntryNodeId { get; set; } = string.Empty;
    public List<string> ExitNodeIds { get; set; } = [];
    public string Semantics { get; set; } = string.Empty;
    public List<string> Limitations { get; set; } = [];
    public List<ControlFlowNode> Nodes { get; set; } = [];
    public List<ControlFlowEdge> Edges { get; set; } = [];
}

public sealed class StructuralTargetInventory
{
    public string SubjectId { get; set; } = string.Empty;
    public string Split { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string FocalClass { get; set; } = string.Empty;
    public string FocalMethodSignature { get; set; } = string.Empty;
    public string SourceFile { get; set; } = string.Empty;
    public string SourceSha256 { get; set; } = string.Empty;
    public string AnalyzerVersion { get; set; } = string.Empty;
    public string OfficialTargetSemantics { get; set; } = string.Empty;
    public List<StructuralTarget> Targets { get; set; } = [];
    public MethodControlFlowGraph ControlFlowGraph { get; set; } = new();
}

public sealed record StructuralTargetRequest(
    string WorkspaceRoot,
    string ProjectsRoot,
    string SubjectsRoot,
    string OutputRoot,
    string JavaHome,
    string ExperimentMode = "Pilot");

public sealed class StructuralTargetReport
{
    public string AnalyzerVersion { get; set; } = string.Empty;
    public string OfficialTargetSemantics { get; set; } = string.Empty;
    public int SubjectCount { get; set; }
    public int PilotSubjectCount { get; set; }
    public int OfficialSubjectCount { get; set; }
    public int SmokeSubjectCount { get; set; }
    public int SubjectWithOfficialTargetsCount { get; set; }
    public int SubjectWithoutOfficialTargetsCount { get; set; }
    public List<string> SubjectsWithoutOfficialTargets { get; set; } = [];
    public int OfficialBranchSiteTargetCount { get; set; }
    public int DiagnosticReturnPathCount { get; set; }
    public int DiagnosticExceptionPathCount { get; set; }
    public int ErrorCount { get; set; }
    public bool DeterministicThreeRuns { get; set; }
    public bool TargetIdsStable { get; set; }
    public bool AllTargetsSourceMapped { get; set; }
    public int CfgNodeCount { get; set; }
    public int CfgEdgeCount { get; set; }
    public bool AllControlFlowGraphsValid { get; set; }
    public bool AllControlFlowNodesSourceMapped { get; set; }
    public bool SourceHashesVerified { get; set; }
    public bool PlannerIndependent { get; set; }
    public bool OutcomeSemanticsFrozen { get; set; }
    public bool Complete { get; set; }
    public string PilotTargetsHash { get; set; } = string.Empty;
    public string OfficialTargetsHash { get; set; } = string.Empty;
    public string SmokeTargetsHash { get; set; } = string.Empty;
    public string CombinedFingerprint { get; set; } = string.Empty;
    public DateTimeOffset GeneratedAt { get; set; }
}
