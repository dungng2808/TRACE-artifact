using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FullChain.Contracts;

namespace FullChain.Infrastructure.Java;

public sealed class MavenCandidateTestRunner : ICandidateTestRunner
{
    private readonly MavenBuildRunner _buildRunner;

    public MavenCandidateTestRunner(MavenBuildRunner buildRunner)
    {
        _buildRunner = buildRunner ?? throw new ArgumentNullException(nameof(buildRunner));
    }

    public async Task<CandidateExecutionResult> CompileAndExecuteCandidateAsync(
        TargetDescriptor target,
        string workspacePath,
        string testClassFqn,
        string testSourceCode,
        IGlobalBudgetGovernor budget,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(workspacePath);
        ArgumentNullException.ThrowIfNull(testClassFqn);
        ArgumentNullException.ThrowIfNull(testSourceCode);

        // 1. Write test file into workspace
        var relativeJavaPath = Path.Combine(testClassFqn.Split('.')) + ".java";
        var testFilePath = Path.Combine(workspacePath, "src", "test", "java", relativeJavaPath);
        var dir = Path.GetDirectoryName(testFilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        await File.WriteAllTextAsync(testFilePath, testSourceCode, ct);

        // 2. Determine simple name and run directory
        var simpleClassName = testClassFqn.Split('.').Last();
        var evidenceDir = Path.Combine(workspacePath, "evidence", "candidate_execution");
        Directory.CreateDirectory(evidenceDir);

        var timeout = budget.GetRemainingStageTimeout("CANDIDATE_COMPILE_AND_EXECUTE", TimeSpan.FromSeconds(120));

        // 3. Execute test through Maven
        var buildResult = await _buildRunner.ExecuteSingleTestAsync(
            workspacePath,
            simpleClassName,
            evidenceDir,
            timeout,
            ct);

        budget.RecordUsage("CANDIDATE_COMPILE_AND_EXECUTE", requestsUsed: 0, tokensUsed: 0, elapsedSeconds: buildResult.Duration.TotalSeconds);

        var stdoutSnippet = GetSnippet(buildResult.Stdout, 2000);

        if (buildResult.CompileSuccess && buildResult.TestSuccess)
        {
            return new CandidateExecutionResult(
                CompileSuccess: true,
                ExecutionSuccess: true,
                BuildLogSnippet: stdoutSnippet,
                Failure: null
            );
        }

        if (!buildResult.CompileSuccess)
        {
            var diag = ExtractCompilationError(buildResult.Stdout);
            var failure = new FailureEvidence(
                FailureType: "COMPILE",
                DiagnosticMessage: diag,
                FailingTestMethodName: null,
                AssociatedScenarioId: null,
                Stdout: stdoutSnippet,
                Stderr: GetSnippet(buildResult.Stderr, 1000),
                ExitCode: buildResult.ExitCode
            );

            return new CandidateExecutionResult(
                CompileSuccess: false,
                ExecutionSuccess: false,
                BuildLogSnippet: stdoutSnippet,
                Failure: failure
            );
        }

        // Compile succeeded, test failed
        var failingMethod = ExtractFailingTestMethod(buildResult.Stdout);
        var assertionDiag = ExtractAssertionFailure(buildResult.Stdout);
        var assertionFailure = new FailureEvidence(
            FailureType: "ASSERTION",
            DiagnosticMessage: assertionDiag,
            FailingTestMethodName: failingMethod,
            AssociatedScenarioId: null,
            Stdout: stdoutSnippet,
            Stderr: GetSnippet(buildResult.Stderr, 1000),
            ExitCode: buildResult.ExitCode
        );

        return new CandidateExecutionResult(
            CompileSuccess: true,
            ExecutionSuccess: false,
            BuildLogSnippet: stdoutSnippet,
            Failure: assertionFailure
        );
    }

    private static string ExtractCompilationError(string stdout)
    {
        var lines = stdout.Split('\n');
        var errorLines = lines.Where(l => l.Contains("[ERROR]") && !l.Contains("-> [Help 1]")).Take(10);
        var joined = string.Join("\n", errorLines);
        return string.IsNullOrWhiteSpace(joined) ? "Maven compilation failure." : joined;
    }

    private static string? ExtractFailingTestMethod(string stdout)
    {
        // Pattern: testMethodName(package.ClassName)  Time elapsed: ...  <<< FAILURE!
        var match = Regex.Match(stdout, @"([a-zA-Z0-9_]+)\([a-zA-Z0-9_\.]+\)\s+Time elapsed:.*?<<<\s*(?:FAILURE|ERROR)!");
        if (match.Success && match.Groups.Count > 1)
        {
            return match.Groups[1].Value;
        }

        // Pattern 2: [ERROR] Failures:\n[ERROR]   TestClass.testMethod:...
        var match2 = Regex.Match(stdout, @"\[ERROR\]\s+[a-zA-Z0-9_]+\.([a-zA-Z0-9_]+):");
        if (match2.Success && match2.Groups.Count > 1)
        {
            return match2.Groups[1].Value;
        }

        return null;
    }

    private static string ExtractAssertionFailure(string stdout)
    {
        var lines = stdout.Split('\n');
        var failureLines = lines.Where(l => l.Contains("<<< FAILURE!") || l.Contains("<<< ERROR!") || l.Contains("[ERROR]   ")).Take(8);
        var joined = string.Join("\n", failureLines);
        return string.IsNullOrWhiteSpace(joined) ? "Test assertion failure." : joined;
    }

    private static string GetSnippet(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length <= maxChars ? text : text[^maxChars..];
    }
}

