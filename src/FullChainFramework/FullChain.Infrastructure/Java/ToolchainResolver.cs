using System;
using System.IO;
using FullChain.Contracts;

namespace FullChain.Infrastructure.Java;

public sealed record ResolvedToolchain(
    string JavaHome,
    string JavaExecutable,
    string MavenExecutable,
    string EvosuiteJar,
    string EvosuiteRuntimeJar
);

public static class JavaEnvironmentConstants
{
    public const string JdkJavaOptions = "--add-opens=java.base/java.util=ALL-UNNAMED --add-opens=java.base/java.lang=ALL-UNNAMED --add-opens=java.base/java.io=ALL-UNNAMED --add-opens=java.base/java.net=ALL-UNNAMED --add-opens=java.desktop/java.awt=ALL-UNNAMED -Djdk.attach.allowAttachSelf=true";
}

public static class ToolchainResolver
{
    public static ResolvedToolchain Resolve(ToolConfiguration config, string repoRoot)
    {
        var javaHome = ResolvePath(repoRoot, config.JavaHome);
        if (!Directory.Exists(javaHome))
        {
            // Fallback to platform bundled JDK
            javaHome = OperatingSystem.IsWindows()
                ? Path.Combine(repoRoot, "JDK", "jdk-17.0.20+8")
                : Path.Combine(repoRoot, "JDK", "macos-temurin-17", "Contents", "Home");
        }

        var javaBin = OperatingSystem.IsWindows()
            ? Path.Combine(javaHome, "bin", "java.exe")
            : Path.Combine(javaHome, "bin", "java");

        var mavenHome = ResolvePath(repoRoot, config.MavenHome);
        if (!Directory.Exists(mavenHome))
        {
            mavenHome = Path.Combine(repoRoot, "Maven", "apache-maven-3.9.16");
        }

        var mvnBin = OperatingSystem.IsWindows()
            ? Path.Combine(mavenHome, "bin", "mvn.cmd")
            : Path.Combine(mavenHome, "bin", "mvn");

        var evoJar = ResolvePath(repoRoot, config.EvosuiteJar);
        if (!File.Exists(evoJar))
        {
            evoJar = Path.Combine(repoRoot, "tools", "evosuite", "evosuite-1.2.0.jar");
        }

        var evoRtJar = ResolvePath(repoRoot, config.EvosuiteRuntimeJar);
        if (!File.Exists(evoRtJar))
        {
            evoRtJar = Path.Combine(repoRoot, "tools", "evosuite", "evosuite-standalone-runtime-1.2.0.jar");
        }

        return new ResolvedToolchain(
            JavaHome: javaHome,
            JavaExecutable: javaBin,
            MavenExecutable: mvnBin,
            EvosuiteJar: evoJar,
            EvosuiteRuntimeJar: evoRtJar
        );
    }

    private static string ResolvePath(string basePath, string relativeOrAbsolutePath)
    {
        if (Path.IsPathRooted(relativeOrAbsolutePath)) return relativeOrAbsolutePath;
        return Path.GetFullPath(Path.Combine(basePath, relativeOrAbsolutePath));
    }
}

