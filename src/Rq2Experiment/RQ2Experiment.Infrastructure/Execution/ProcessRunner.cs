using System.Diagnostics;
using System.Text;

namespace RQ2Experiment.Infrastructure.Execution;

internal sealed record ProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration)
{
    public bool Succeeded => ExitCode == 0;
    public string CombinedOutput => StandardOutput + Environment.NewLine + StandardError;
}

internal static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environmentOverrides = null)
    {
        var startInfo = CreateStartInfo(executable, arguments, workingDirectory);
        if (environmentOverrides is not null)
        {
            foreach (var (name, value) in environmentOverrides)
            {
                startInfo.Environment[name] = value;
            }
        }
        using var process = new Process { StartInfo = startInfo };
        var stopwatch = Stopwatch.StartNew();
        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start process '{executable}'.");
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"Process '{executable}' exceeded {timeout.TotalMinutes:0.#} minutes.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var standardOutput = await standardOutputTask;
        var standardError = await standardErrorTask;
        stopwatch.Stop();
        return new ProcessResult(process.ExitCode, standardOutput, standardError, stopwatch.Elapsed);
    }

    private static ProcessStartInfo CreateStartInfo(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory)
    {
        var isBatchFile = executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                          executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo
        {
            FileName = isBatchFile ? Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe" : executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        ApplyJavaHomeWhenMissing(startInfo);

        if (isBatchFile)
        {
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("call");
            startInfo.ArgumentList.Add(executable);
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }
        else
        {
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        return startInfo;
    }

    private static void ApplyJavaHomeWhenMissing(ProcessStartInfo startInfo)
    {
        if (startInfo.Environment.TryGetValue("JAVA_HOME", out var configuredJavaHome) &&
            !string.IsNullOrWhiteSpace(configuredJavaHome))
        {
            return;
        }

        var executableName = OperatingSystem.IsWindows() ? "java.exe" : "java";
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), executableName);
                if (!File.Exists(candidate)) continue;
                var file = new FileInfo(candidate);
                var resolved = file.ResolveLinkTarget(returnFinalTarget: true) ?? file;
                var bin = Directory.GetParent(resolved.FullName);
                var javaHome = bin?.Name.Equals("bin", StringComparison.OrdinalIgnoreCase) == true
                    ? bin.Parent?.FullName
                    : null;
                if (string.IsNullOrWhiteSpace(javaHome) ||
                    !File.Exists(Path.Combine(javaHome, "bin", executableName)))
                {
                    continue;
                }

                startInfo.Environment["JAVA_HOME"] = javaHome;
                return;
            }
            catch (IOException)
            {
                // Continue searching PATH when a link cannot be resolved.
            }
            catch (UnauthorizedAccessException)
            {
                // Continue searching PATH when a candidate cannot be inspected.
            }
        }

        if (!OperatingSystem.IsWindows()) return;
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var roots = new[]
        {
            Path.Combine(programFiles, "Java"),
            Path.Combine(programFiles, "Eclipse Adoptium"),
            Path.Combine(programFiles, "Microsoft")
        };
        foreach (var root in roots.Where(Directory.Exists))
        {
            try
            {
                var javaHome = Directory.EnumerateDirectories(root)
                    .Where(directory => File.Exists(Path.Combine(directory, "bin", executableName)))
                    .OrderByDescending(directory => directory, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (javaHome is null) continue;
                startInfo.Environment["JAVA_HOME"] = javaHome;
                return;
            }
            catch (IOException)
            {
                // Continue searching other conventional JDK roots.
            }
            catch (UnauthorizedAccessException)
            {
                // Continue searching other conventional JDK roots.
            }
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
