using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FullChain.Infrastructure.Processes;

namespace FullChain.Infrastructure.Evidence;

public static class CommandEvidenceWriter
{
    public static async Task WriteAsync(
        string evidenceDirectory,
        string stepName,
        ProcessResult result,
        CancellationToken ct = default)
    {
        var dir = Path.Combine(evidenceDirectory, stepName);
        Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(Path.Combine(dir, "command.txt"), $"{result.Command} {result.Arguments}", ct);
        await File.WriteAllTextAsync(Path.Combine(dir, "stdout.log"), result.Stdout, ct);
        await File.WriteAllTextAsync(Path.Combine(dir, "stderr.log"), result.Stderr, ct);

        var meta = new
        {
            step = stepName,
            command = result.Command,
            arguments = result.Arguments,
            workingDirectory = result.WorkingDirectory,
            exitCode = result.ExitCode,
            durationMs = result.Duration.TotalMilliseconds,
            timedOut = result.TimedOut,
            cancelled = result.Cancelled,
            isSuccess = result.IsSuccess,
            timestampUtc = DateTimeOffset.UtcNow.ToString("o")
        };

        var json = JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(dir, "execution-meta.json"), json, ct);
    }
}

