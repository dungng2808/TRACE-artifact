using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FullChain.Contracts;
using FullChain.Core.Orchestration;
using FullChain.Infrastructure.Evaluation;
using FullChain.Infrastructure.Java;
using FullChain.Infrastructure.LLM;
using FullChain.Infrastructure.Persistence;
using FullChain.Infrastructure.Processes;
using FullChain.Infrastructure.Rq1;
using FullChain.Infrastructure.Rq2;
using FullChain.Infrastructure.Rq3;
using FullChain.Infrastructure.Workspace;

namespace FullChain.Benchmark;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("=== FullChain Framework Benchmark Tool ===");
        var repoRoot = FindRepoRoot();

        // 1. Resolve configuration and tool lock
        var configPath = Path.Combine(repoRoot, "experiments", "full-chain", "configs", "full-chain.local.json");
        if (!File.Exists(configPath))
        {
            configPath = Path.Combine(repoRoot, "experiments", "full-chain", "configs", "full-chain.example.json");
        }

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var config = JsonSerializer.Deserialize<FullChainConfiguration>(await File.ReadAllTextAsync(configPath), options)!;
        var toolLock = JsonSerializer.Deserialize<ToolLock>(await File.ReadAllTextAsync(Path.Combine(repoRoot, config.ToolLockPath)), options)!;

        // 2. Parse command line arguments
        string manifestPath = Path.Combine(repoRoot, config.CohortManifestPath);
        var repetitions = new List<int> { 42, 43, 44 };
        string campaignId = "full-chain-benchmark-run";
        string? outputDir = null;
        int? limit = null;
        int evoTimeoutSeconds = 420;
        bool generateReports = true;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--manifest" && i + 1 < args.Length)
            {
                manifestPath = Path.IsPathRooted(args[++i]) ? args[i] : Path.Combine(repoRoot, args[i]);
            }
            else if (args[i] == "--repetitions" && i + 1 < args.Length)
            {
                repetitions = args[++i].Split(',').Select(int.Parse).ToList();
            }
            else if (args[i] == "--campaign-id" && i + 1 < args.Length)
            {
                campaignId = args[++i];
            }
            else if (args[i] == "--output" && i + 1 < args.Length)
            {
                outputDir = Path.IsPathRooted(args[++i]) ? args[i] : Path.Combine(repoRoot, args[i]);
            }
            else if (args[i] == "--limit" && i + 1 < args.Length)
            {
                limit = int.Parse(args[++i]);
            }
            else if (args[i] == "--evo-timeout" && i + 1 < args.Length)
            {
                evoTimeoutSeconds = int.Parse(args[++i]);
            }
            else if (args[i] == "--no-reports")
            {
                generateReports = false;
            }
        }

        outputDir ??= Path.Combine(repoRoot, "experiments", "full-chain", "runs", campaignId);

        // 3. Load manifest targets
        using var manifestDoc = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        var targets = manifestDoc.RootElement.GetProperty("targets").EnumerateArray()
            .Select(e => JsonSerializer.Deserialize<TargetDescriptor>(e.GetRawText(), options)!)
            .ToList();

        if (limit.HasValue && limit.Value > 0)
        {
            targets = targets.Take(limit.Value).ToList();
        }

        Console.WriteLine($"[Benchmark] Campaign: {campaignId}");
        Console.WriteLine($"[Benchmark] Manifest: {manifestPath} ({targets.Count} targets)");
        Console.WriteLine($"[Benchmark] Repetitions: [{string.Join(", ", repetitions)}]");
        Console.WriteLine($"[Benchmark] Output directory: {outputDir}");

        // 4. Resolve components
        var toolchain = ToolchainResolver.Resolve(config.Tools, repoRoot);
        var proc = new ProcessRunner();
        var ws = new WorkspaceManager(outputDir, repoRoot);
        var ckStore = new CheckpointStore(Path.Combine(outputDir, "checkpoints"));
        var indexWriter = new ArtifactIndexWriter(outputDir);

        var sm = new FullChainStateMachine();
        var apiKeyResolver = new ApiKeyResolver(repoRoot);
        var resolvedApiKey = apiKeyResolver.Resolve(config.Llm);
        var client = new OpenAiCompatibleClient(config.Llm, resolvedApiKey: resolvedApiKey, apiKeyResolver: apiKeyResolver);
        var ctx = new Rq1ContextAdapter(repoRoot);
        var gen = new Rq1PlannedGenerationAdapter(ctx, client);
        var build = new MavenBuildRunner(proc, toolchain);
        var runner = new MavenCandidateTestRunner(build);
        var repair = new Rq2RepairAdapter(toolchain, config.Llm, resolvedApiKey, apiKeyResolver);
        var eval = new GeneratedSuiteEvaluator(proc, toolchain);
        var verifier = new Rq3VerifierAdapter(client);

        var orchestrator = new FullChainOrchestrator(sm, gen, runner, repair, eval, verifier, ckStore, indexWriter, ws);
        var evoAdapter = new EvoSuiteAdapter(toolchain, proc, eval, verifier, ws, indexWriter);

        var campaignRunner = new PairedCampaignRunner(orchestrator, evoAdapter);

        // 5. Run paired campaign
        var result = await campaignRunner.RunPairedCampaignAsync(
            targets: targets,
            repetitions: repetitions,
            config: config,
            toolLock: toolLock,
            campaignId: campaignId,
            outputDirectory: outputDir,
            evoSuiteSearchTimeoutSeconds: evoTimeoutSeconds
        );

        Console.WriteLine($"\n[Benchmark Finished] Total raw records: {result.Records.Count}, Completed pairs: {result.PairedRows.Count}/{targets.Count * repetitions.Count}");
        Console.WriteLine($"[Benchmark Finished] Headline eligible: {result.ReadinessReport.IsEligible}");

        // 6. Generate reports if requested
        if (generateReports)
        {
            var reportsDir = Path.Combine(repoRoot, "experiments", "full-chain", "reports");
            var reports = await PilotReportGenerator.GenerateReportsAsync(
                reportsDirectory: reportsDir,
                campaignId: campaignId,
                campaignResult: result,
                config: config,
                toolLock: toolLock,
                manifestTargets: targets,
                repetitions: repetitions
            );
            Console.WriteLine($"[Reports Generated] Completeness: {reports.CompletenessReportPath}");
            Console.WriteLine($"[Reports Generated] Exclusions: {reports.ExclusionsReportPath}");
            Console.WriteLine($"[Reports Generated] Environment: {reports.EnvironmentReportPath}");
            Console.WriteLine($"[Reports Generated] Evidence: {reports.EvidenceReportPath}");
            Console.WriteLine($"[Reports Generated] Cost: {reports.CostReportPath}");
        }

        return result.ReadinessReport.IsEligible ? 0 : 1;
    }

    private static string FindRepoRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "docs", "FULL_CHAIN_REBUILD_PLAN.md")))
            {
                return dir;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }
        return Directory.GetCurrentDirectory();
    }
}
