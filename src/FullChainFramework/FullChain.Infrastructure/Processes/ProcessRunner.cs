using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FullChain.Infrastructure.Processes;

public sealed record ProcessResult(
    string Command,
    string Arguments,
    string WorkingDirectory,
    int ExitCode,
    string Stdout,
    string Stderr,
    TimeSpan Duration,
    bool TimedOut,
    bool Cancelled
)
{
    public bool IsSuccess => !TimedOut && !Cancelled && ExitCode == 0;
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        string executable,
        string arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string executable,
        string arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (environment != null)
        {
            foreach (var kvp in environment)
            {
                startInfo.Environment[kvp.Key] = kvp.Value;
            }
        }

        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();

        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) stdoutBuilder.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) stderrBuilder.AppendLine(e.Data);
        };

        var stopwatch = Stopwatch.StartNew();
        var timedOut = false;
        var cancelled = false;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout.HasValue && timeout.Value > TimeSpan.Zero)
        {
            linkedCts.CancelAfter(timeout.Value);
        }

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Failed to start process: {executable} {arguments}");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                }
                else
                {
                    timedOut = true;
                }

                KillProcessTree(process);
                // Give process tree a moment to drain
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                catch { }
            }
        }
        finally
        {
            stopwatch.Stop();
        }

        int exitCode = -1;
        try
        {
            if (process.HasExited)
            {
                exitCode = process.ExitCode;
            }
        }
        catch { }

        return new ProcessResult(
            Command: executable,
            Arguments: arguments,
            WorkingDirectory: workingDirectory,
            ExitCode: exitCode,
            Stdout: stdoutBuilder.ToString(),
            Stderr: stderrBuilder.ToString(),
            Duration: stopwatch.Elapsed,
            TimedOut: timedOut,
            Cancelled: cancelled
        );
    }

    public static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Process may already be dead
        }
    }
}

