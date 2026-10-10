using System;
using System.Collections.Generic;

namespace FullChain.Contracts;

public sealed record GeneratedTestArtifact(
    string TestClassName,
    string TestPackageName,
    string TestSourceCode,
    string SourceSha256,
    string PlanJson,
    string PlanHash,
    IReadOnlyDictionary<string, string> ScenarioToMethodMap,
    int PromptTokens,
    int CompletionTokens,
    long LatencyMs
);

public sealed record FailureEvidence(
    string FailureType, // "PARSE", "COMPILE", "ASSERTION", "TIMEOUT", "TOOL_ERROR"
    string DiagnosticMessage,
    string? FailingTestMethodName,
    string? AssociatedScenarioId,
    string Stdout,
    string Stderr,
    int ExitCode
);

public sealed record RepairArtifact(
    int RoundNumber,
    string Strategy, // strictly "TGSLR"
    string TargetMethodName,
    string BeforeMethodSource,
    string AfterMethodSource,
    string FullRepairedSource,
    string SourceSha256,
    int PromptTokens,
    int CompletionTokens,
    bool BuildPassed,
    string StopReason
);

public sealed record CommonEnvelope(
    string SchemaVersion,
    string TargetId,
    string ProjectId,
    string SourceSha256,
    string TestSourceSha256,
    string TestSourceCode,
    IReadOnlyList<BranchObligation> BranchObligations,
    bool CompileSuccess,
    bool TestExecutionSuccess,
    string BuildLogSnippet,
    double BranchCoverage,
    double MutationScore,
    double StructuralTargetSatisfaction,
    string JaCoCoReportXml,
    string PitReportXml,
    IReadOnlyList<ObligationCoverageEvidence>? ObligationCoverage = null,
    IReadOnlyList<MutationEvidence>? FocalMutations = null,
    bool MutationEvidenceAvailable = false
);

/// <summary>
/// Focal, source-line measurement for one declared structural obligation.
/// This is distinct from project-wide coverage so B2 can audit only the
/// targets that the generated scenario plan claims to exercise.
/// </summary>
public sealed record ObligationCoverageEvidence(
    string TargetId,
    string Type,
    int Line,
    int CoveredBranches,
    int MissedBranches,
    int CoveredInstructions,
    int MissedInstructions,
    bool Satisfied
);

/// <summary>
/// A focal PIT mutation transformation retained for blinded oracle review.
/// No private gold labels or source outside the evaluated focal method are
/// included.
/// </summary>
public sealed record MutationEvidence(
    int Line,
    string Mutator,
    string Description,
    string Status,
    bool Detected,
    string? KillingTest
);

public static class VerificationVerdicts
{
    public const string Verified = "VERIFIED";
    public const string RepairRequired = "REPAIR_REQUIRED";
    public const string VerifierRejected = "VERIFIER_REJECTED";
    public const string Abstain = "ABSTAIN";
    public const string Unverified = "UNVERIFIED";
}

public static class VerificationRoutes
{
    public const string None = "NONE";
    public const string LocalTestRepair = "LOCAL_TEST_REPAIR";
    public const string RegenerateTest = "REGENERATE_TEST";
    public const string SharedInfrastructureRepair = "SHARED_INFRASTRUCTURE_REPAIR";
    public const string RequestToolEvidence = "REQUEST_TOOL_EVIDENCE";
}

/// <summary>
/// The complete, non-secret input required by the blinded RQ3 verifier.
/// It intentionally keeps target identity separate from evaluation metrics so
/// the verifier never has to reconstruct a focal class or method from a target ID.
/// </summary>
public sealed record VerificationRequest(
    TargetDescriptor Target,
    CommonEnvelope Evaluation,
    string TestClassFqn,
    string ArtifactRelativePath,
    int CurrentVerificationPass,
    int RemainingRepairRounds,
    IReadOnlyDictionary<string, string>? ScenarioToMethodMap = null,
    string? PlanJson = null
);

public sealed record SpecialistFinding(
    string SpecialistId,
    string Verdict, // "PASS", "FAIL", "ABSTAIN"
    double Confidence,
    string DetailedReason
);

public sealed record VerificationDecision(
    string AggregateVerdict, // See VerificationVerdicts.
    string? ActionableRoute, // See VerificationRoutes; only LOCAL_TEST_REPAIR is actionable.
    IReadOnlyList<SpecialistFinding> SpecialistFindings,
    string EnvelopeSha256,
    long LatencyMs,
    string Timestamp
);

public sealed record CandidateDiagnostic(
    bool Compiled,
    bool Executed,
    double BranchCoverage,
    double MutationScore,
    double StructuralTargetSatisfaction,
    bool HasMissingEvidence
);

public sealed record DeliveredSystemOutcome(
    double VerifiedSuiteRate, // 1.0 or 0.0
    double CompilationSatisfactionRate,
    double ExecutionSatisfactionRate,
    double BranchCoverage,
    double MutationScore,
    double StructuralTargetSatisfaction
);

public sealed record FullChainRecord(
    string SchemaVersion,
    string RunId,
    string SubjectId,
    int RepetitionId,
    string Treatment, // "FullChain" or "EvoSuite"
    string TerminalStatus,
    string? TerminalReason,
    CandidateDiagnostic Diagnostic,
    DeliveredSystemOutcome Outcome,
    int TotalRepairRounds,
    int TotalVerificationPasses,
    int TotalProviderRequests,
    int TotalTokensUsed,
    double TotalWallClockSeconds,
    string WorkspaceDirectory,
    string Timestamp
);

public sealed record CampaignCheckpoint(
    string SchemaVersion,
    string CampaignId,
    string TargetId,
    int RepetitionId,
    string Treatment,
    string CurrentStage,
    string FullChainStateJson,
    string InputHash,
    string CreatedAtUtc
);

public sealed record PreflightReport(
    string SchemaVersion,
    string Timestamp,
    string SubjectId,
    string ProjectId,
    bool JavaVerified,
    string JavaVersion,
    bool MavenVerified,
    string MavenVersion,
    bool EvoSuiteVerified,
    string EvoSuiteSha256,
    bool SourceFileVerified,
    string SourceSha256,
    bool PomVerified,
    bool TestCompileSuccess,
    int TargetObligationsCount,
    bool Passed,
    IReadOnlyList<string> Diagnostics
);

public sealed record ArtifactIndexEntry(
    string ArtifactType,
    string Stage,
    string RelativePath,
    string AbsolutePath,
    string Sha256,
    long ByteSize,
    string TimestampUtc
);

public sealed record ArtifactIndex(
    string SchemaVersion,
    string CampaignId,
    string TargetId,
    int RepetitionId,
    string Treatment,
    IReadOnlyList<ArtifactIndexEntry> Entries
);
