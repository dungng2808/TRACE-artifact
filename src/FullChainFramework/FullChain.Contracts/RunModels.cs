using System;
using System.Collections.Generic;

namespace FullChain.Contracts;

public static class TerminalStatus
{
    public const string Succeeded = "Succeeded";
    public const string GenerationFailed = "GenerationFailed";
    public const string CandidateInvalid = "CandidateInvalid";
    public const string RepairExhausted = "RepairExhausted";
    public const string VerifierRejected = "VerifierRejected";
    public const string Unverified = "Unverified";
    public const string Timeout = "Timeout";
    public const string Cancelled = "Cancelled";
    public const string BudgetExhausted = "BudgetExhausted";
    public const string ProviderFailure = "ProviderFailure";
    public const string ToolFailure = "ToolFailure";
    public const string InfrastructureFailure = "InfrastructureFailure";
    public const string MissingEvidence = "MissingEvidence";
    public const string Unsupported = "Unsupported";
    public const string NotApplicable = "NotApplicable";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Succeeded,
        GenerationFailed,
        CandidateInvalid,
        RepairExhausted,
        VerifierRejected,
        Unverified,
        Timeout,
        Cancelled,
        BudgetExhausted,
        ProviderFailure,
        ToolFailure,
        InfrastructureFailure,
        MissingEvidence,
        Unsupported,
        NotApplicable
    };
}

public sealed record TargetDescriptor(
    string SubjectId,
    string ProjectId,
    string Revision,
    string RepositoryPath,
    string ModulePath,
    string SourceFile,
    string FocalClass,
    string FocalMethodSignature,
    int StartLine,
    int EndLine,
    int Loc,
    int CyclomaticComplexity,
    string ComplexityStratum,
    string Visibility,
    bool ExplicitExceptionPath,
    string SourceSha256,
    string OfficialTargetSemantics,
    IReadOnlyList<BranchObligation> Obligations
);

public sealed record BranchObligation(
    string TargetId,
    string Type,
    string MeasurementRole,
    string MeasurementSemantics,
    int Line,
    string? SourceRange = null,
    string? Predicate = null,
    string? Statement = null
);

public sealed record FullChainConfiguration(
    string SchemaVersion,
    string ExperimentId,
    string Mode,
    string CohortManifestPath,
    string DatasetProjectsRoot,
    string OutputRoot,
    string ToolLockPath,
    IReadOnlyList<int> Repetitions,
    BudgetPolicy Budget,
    LlmConfiguration Llm,
    ToolConfiguration Tools
);

public sealed record BudgetPolicy(
    int HardWallClockSeconds,
    FullChainBudgetPolicy FullChain,
    EvoSuiteBudgetPolicy EvoSuite
);

public sealed record FullChainBudgetPolicy(
    int GenerationTimeoutSeconds,
    int RepairTotalTimeoutSeconds,
    int EvaluationTotalTimeoutSeconds,
    int VerificationTotalTimeoutSeconds,
    int OrchestrationTimeoutSeconds,
    int MaxRepairRounds,
    int MaxVerificationPasses,
    int MaxProviderRequests,
    int MaxTransientRetriesPerRequest,
    int MaxCumulativeTokens
);

public sealed record EvoSuiteBudgetPolicy(
    int SearchTimeoutSeconds,
    int EvaluationTotalTimeoutSeconds,
    int VerificationTotalTimeoutSeconds,
    int OrchestrationTimeoutSeconds
);

public sealed record LlmConfiguration(
    string Provider,
    string Endpoint,
    string ModelId,
    double Temperature,
    int TimeoutSeconds,
    string? ApiKeyEnvironmentVariable = null,
    string? ApiKeyFilePath = null
);

public sealed record ToolConfiguration(
    string JavaHome,
    string MavenHome,
    string EvosuiteJar,
    string EvosuiteRuntimeJar,
    string JacocoVersion,
    string PitVersion
);

public sealed record ToolLock(
    string SchemaVersion,
    string GeneratedAt,
    ToolLockEnvironment Environment
);

public sealed record ToolLockEnvironment(
    ToolLockJava Java,
    ToolLockMaven Maven,
    ToolLockEvoSuite Evosuite,
    ToolLockJacoco Jacoco,
    ToolLockPit Pit
);

public sealed record ToolLockJava(
    string Version,
    string Vendor,
    ToolPathEntry Macos,
    ToolPathEntry Windows
);

public sealed record ToolLockMaven(
    string Version,
    string Home,
    string Executable,
    string Sha256
);

public sealed record ToolLockEvoSuite(
    string Version,
    ToolPathEntry Jar,
    ToolPathEntry RuntimeJar
);

public sealed record ToolLockJacoco(
    string GroupId,
    string ArtifactId,
    string Version,
    string JarSha256
);

public sealed record ToolLockPit(
    string GroupId,
    string ArtifactId,
    string Version,
    string Junit5Plugin,
    string JarSha256
);

public sealed record ToolPathEntry(string Path, string Sha256);

public sealed record FullChainState(
    string CurrentStage,
    string Status,
    string? TerminalReason = null,
    int RepairRound = 0,
    int VerificationPass = 0,
    int RequestsUsed = 0,
    int TokensUsed = 0,
    double WallClockSecondsElapsed = 0.0
);

