using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FullChain.Contracts;
using FullChain.Infrastructure.Java;
using FullChain.Infrastructure.LLM;
using FullChain.Infrastructure.Processes;
using FullChain.Runner;

namespace FullChain.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        var command = args[0].ToLowerInvariant();
        var subArgs = args.Skip(1).ToArray();

        try
        {
            return command switch
            {
                "doctor" => await RunDoctorAsync(subArgs),
                "validate-config" => await RunValidateConfigAsync(subArgs),
                "full-chain-preflight" => await RunPreflightAsync(subArgs),
                "run" => await RunCampaignAsync(subArgs, resumeIfPossible: false),
                "resume" => await RunCampaignAsync(subArgs, resumeIfPossible: true),
                "export" => await RunExportAsync(subArgs),
                _ => PrintUnknownCommand(command)
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("[CANCELLED] Campaign cancellation was requested.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"[FATAL ERROR] {ex.Message}");
            Console.ResetColor();
            return 2;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("FullChainFramework CLI");
        Console.WriteLine("Usage: dotnet run --project src/FullChainFramework/FullChain.Cli -- <command> [options]");
        Console.WriteLine("\nCommands:");
        Console.WriteLine("  doctor [--config <path>]                 Verify tool locks, hashes, and redacted LLM credential readiness");
        Console.WriteLine("  validate-config [path]                    Validate JSON configuration and secret references");
        Console.WriteLine("  full-chain-preflight [options]            Validate config, selected manifest target, and toolchain");
        Console.WriteLine("  run [options]                             Run a paired FullChain + EvoSuite campaign");
        Console.WriteLine("  resume [options]                          Resume compatible checkpoints in a paired campaign");
        Console.WriteLine("  export --runs <dir> [options]             Rebuild paired CSV, availability ledger, and reports");
        Console.WriteLine("\nCampaign options:");
        Console.WriteLine("  --config <path> --manifest <path> --target <id[,id]> | --all");
        Console.WriteLine("  --repetition <n[,n]> --campaign-id <id> --output <dir> --evo-timeout <seconds>");
        Console.WriteLine("  --dry-run                                 Validate and print the selected campaign without execution");
    }

    private static int PrintUnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        PrintUsage();
        return 1;
    }

    private static async Task<int> RunDoctorAsync(string[] args)
    {
        var repoRoot = FindRepoRoot();
        var configPath = GetOption(args, "--config") ?? GetDefaultConfigPath(repoRoot);
        var loaded = await CampaignFileLoader.LoadAsync(repoRoot, configPath, manifestPath: null, selectedSubjectIds: null, selectedRepetitions: null);
        var toolchain = ToolchainResolver.Resolve(loaded.Configuration.Tools, repoRoot);
        var processRunner = new ProcessRunner();

        Console.WriteLine("=== FullChain Doctor ===");
        Console.WriteLine($"Repository Root: {repoRoot}");

        var javaResult = await processRunner.RunAsync(
            toolchain.JavaExecutable,
            "-version",
            repoRoot,
            timeout: TimeSpan.FromSeconds(15));
        var mavenResult = await processRunner.RunAsync(
            toolchain.MavenExecutable,
            "-version",
            repoRoot,
            new Dictionary<string, string> { ["JAVA_HOME"] = toolchain.JavaHome },
            TimeSpan.FromSeconds(15));
        var mavenHashMatches = await HashMatchesAsync(toolchain.MavenExecutable, loaded.ToolLock.Environment.Maven.Sha256);
        var evoSuiteHashMatches = await HashMatchesAsync(toolchain.EvosuiteJar, loaded.ToolLock.Environment.Evosuite.Jar.Sha256);
        var credentialValid = true;
        try
        {
            var resolver = new ApiKeyResolver(repoRoot);
            ProviderCredentialValidator.EnsureUsable(loaded.Configuration.Llm, resolver.Resolve(loaded.Configuration.Llm));
        }
        catch (ProviderFailureException)
        {
            credentialValid = false;
        }

        PrintCheck("Java executable", javaResult.IsSuccess, toolchain.JavaExecutable);
        PrintCheck("Maven executable", mavenResult.IsSuccess, toolchain.MavenExecutable);
        PrintCheck("Maven SHA-256", mavenHashMatches, loaded.ToolLock.Environment.Maven.Sha256);
        PrintCheck("EvoSuite JAR SHA-256", evoSuiteHashMatches, loaded.ToolLock.Environment.Evosuite.Jar.Sha256);
        PrintCheck("Manifest", File.Exists(loaded.ManifestPath), loaded.ManifestPath);
        PrintCheck("LLM credential", credentialValid, credentialValid
            ? "Configured credential reference resolved (value redacted)"
            : "Configured credential reference rejected (value redacted)");

        return javaResult.IsSuccess && mavenResult.IsSuccess && mavenHashMatches && evoSuiteHashMatches && credentialValid ? 0 : 1;
    }

    private static async Task<int> RunValidateConfigAsync(string[] args)
    {
        var repoRoot = FindRepoRoot();
        var configPath = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal)
            ? args[0]
            : GetOption(args, "--config") ?? GetDefaultConfigPath(repoRoot);
        var fullConfigPath = CampaignFileLoader.ResolvePath(repoRoot, configPath);
        if (!File.Exists(fullConfigPath))
        {
            Console.Error.WriteLine($"[ERROR] Config file not found: {fullConfigPath}");
            return 1;
        }

        var content = await File.ReadAllTextAsync(fullConfigPath);
        var result = FullChain.Core.ConfigurationValidator.ValidateJson(content, repoRoot);
        foreach (var warning in result.Warnings)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[WARNING] {warning}");
            Console.ResetColor();
        }
        if (!result.IsValid)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[CONFIG INVALID] {fullConfigPath}");
            foreach (var error in result.Errors)
            {
                Console.WriteLine($"  - {error}");
            }
            Console.ResetColor();
            return 1;
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[CONFIG VALID] {fullConfigPath}");
        Console.ResetColor();
        return 0;
    }

    private static async Task<int> RunPreflightAsync(string[] args)
    {
        var repoRoot = FindRepoRoot();
        var selectedTargets = ReadCsvOption(args, "--target");
        var loaded = await LoadCampaignAsync(repoRoot, args, requireExplicitTargetSelection: false, selectedTargets);
        var toolchain = ToolchainResolver.Resolve(loaded.Configuration.Tools, repoRoot);
        var processRunner = new ProcessRunner();
        var target = loaded.Targets[0];
        var projectPath = ResolveProjectPath(repoRoot, target.RepositoryPath);
        var sourcePath = Path.Combine(projectPath, target.SourceFile);

        Console.WriteLine("=== FullChain Preflight Verification ===");
        Console.WriteLine($"Target Subject: {target.SubjectId} (Project: {target.ProjectId})");
        var java = await processRunner.RunAsync(toolchain.JavaExecutable, "-version", repoRoot, timeout: TimeSpan.FromSeconds(15));
        var maven = await processRunner.RunAsync(
            toolchain.MavenExecutable,
            "-version",
            repoRoot,
            new Dictionary<string, string> { ["JAVA_HOME"] = toolchain.JavaHome },
            TimeSpan.FromSeconds(15));

        var checks = new[]
        {
            ("Java", java.IsSuccess, toolchain.JavaExecutable),
            ("Maven", maven.IsSuccess, toolchain.MavenExecutable),
            ("EvoSuite", File.Exists(toolchain.EvosuiteJar), toolchain.EvosuiteJar),
            ("Project POM", File.Exists(Path.Combine(projectPath, "pom.xml")), projectPath),
            ("Target source", File.Exists(sourcePath), sourcePath)
        };
        foreach (var check in checks)
        {
            PrintCheck(check.Item1, check.Item2, check.Item3);
        }

        return checks.All(check => check.Item2) ? 0 : 1;
    }

    private static async Task<int> RunCampaignAsync(string[] args, bool resumeIfPossible)
    {
        var repoRoot = FindRepoRoot();
        var loaded = await LoadCampaignAsync(repoRoot, args, requireExplicitTargetSelection: true);
        var campaignId = GetOption(args, "--campaign-id")
                         ?? $"cli-{DateTime.UtcNow:yyyyMMddHHmmss}";
        var outputDirectory = CampaignFileLoader.ResolvePath(
            repoRoot,
            GetOption(args, "--output") ?? Path.Combine("experiments", "full-chain", "runs", campaignId));
        var evoTimeout = ReadPositiveIntOption(args, "--evo-timeout") ?? 420;
        var dryRun = args.Contains("--dry-run", StringComparer.Ordinal);

        var runRequest = new FullChainCampaignRequest(
            loaded.Configuration,
            loaded.ToolLock,
            loaded.Targets,
            loaded.Repetitions,
            campaignId,
            outputDirectory,
            evoTimeout,
            resumeIfPossible);

        if (dryRun)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                mode = "dry-run",
                campaignId,
                outputDirectory,
                targetCount = loaded.Targets.Count,
                targetIds = loaded.Targets.Select(target => target.SubjectId),
                repetitions = loaded.Repetitions,
                resumeIfPossible
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        using var cancellationSource = new CancellationTokenSource();
        ConsoleCancelEventHandler cancellationHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellationSource.Cancel();
        };
        Console.CancelKeyPress += cancellationHandler;
        try
        {
            var campaignService = new FullChainCampaignService(repoRoot);
            campaignService.LogEmitted += message => Console.WriteLine(message);
            campaignService.StageChanged += (stage, status) => Console.WriteLine($"[Stage] {stage} -> {status}");
            var result = await campaignService.RunAsync(runRequest, cancellationSource.Token);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                campaignId,
                rawRecordsPath = result.RawRecordsPath,
                pairedComparisonPath = result.PairedCsvPath,
                availabilityLedgerPath = result.AvailabilityLedgerPath,
                recordCount = result.Records.Count,
                completedPairs = result.ReadinessReport.CompletedPairs,
                expectedPairs = result.ReadinessReport.TotalExpectedPairs,
                headlineEligible = result.ReadinessReport.IsEligible
            }, new JsonSerializerOptions { WriteIndented = true }));
            return result.Records.Any(record => record.TerminalStatus is
                TerminalStatus.ProviderFailure or
                TerminalStatus.ToolFailure or
                TerminalStatus.InfrastructureFailure or
                TerminalStatus.Timeout)
                ? 1
                : 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancellationHandler;
        }
    }

    private static async Task<int> RunExportAsync(string[] args)
    {
        var repoRoot = FindRepoRoot();
        var runsPath = GetOption(args, "--runs");
        if (string.IsNullOrWhiteSpace(runsPath))
        {
            Console.Error.WriteLine("[ERROR] export requires --runs <campaign-directory-or-raw-records.jsonl>.");
            return 1;
        }

        var fullRunsPath = CampaignFileLoader.ResolvePath(repoRoot, runsPath);
        var sourceRecordsPath = Directory.Exists(fullRunsPath)
            ? Path.Combine(fullRunsPath, "raw-records.jsonl")
            : fullRunsPath;
        var campaignId = GetOption(args, "--campaign-id")
                         ?? Path.GetFileName(Path.GetDirectoryName(sourceRecordsPath) ?? "campaign");
        var outputDirectory = CampaignFileLoader.ResolvePath(
            repoRoot,
            GetOption(args, "--out") ?? Path.Combine(Path.GetDirectoryName(sourceRecordsPath) ?? repoRoot, "export"));
        var loaded = await LoadCampaignAsync(repoRoot, args, requireExplicitTargetSelection: false);

        var result = await CampaignExportService.ExportAsync(
            campaignId,
            sourceRecordsPath,
            outputDirectory,
            loaded.Configuration,
            loaded.ToolLock,
            loaded.Targets,
            loaded.Repetitions);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            campaignId,
            result.RawRecordsPath,
            result.PairedComparisonPath,
            result.AvailabilityLedgerPath,
            reportsDirectory = Path.GetDirectoryName(result.Reports.CompletenessReportPath),
            headlineEligible = result.Readiness.IsEligible
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static async Task<LoadedCampaign> LoadCampaignAsync(
        string repoRoot,
        string[] args,
        bool requireExplicitTargetSelection,
        IReadOnlyCollection<string>? targetOverride = null)
    {
        var targetIds = targetOverride ?? ReadCsvOption(args, "--target");
        var runAll = args.Contains("--all", StringComparer.Ordinal);
        if (requireExplicitTargetSelection && targetIds.Count == 0 && !runAll)
        {
            throw new ArgumentException("Campaign execution requires --target <id[,id]> or explicit --all.");
        }

        var repetitions = ReadIntCsvOption(args, "--repetition");
        return await CampaignFileLoader.LoadAsync(
            repoRoot,
            GetOption(args, "--config") ?? GetDefaultConfigPath(repoRoot),
            GetOption(args, "--manifest"),
            targetIds.Count > 0 ? targetIds : null,
            repetitions.Count > 0 ? repetitions : null);
    }

    private static string GetDefaultConfigPath(string repoRoot)
    {
        var local = Path.Combine(repoRoot, "experiments", "full-chain", "configs", "full-chain.local.json");
        if (File.Exists(local))
        {
            return local;
        }

        return Path.Combine(repoRoot, "experiments", "full-chain", "configs", "full-chain.official.json");
    }

    private static IReadOnlyCollection<string> ReadCsvOption(string[] args, string option)
    {
        var value = GetOption(args, option);
        return string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
    }

    private static IReadOnlyList<int> ReadIntCsvOption(string[] args, string option)
    {
        var value = GetOption(args, option);
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<int>();
        }

        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(valuePart => int.TryParse(valuePart, out var parsed) && parsed > 0
                ? parsed
                : throw new ArgumentException($"{option} must contain positive integer values."))
            .Distinct()
            .OrderBy(valuePart => valuePart)
            .ToArray();
    }

    private static int? ReadPositiveIntOption(string[] args, string option)
    {
        var value = GetOption(args, option);
        if (value == null)
        {
            return null;
        }

        if (!int.TryParse(value, out var parsed) || parsed <= 0)
        {
            throw new ArgumentException($"{option} must be a positive integer.");
        }
        return parsed;
    }

    private static string? GetOption(string[] args, string option)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (!string.Equals(args[index], option, StringComparison.Ordinal))
            {
                continue;
            }
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"{option} requires a value.");
            }
            return args[index + 1];
        }
        return null;
    }

    private static string ResolveProjectPath(string repoRoot, string rawProjectPath)
    {
        var direct = CampaignFileLoader.ResolvePath(repoRoot, rawProjectPath);
        if (Directory.Exists(direct))
        {
            return direct;
        }

        if (rawProjectPath.StartsWith("datasets/sf110", StringComparison.OrdinalIgnoreCase))
        {
            var mapped = CampaignFileLoader.ResolvePath(repoRoot, rawProjectPath.Replace("datasets/sf110", "datasets/rq1/sf110", StringComparison.OrdinalIgnoreCase));
            if (Directory.Exists(mapped))
            {
                return mapped;
            }
        }

        return direct;
    }

    private static async Task<bool> HashMatchesAsync(string filePath, string expectedSha256)
    {
        if (!File.Exists(filePath) || string.IsNullOrWhiteSpace(expectedSha256))
        {
            return false;
        }

        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream);
        return string.Equals(Convert.ToHexString(hash), expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static void PrintCheck(string label, bool passed, string detail)
    {
        Console.WriteLine($"[{(passed ? "PASS" : "FAIL")}] {label}: {detail}");
    }

    private static string FindRepoRoot()
    {
        var directory = Directory.GetCurrentDirectory();
        while (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "docs", "FULL_CHAIN_REBUILD_PLAN.md")))
            {
                return directory;
            }
            directory = Directory.GetParent(directory)?.FullName;
        }
        return Directory.GetCurrentDirectory();
    }
}
