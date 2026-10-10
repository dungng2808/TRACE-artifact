using System;
using System.IO;
using FullChain.Infrastructure.Java;

namespace FullChain.Infrastructure.Processes;

/// <summary>
/// Temporarily exposes the locked Java and Maven binaries to an in-process
/// dependency that cannot accept a process-local environment dictionary.
/// Callers must serialize use and dispose this scope in a finally path.
/// </summary>
internal sealed class ScopedToolchainEnvironment : IDisposable
{
    private readonly string? _originalPath;
    private readonly string? _originalJavaHome;
    private bool _disposed;

    private ScopedToolchainEnvironment(string? originalPath, string? originalJavaHome)
    {
        _originalPath = originalPath;
        _originalJavaHome = originalJavaHome;
    }

    public static ScopedToolchainEnvironment Apply(ResolvedToolchain toolchain)
    {
        ArgumentNullException.ThrowIfNull(toolchain);

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var originalJavaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        var mavenBinDir = Path.GetDirectoryName(toolchain.MavenExecutable)
                          ?? throw new InvalidOperationException("Resolved Maven executable has no parent directory.");
        var javaBinDir = Path.Combine(toolchain.JavaHome, "bin");
        var path = $"{mavenBinDir}{Path.PathSeparator}{javaBinDir}{Path.PathSeparator}{originalPath ?? string.Empty}";

        Environment.SetEnvironmentVariable("PATH", path);
        Environment.SetEnvironmentVariable("JAVA_HOME", toolchain.JavaHome);
        return new ScopedToolchainEnvironment(originalPath, originalJavaHome);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Environment.SetEnvironmentVariable("PATH", _originalPath);
        Environment.SetEnvironmentVariable("JAVA_HOME", _originalJavaHome);
        _disposed = true;
    }
}
