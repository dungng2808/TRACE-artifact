using System.Diagnostics;

namespace RQ3Evaluator.Wpf.Services;

public sealed class ProcessExecutionService
{
    private Process? _currentProcess;
    private readonly object _lock = new();

    public async Task<int> ExecuteAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        Action<string> onOutput,
        Action<string> onError,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var tcs = new TaskCompletionSource<int>();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                onOutput(e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                onError(e.Data);
            }
        };

        lock (_lock)
        {
            _currentProcess = process;
        }

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Failed to start process: {fileName}");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var reg = ct.Register(() =>
            {
                try
                {
                    lock (_lock)
                    {
                        if (_currentProcess != null && !_currentProcess.HasExited)
                        {
                            _currentProcess.Kill(entireProcessTree: true);
                        }
                    }
                }
                catch
                {
                    // ignored
                }
            });

            await process.WaitForExitAsync(ct);
            return process.ExitCode;
        }
        finally
        {
            lock (_lock)
            {
                _currentProcess = null;
            }
        }
    }

    public void CancelCurrent()
    {
        lock (_lock)
        {
            try
            {
                if (_currentProcess != null && !_currentProcess.HasExited)
                {
                    _currentProcess.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // ignored
            }
        }
    }
}
