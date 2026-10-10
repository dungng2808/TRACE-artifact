using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FullChain.Infrastructure.Evidence;
using FullChain.Infrastructure.Processes;

namespace FullChain.Infrastructure.Java;

public sealed record MavenBuildResult(
    bool CompileSuccess,
    bool TestSuccess,
    int ExitCode,
    TimeSpan Duration,
    string Stdout,
    string Stderr,
    bool TimedOut,
    bool Cancelled
);

public sealed class MavenBuildRunner
{
    private readonly IProcessRunner _processRunner;
    private readonly ResolvedToolchain _toolchain;

    public MavenBuildRunner(IProcessRunner processRunner, ResolvedToolchain toolchain)
    {
        _processRunner = processRunner;
        _toolchain = toolchain;
    }

    public async Task<MavenBuildResult> CompileTestsAsync(
        string workspaceDirectory,
        string evidenceDirectory,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        var env = new Dictionary<string, string>
        {
            ["JAVA_HOME"] = _toolchain.JavaHome,
            ["JDK_JAVA_OPTIONS"] = JavaEnvironmentConstants.JdkJavaOptions
        };

        var args = "-B -ntp test-compile -DskipTests";
        var result = await _processRunner.RunAsync(
            _toolchain.MavenExecutable,
            args,
            workspaceDirectory,
            env,
            timeout,
            ct);

        await CommandEvidenceWriter.WriteAsync(evidenceDirectory, "compile", result, ct);

        return new MavenBuildResult(
            CompileSuccess: result.IsSuccess,
            TestSuccess: false,
            ExitCode: result.ExitCode,
            Duration: result.Duration,
            Stdout: result.Stdout,
            Stderr: result.Stderr,
            TimedOut: result.TimedOut,
            Cancelled: result.Cancelled
        );
    }

    public async Task<MavenBuildResult> ExecuteSingleTestAsync(
        string workspaceDirectory,
        string testClassSimpleName,
        string evidenceDirectory,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        var env = new Dictionary<string, string>
        {
            ["JAVA_HOME"] = _toolchain.JavaHome,
            ["JDK_JAVA_OPTIONS"] = JavaEnvironmentConstants.JdkJavaOptions
        };

        var args = $"-B -ntp test -Dtest={testClassSimpleName} -Dsurefire.failIfNoSpecifiedTests=true";
        var result = await _processRunner.RunAsync(
            _toolchain.MavenExecutable,
            args,
            workspaceDirectory,
            env,
            timeout,
            ct);

        await CommandEvidenceWriter.WriteAsync(evidenceDirectory, "test_execution", result, ct);

        return new MavenBuildResult(
            CompileSuccess: result.ExitCode == 0 || result.Stdout.Contains("Results:"),
            TestSuccess: result.IsSuccess,
            ExitCode: result.ExitCode,
            Duration: result.Duration,
            Stdout: result.Stdout,
            Stderr: result.Stderr,
            TimedOut: result.TimedOut,
            Cancelled: result.Cancelled
        );
    }
}

