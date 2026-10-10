namespace Rq1Experiment.Contracts.Experiments;

public static class ToolRunStatuses
{
    public const string Pending = "PENDING";
    public const string GenerationInvalid = "GENERATION_INVALID";
    public const string CompilePass = "COMPILE_PASS";
    public const string CompileFail = "COMPILE_FAIL";
    public const string TestDiscoveryFail = "TEST_DISCOVERY_FAIL";
    public const string TestExecutionPass = "TEST_EXECUTION_PASS";
    public const string TestExecutionFail = "TEST_EXECUTION_FAIL";
    public const string AssertionFail = "ASSERTION_FAIL";
    public const string RuntimeError = "RUNTIME_ERROR";
    public const string CoveragePass = "COVERAGE_PASS";
    public const string CoverageToolError = "COVERAGE_TOOL_ERROR";
    public const string MutationPass = "MUTATION_PASS";
    public const string MutationToolError = "MUTATION_TOOL_ERROR";
    public const string Timeout = "TIMEOUT";
}

public sealed record ToolExecutionRequest(
    string ExperimentId,
    string Mode,
    string ExperimentOutputRoot,
    string WorkspaceRoot,
    string DatasetProjectsRoot,
    string JavaHome,
    int CompileTimeoutSeconds,
    int TestTimeoutSeconds,
    int JacocoTimeoutSeconds,
    int PitTimeoutSeconds,
    string JacocoVersion,
    string PitVersion,
    int MaxParallelToolRuns = 1);

public sealed class ProcessEvidence
{
    public string Gate { get; set; } = string.Empty;
    public string Executable { get; set; } = string.Empty;
    public List<string> Arguments { get; set; } = [];
    public int? ExitCode { get; set; }
    public bool TimedOut { get; set; }
    public double DurationSeconds { get; set; }
    public string StandardOutputFile { get; set; } = string.Empty;
    public string StandardErrorFile { get; set; } = string.Empty;
}

public sealed class FocalCoverageEvidence
{
    public bool MethodFound { get; set; }
    public int LineCovered { get; set; }
    public int LineMissed { get; set; }
    public int BranchCovered { get; set; }
    public int BranchMissed { get; set; }
    public int InstructionCovered { get; set; }
    public int InstructionMissed { get; set; }
    public string ReportFile { get; set; } = string.Empty;
}

public sealed class FocalMutationEvidence
{
    public int FocalMutants { get; set; }
    public int Killed { get; set; }
    public int Survived { get; set; }
    public int NoCoverage { get; set; }
    public int NonViable { get; set; }
    public int TimedOut { get; set; }
    public int RunError { get; set; }
    public int MemoryError { get; set; }
    public int Denominator => Killed + Survived + NoCoverage;
    public double? MutationScore => Denominator == 0 ? null : (double)Killed / Denominator;
    public string ReportFile { get; set; } = string.Empty;
}

public sealed class ToolRunRecord
{
    public string ExperimentId { get; set; } = string.Empty;
    public string SubjectId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string Approach { get; set; } = string.Empty;
    public int Repetition { get; set; }
    public string GenerationRunDirectory { get; set; } = string.Empty;
    public string GenerationStatus { get; set; } = string.Empty;
    public string Status { get; set; } = ToolRunStatuses.Pending;
    public string? FailureGate { get; set; }
    public string? FailureReason { get; set; }
    public string RepositoryPath { get; set; } = string.Empty;
    public string ModulePath { get; set; } = ".";
    public string FocalClass { get; set; } = string.Empty;
    public string FocalMethodSignature { get; set; } = string.Empty;
    public int FocalStartLine { get; set; }
    public int FocalEndLine { get; set; }
    public string GeneratedTestClass { get; set; } = string.Empty;
    public string GeneratedTestSourceHash { get; set; } = string.Empty;
    public string BuildTool { get; set; } = string.Empty;
    public string IsolatedWorkspace { get; set; } = string.Empty;
    public bool WorkspaceRetained { get; set; }
    public int RemovedExistingTestFileCount { get; set; }
    public List<ProcessEvidence> Processes { get; set; } = [];
    public int JunitTests { get; set; }
    public int JunitFailures { get; set; }
    public int JunitErrors { get; set; }
    public int JunitSkipped { get; set; }
    public FocalCoverageEvidence? Coverage { get; set; }
    public FocalMutationEvidence? Mutation { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompileTestCompletedAt { get; set; }
    public DateTimeOffset? AdequacyCompletedAt { get; set; }
}

public sealed class ToolExecutionReport
{
    public string ExperimentId { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
    public string Stage { get; set; } = string.Empty;
    public int DiscoveredRunCount { get; set; }
    public int GenerationInvalidCount { get; set; }
    public int CompilePassCount { get; set; }
    public int CompileFailCount { get; set; }
    public int JunitProcessSuccessCount { get; set; }
    public int ExecutionPassCount { get; set; }
    public int ExecutionFailCount { get; set; }
    public int TestDiscoveryFailCount { get; set; }
    public int CoveragePassCount { get; set; }
    public int CoverageToolErrorCount { get; set; }
    public int MutationPassCount { get; set; }
    public int MutationToolErrorCount { get; set; }
    public int TimeoutCount { get; set; }
    public bool GeneratedTestsOnly { get; set; }
    public bool GateOrderingValid { get; set; }
    public bool Complete { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }
}
