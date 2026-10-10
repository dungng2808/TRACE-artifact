using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using FullChain.Contracts;
using FullChain.Core;
using FullChain.Infrastructure.Evaluation;
using FullChain.Infrastructure.Evidence;
using FullChain.Infrastructure.Java;
using FullChain.Infrastructure.Processes;

namespace FullChain.Benchmark;

public sealed class EvoSuiteAdapter
{
    private readonly ResolvedToolchain _toolchain;
    private readonly IProcessRunner _processRunner;
    private readonly ICommonEvaluator _evaluator;
    private readonly IVerifierAdapter _verifier;
    private readonly IWorkspaceManager _workspaceManager;
    private readonly IArtifactIndexWriter _artifactIndexWriter;

    public EvoSuiteAdapter(
        ResolvedToolchain toolchain,
        IProcessRunner processRunner,
        ICommonEvaluator evaluator,
        IVerifierAdapter verifier,
        IWorkspaceManager workspaceManager,
        IArtifactIndexWriter artifactIndexWriter)
    {
        _toolchain = toolchain ?? throw new ArgumentNullException(nameof(toolchain));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _workspaceManager = workspaceManager ?? throw new ArgumentNullException(nameof(workspaceManager));
        _artifactIndexWriter = artifactIndexWriter ?? throw new ArgumentNullException(nameof(artifactIndexWriter));
    }

    public async Task<FullChainRecord> RunEvoSuiteTargetAsync(
        TargetDescriptor target,
        int repetitionId,
        FullChainConfiguration config,
        ToolLock toolLock,
        string campaignId,
        IGlobalBudgetGovernor budget,
        int searchTimeoutSeconds = 420,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(budget);

        var startTime = DateTime.UtcNow;

        // 1. Prepare isolated workspace
        var workspacePath = await _workspaceManager.PrepareIsolatedWorkspaceAsync(
            target.RepositoryPath,
            campaignId,
            target.SubjectId,
            repetitionId,
            "EvoSuite",
            "benchmark",
            ct);

        var env = new Dictionary<string, string>
        {
            ["JAVA_HOME"] = _toolchain.JavaHome,
            ["JDK_JAVA_OPTIONS"] = JavaEnvironmentConstants.JdkJavaOptions
        };

        // 2. Initial compile of production classes
        var buildRunner = new MavenBuildRunner(_processRunner, _toolchain);
        var initialCompile = await buildRunner.CompileTestsAsync(
            workspacePath,
            Path.Combine(workspacePath, "evidence", "pre_compile"),
            TimeSpan.FromSeconds(120),
            ct);

        if (!initialCompile.CompileSuccess)
        {
            return CreateFailureRecord(target, repetitionId, "EvoSuite", TerminalStatus.InfrastructureFailure, "Workspace production code failed compilation.", workspacePath, startTime);
        }

        // 3. Run EvoSuite search
        var projectClasses = Path.Combine(workspacePath, "target", "classes");
        var cpEntries = new List<string> { projectClasses };
        var libDir = Path.Combine(workspacePath, "lib");
        if (Directory.Exists(libDir))
        {
            foreach (var jar in Directory.GetFiles(libDir, "*.jar", SearchOption.AllDirectories))
            {
                cpEntries.Add(jar);
            }
        }
        var projectCp = string.Join(Path.PathSeparator, cpEntries);
        var testOutputDir = Path.Combine(workspacePath, "evosuite-tests");
        Directory.CreateDirectory(testOutputDir);

        var evosuiteArgs = $"-jar \"{_toolchain.EvosuiteJar}\" -class {target.FocalClass} -projectCP \"{projectCp}\" -Dsearch_budget={searchTimeoutSeconds} -Dstopping_condition=MaxTime -seed {repetitionId} -Dtest_dir=\"{testOutputDir}\" -Djunit_suffix=_ESTest -Djunit_check=FALSE -Duse_separate_classloader=false";

        var javaExec = _toolchain.JavaExecutable;
        var evoTimeout = TimeSpan.FromSeconds(searchTimeoutSeconds + 60);

        var evoResult = await _processRunner.RunAsync(
            javaExec,
            evosuiteArgs,
            workspacePath,
            env,
            evoTimeout,
            ct);

        // Record search logs to evidence directory
        var evoEvidenceDir = Path.Combine(workspacePath, "evidence", "evosuite_search");
        Directory.CreateDirectory(evoEvidenceDir);
        await File.WriteAllTextAsync(Path.Combine(evoEvidenceDir, "stdout.log"), evoResult.Stdout, ct);
        await File.WriteAllTextAsync(Path.Combine(evoEvidenceDir, "stderr.log"), evoResult.Stderr, ct);
        await File.WriteAllTextAsync(Path.Combine(evoEvidenceDir, "command.txt"), $"{javaExec} {evosuiteArgs}\nExitCode: {evoResult.ExitCode}\nTimedOut: {evoResult.TimedOut}", ct);
        await _artifactIndexWriter.RecordArtifactAsync(
            campaignId, target.SubjectId, repetitionId, "EvoSuite", "EVOSUITE_SEARCH", "EvoSuiteSearchLog", Path.Combine(evoEvidenceDir, "stdout.log"), ct);

        budget.RecordUsage("EVOSUITE_SEARCH", requestsUsed: 0, tokensUsed: 0, elapsedSeconds: evoResult.Duration.TotalSeconds);

        var focalSimpleName = target.FocalClass.Split('.').Last();
        var generatedTestFiles = Directory.Exists(testOutputDir)
            ? Directory.GetFiles(testOutputDir, $"{focalSimpleName}_ESTest.java", SearchOption.AllDirectories)
            : Array.Empty<string>();

        if (generatedTestFiles.Length == 0)
        {
            var reason = evoResult.TimedOut
                ? $"EvoSuite process timed out after {evoTimeout.TotalSeconds:F0}s."
                : evoResult.ExitCode != 0
                    ? $"EvoSuite process exited with code {evoResult.ExitCode}: {(string.IsNullOrWhiteSpace(evoResult.Stderr) ? evoResult.Stdout.Trim().Split('\n').LastOrDefault() : evoResult.Stderr.Trim().Split('\n').LastOrDefault())}"
                    : "EvoSuite produced no test file within budget.";

            return CreateFailureRecord(target, repetitionId, "EvoSuite", TerminalStatus.GenerationFailed, reason, workspacePath, startTime);
        }

        var testFilePath = generatedTestFiles[0];
        var testSourceCode = await File.ReadAllTextAsync(testFilePath, ct);
        testSourceCode = testSourceCode.Replace("separateClassLoader = true", "separateClassLoader = false");

        // Also locate scaffolding file if generated
        var scaffoldingFiles = Directory.GetFiles(testOutputDir, $"{focalSimpleName}_ESTest_scaffolding.java", SearchOption.AllDirectories);
        string? scaffoldingSource = scaffoldingFiles.Length > 0 ? await File.ReadAllTextAsync(scaffoldingFiles[0], ct) : null;
        if (scaffoldingSource != null)
        {
            scaffoldingSource = scaffoldingSource.Replace("separateClassLoader = true", "separateClassLoader = false");
        }

        // Copy EvoSuite tests into workspace src/test/java
        var packageDir = target.FocalClass.Contains('.') ? target.FocalClass[..target.FocalClass.LastIndexOf('.')].Replace('.', Path.DirectorySeparatorChar) : "";
        var destTestDir = Path.Combine(workspacePath, "src", "test", "java", packageDir);
        Directory.CreateDirectory(destTestDir);

        var destTestFile = Path.Combine(destTestDir, $"{focalSimpleName}_ESTest.java");
        await File.WriteAllTextAsync(destTestFile, testSourceCode, ct);

        if (scaffoldingSource != null)
        {
            var destScaffoldingFile = Path.Combine(destTestDir, $"{focalSimpleName}_ESTest_scaffolding.java");
            await File.WriteAllTextAsync(destScaffoldingFile, scaffoldingSource, ct);
        }

        // Instrument POM with EvoSuite standalone runtime
        await InstrumentPomWithEvosuiteRuntimeAsync(workspacePath, ct);

        // Record test source artifact
        await _artifactIndexWriter.RecordArtifactAsync(
            campaignId, target.SubjectId, repetitionId, "EvoSuite", "EVOSUITE_GENERATION", "EvoSuiteTestSource", destTestFile, ct);

        // 4. Common Evaluation (JaCoCo, PIT, STS)
        var testFqn = target.FocalClass.Contains('.') ? $"{target.FocalClass}_ESTest" : $"{focalSimpleName}_ESTest";
        var envelope = await _evaluator.EvaluateSuiteAsync(target, workspacePath, testFqn, testSourceCode, budget, ct);

        // 5. Blinded RQ3 B2 Verification Gate
        var verificationRequest = new VerificationRequest(
            Target: target,
            Evaluation: envelope,
            TestClassFqn: testFqn,
            ArtifactRelativePath: Path.GetRelativePath(workspacePath, destTestFile),
            CurrentVerificationPass: 1,
            RemainingRepairRounds: 0);
        var decision = await _verifier.VerifyBlindedEnvelopeAsync(verificationRequest, budget, ct);

        // 6. Terminal status determination for EvoSuite baseline
        // Rule: EvoSuite has no repair loop. REPAIR_REQUIRED and ABSTAIN are terminal.
        string terminalStatus;
        string? terminalReason;
        double verifiedSuiteRate = 0.0;
        double csr = 0.0;
        double esr = 0.0;

        if (string.Equals(decision.AggregateVerdict, "VERIFIED", StringComparison.OrdinalIgnoreCase))
        {
            terminalStatus = TerminalStatus.Succeeded;
            terminalReason = "VerifiedByB2";
            verifiedSuiteRate = 1.0;
            csr = envelope.CompileSuccess ? 1.0 : 0.0;
            esr = envelope.TestExecutionSuccess ? 1.0 : 0.0;
        }
        else if (string.Equals(decision.AggregateVerdict, "ABSTAIN", StringComparison.OrdinalIgnoreCase))
        {
            terminalStatus = TerminalStatus.Unverified;
            terminalReason = "TerminalUnverifiedAbstain";
        }
        else if (string.Equals(decision.AggregateVerdict, "REPAIR_REQUIRED", StringComparison.OrdinalIgnoreCase))
        {
            terminalStatus = TerminalStatus.RepairExhausted;
            terminalReason = "TerminalRepairExhausted_NoRepairInBaseline";
        }
        else
        {
            terminalStatus = TerminalStatus.VerifierRejected;
            terminalReason = "B2Rejected";
        }

        var diagnostic = new CandidateDiagnostic(
            Compiled: envelope.CompileSuccess,
            Executed: envelope.TestExecutionSuccess,
            BranchCoverage: envelope.BranchCoverage,
            MutationScore: envelope.MutationScore,
            StructuralTargetSatisfaction: envelope.StructuralTargetSatisfaction,
            HasMissingEvidence: false
        );

        var outcome = new DeliveredSystemOutcome(
            VerifiedSuiteRate: verifiedSuiteRate,
            CompilationSatisfactionRate: csr,
            ExecutionSatisfactionRate: esr,
            BranchCoverage: verifiedSuiteRate > 0 ? envelope.BranchCoverage : 0.0,
            MutationScore: verifiedSuiteRate > 0 ? envelope.MutationScore : 0.0,
            StructuralTargetSatisfaction: verifiedSuiteRate > 0 ? envelope.StructuralTargetSatisfaction : 0.0
        );

        var totalSeconds = (DateTime.UtcNow - startTime).TotalSeconds;
        var runId = $"EvoSuite_{target.SubjectId}_rep{repetitionId}_{DateTime.UtcNow:yyyyMMddHHmmss}";

        return new FullChainRecord(
            SchemaVersion: "full-chain-record-v1",
            RunId: runId,
            SubjectId: target.SubjectId,
            RepetitionId: repetitionId,
            Treatment: "EvoSuite",
            TerminalStatus: terminalStatus,
            TerminalReason: terminalReason,
            Diagnostic: diagnostic,
            Outcome: outcome,
            TotalRepairRounds: 0,
            TotalVerificationPasses: 1,
            TotalProviderRequests: 0,
            TotalTokensUsed: 0,
            TotalWallClockSeconds: totalSeconds,
            WorkspaceDirectory: workspacePath,
            Timestamp: DateTime.UtcNow.ToString("o")
        );
    }

    private async Task InstrumentPomWithEvosuiteRuntimeAsync(string workspacePath, CancellationToken ct)
    {
        var pomPath = Path.Combine(workspacePath, "pom.xml");
        if (!File.Exists(pomPath)) return;

        var xml = await File.ReadAllTextAsync(pomPath, ct);
        var doc = XDocument.Parse(xml);
        var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;

        var dependencies = doc.Root?.Element(ns + "dependencies");
        if (dependencies == null)
        {
            dependencies = new XElement(ns + "dependencies");
            doc.Root?.Add(dependencies);
        }

        // Check if evosuite-standalone-runtime is already present
        var existing = dependencies.Elements(ns + "dependency")
            .FirstOrDefault(d => (string?)d.Element(ns + "artifactId") == "evosuite-standalone-runtime");

        bool modified = false;
        if (existing == null)
        {
            var dep = new XElement(ns + "dependency",
                new XElement(ns + "groupId", "org.evosuite"),
                new XElement(ns + "artifactId", "evosuite-standalone-runtime"),
                new XElement(ns + "version", "1.2.0"),
                new XElement(ns + "scope", "system"),
                new XElement(ns + "systemPath", _toolchain.EvosuiteRuntimeJar)
            );
            dependencies.Add(dep);
            modified = true;
        }

        // Check if junit-vintage-engine is present (needed for Surefire 3.x to run EvoSuite JUnit 4 tests)
        var vintageExisting = dependencies.Elements(ns + "dependency")
            .FirstOrDefault(d => (string?)d.Element(ns + "artifactId") == "junit-vintage-engine");

        if (vintageExisting == null)
        {
            var vintageDep = new XElement(ns + "dependency",
                new XElement(ns + "groupId", "org.junit.vintage"),
                new XElement(ns + "artifactId", "junit-vintage-engine"),
                new XElement(ns + "version", "5.10.2"),
                new XElement(ns + "scope", "test")
            );
            dependencies.Add(vintageDep);
            modified = true;
        }

        if (modified)
        {
            await File.WriteAllTextAsync(pomPath, doc.ToString(), ct);
        }
    }

    private static FullChainRecord CreateFailureRecord(
        TargetDescriptor target,
        int repetitionId,
        string treatment,
        string terminalStatus,
        string terminalReason,
        string workspacePath,
        DateTime startTime)
    {
        return new FullChainRecord(
            SchemaVersion: "full-chain-record-v1",
            RunId: $"{treatment}_{target.SubjectId}_rep{repetitionId}_{DateTime.UtcNow:yyyyMMddHHmmss}",
            SubjectId: target.SubjectId,
            RepetitionId: repetitionId,
            Treatment: treatment,
            TerminalStatus: terminalStatus,
            TerminalReason: terminalReason,
            Diagnostic: new CandidateDiagnostic(false, false, 0.0, 0.0, 0.0, false),
            Outcome: new DeliveredSystemOutcome(0.0, 0.0, 0.0, 0.0, 0.0, 0.0),
            TotalRepairRounds: 0,
            TotalVerificationPasses: 0,
            TotalProviderRequests: 0,
            TotalTokensUsed: 0,
            TotalWallClockSeconds: (DateTime.UtcNow - startTime).TotalSeconds,
            WorkspaceDirectory: workspacePath,
            Timestamp: DateTime.UtcNow.ToString("o")
        );
    }
}
