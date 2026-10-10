using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FullChain.Benchmark;
using FullChain.Contracts;
using FullChain.Core;
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

namespace FullChain.Runner;

public sealed record FullChainCampaignRequest(
    FullChainConfiguration Configuration,
    ToolLock ToolLock,
    IReadOnlyList<TargetDescriptor> Targets,
    IReadOnlyList<int> Repetitions,
    string CampaignId,
    string OutputDirectory,
    int EvoSuiteSearchTimeoutSeconds = 420,
    bool ResumeIfPossible = true,
    string? InMemoryApiKey = null
);

public sealed record LoadedCampaign(
    FullChainConfiguration Configuration,
    ToolLock ToolLock,
    IReadOnlyList<TargetDescriptor> Targets,
    IReadOnlyList<int> Repetitions,
    string ManifestPath
);

public sealed class FullChainCampaignService
{
    private readonly string _repoRoot;

    public event Action<string>? LogEmitted;
    public event Action<string, string>? StageChanged;

    public FullChainCampaignService(string repoRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        _repoRoot = Path.GetFullPath(repoRoot);
    }

    public async Task<PairedCampaignResult> RunAsync(
        FullChainCampaignRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Targets.Count == 0)
        {
            throw new ArgumentException("A campaign must include at least one target.", nameof(request));
        }
        if (request.Repetitions.Count == 0)
        {
            throw new ArgumentException("A campaign must include at least one repetition.", nameof(request));
        }

        var validation = ConfigurationValidator.ValidateConfiguration(request.Configuration, _repoRoot);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(string.Join("; ", validation.Errors));
        }
        foreach (var warning in validation.Warnings)
        {
            Log($"[Configuration warning] {warning}");
        }

        var toolchain = ToolchainResolver.Resolve(request.Configuration.Tools, _repoRoot);
        var apiKeyResolver = new ApiKeyResolver(_repoRoot);
        var resolvedApiKey = apiKeyResolver.Resolve(request.Configuration.Llm, request.InMemoryApiKey);
        ProviderCredentialValidator.EnsureUsable(request.Configuration.Llm, resolvedApiKey);

        // Credential validation deliberately precedes every run-directory object:
        // a rejected request must leave no misleading partial campaign artifact.
        var processRunner = new ProcessRunner();
        var workspaceManager = new WorkspaceManager(request.OutputDirectory, _repoRoot);
        var checkpointStore = new CheckpointStore(Path.Combine(request.OutputDirectory, "checkpoints"));
        var artifactIndexWriter = new ArtifactIndexWriter(request.OutputDirectory);
        var llmClient = new OpenAiCompatibleClient(
            request.Configuration.Llm,
            resolvedApiKey: resolvedApiKey,
            apiKeyResolver: apiKeyResolver);
        var generator = new Rq1PlannedGenerationAdapter(new Rq1ContextAdapter(_repoRoot), llmClient);
        var candidateRunner = new MavenCandidateTestRunner(new MavenBuildRunner(processRunner, toolchain));
        var repairAdapter = new Rq2RepairAdapter(toolchain, request.Configuration.Llm, resolvedApiKey, apiKeyResolver);
        var evaluator = new GeneratedSuiteEvaluator(processRunner, toolchain);
        var verifier = new Rq3VerifierAdapter(llmClient);
        var orchestrator = new FullChainOrchestrator(
            new FullChainStateMachine(),
            generator,
            candidateRunner,
            repairAdapter,
            evaluator,
            verifier,
            checkpointStore,
            artifactIndexWriter,
            workspaceManager);
        orchestrator.LogEmitted += Log;
        orchestrator.StageChanged += (stage, status) => StageChanged?.Invoke(stage, status);

        var evoSuiteAdapter = new EvoSuiteAdapter(
            toolchain,
            processRunner,
            evaluator,
            verifier,
            workspaceManager,
            artifactIndexWriter);
        var pairedRunner = new PairedCampaignRunner(orchestrator, evoSuiteAdapter);
        pairedRunner.LogEmitted += Log;

        return await pairedRunner.RunPairedCampaignAsync(
            request.Targets,
            request.Repetitions,
            request.Configuration,
            request.ToolLock,
            request.CampaignId,
            request.OutputDirectory,
            request.EvoSuiteSearchTimeoutSeconds,
            request.ResumeIfPossible,
            ct);
    }

    private void Log(string message)
    {
        LogEmitted?.Invoke(message);
    }
}

public static class CampaignFileLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<LoadedCampaign> LoadAsync(
        string repoRoot,
        string configPath,
        string? manifestPath,
        IReadOnlyCollection<string>? selectedSubjectIds,
        IReadOnlyList<int>? selectedRepetitions,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        var fullRepoRoot = Path.GetFullPath(repoRoot);
        var fullConfigPath = ResolvePath(fullRepoRoot, configPath);
        if (!File.Exists(fullConfigPath))
        {
            throw new FileNotFoundException("FullChain configuration file was not found.", fullConfigPath);
        }

        var configJson = await File.ReadAllTextAsync(fullConfigPath, ct);
        var configuration = JsonSerializer.Deserialize<FullChainConfiguration>(configJson, JsonOptions)
                            ?? throw new InvalidOperationException("FullChain configuration could not be deserialized.");
        var validation = ConfigurationValidator.ValidateConfiguration(configuration, fullRepoRoot);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(string.Join("; ", validation.Errors));
        }

        var fullManifestPath = ResolvePath(fullRepoRoot, manifestPath ?? configuration.CohortManifestPath);
        if (!File.Exists(fullManifestPath))
        {
            throw new FileNotFoundException("Target manifest was not found.", fullManifestPath);
        }

        var fullToolLockPath = ResolvePath(fullRepoRoot, configuration.ToolLockPath);
        var toolLockJson = await File.ReadAllTextAsync(fullToolLockPath, ct);
        var toolLock = JsonSerializer.Deserialize<ToolLock>(toolLockJson, JsonOptions)
                       ?? throw new InvalidOperationException("Tool lock could not be deserialized.");

        using var manifestDocument = JsonDocument.Parse(await File.ReadAllTextAsync(fullManifestPath, ct));
        var allTargets = manifestDocument.RootElement.GetProperty("targets")
            .EnumerateArray()
            .Select(element => JsonSerializer.Deserialize<TargetDescriptor>(element.GetRawText(), JsonOptions)
                               ?? throw new InvalidOperationException("Target descriptor could not be deserialized."))
            .ToList();

        IReadOnlyList<TargetDescriptor> targets = selectedSubjectIds is { Count: > 0 }
            ? allTargets.Where(target => selectedSubjectIds.Contains(target.SubjectId, StringComparer.Ordinal)).ToList()
            : allTargets;
        if (targets.Count == 0)
        {
            throw new InvalidOperationException("No requested subject IDs were found in the selected manifest.");
        }

        var repetitions = selectedRepetitions is { Count: > 0 }
            ? selectedRepetitions.Distinct().OrderBy(value => value).ToList()
            : configuration.Repetitions.Distinct().OrderBy(value => value).ToList();
        if (repetitions.Count == 0)
        {
            throw new InvalidOperationException("No repetition IDs were selected.");
        }

        return new LoadedCampaign(configuration, toolLock, targets, repetitions, fullManifestPath);
    }

    public static string ResolvePath(string repoRoot, string path)
    {
        return Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(repoRoot, path));
    }
}

public sealed record CampaignExportResult(
    string RawRecordsPath,
    string PairedComparisonPath,
    string AvailabilityLedgerPath,
    PilotReports Reports,
    ReadinessReport Readiness
);

public static class CampaignExportService
{
    public static async Task<CampaignExportResult> ExportAsync(
        string campaignId,
        string sourceRecordsPath,
        string outputDirectory,
        FullChainConfiguration configuration,
        ToolLock toolLock,
        IReadOnlyList<TargetDescriptor> targets,
        IReadOnlyList<int> repetitions,
        CancellationToken ct = default)
    {
        if (!File.Exists(sourceRecordsPath))
        {
            throw new FileNotFoundException("Campaign raw-records.jsonl was not found.", sourceRecordsPath);
        }

        var records = await ReadRawRecordsAsync(sourceRecordsPath, ct);

        if (records.Count == 0)
        {
            throw new InvalidOperationException("Campaign export requires at least one raw record.");
        }

        Directory.CreateDirectory(outputDirectory);
        var targetToProjectMap = targets.ToDictionary(target => target.SubjectId, target => target.ProjectId);
        var rawRecordsPath = Path.Combine(outputDirectory, "raw-records.jsonl");
        var pairedComparisonPath = Path.Combine(outputDirectory, "paired-comparison.csv");
        var availabilityLedgerPath = Path.Combine(outputDirectory, "availability-ledger.json");

        await PairedResultWriter.WriteRawRecordsJsonlAsync(rawRecordsPath, records, ct);
        await PairedResultWriter.WritePairedComparisonCsvAsync(pairedComparisonPath, records, targetToProjectMap, ct);
        var readiness = ReadinessGate.EvaluateCampaign(targets, repetitions, records);
        await PairedResultWriter.WriteAvailabilityLedgerAsync(availabilityLedgerPath, campaignId, records, readiness, ct);

        var pairedResult = new PairedCampaignResult(
            records,
            PairedResultWriter.BuildPairedRows(records, targetToProjectMap),
            readiness,
            rawRecordsPath,
            pairedComparisonPath,
            availabilityLedgerPath);
        var reports = await PilotReportGenerator.GenerateReportsAsync(
            Path.Combine(outputDirectory, "reports"),
            campaignId,
            pairedResult,
            configuration,
            toolLock,
            targets,
            repetitions);

        return new CampaignExportResult(
            rawRecordsPath,
            pairedComparisonPath,
            availabilityLedgerPath,
            reports,
            readiness);
    }

    /// <summary>
    /// Reads both current one-line JSONL records and legacy pretty-printed
    /// concatenated JSON objects produced before the JSONL writer was fixed.
    /// </summary>
    private static async Task<List<FullChainRecord>> ReadRawRecordsAsync(string sourceRecordsPath, CancellationToken ct)
    {
        var jsonBytes = await File.ReadAllBytesAsync(sourceRecordsPath, ct);
        return ParseRawRecords(jsonBytes);
    }

    private static List<FullChainRecord> ParseRawRecords(byte[] jsonBytes)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var records = new List<FullChainRecord>();
        var json = Encoding.UTF8.GetString(jsonBytes).TrimStart('\uFEFF');
        var objectStart = -1;
        var depth = 0;
        var insideString = false;
        var escaped = false;

        for (var index = 0; index < json.Length; index++)
        {
            var current = json[index];
            if (objectStart < 0)
            {
                if (char.IsWhiteSpace(current))
                {
                    continue;
                }
                if (current != '{')
                {
                    throw new InvalidOperationException("Raw campaign records must contain consecutive JSON objects.");
                }

                objectStart = index;
                depth = 1;
                continue;
            }

            if (insideString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (current == '\\')
                {
                    escaped = true;
                }
                else if (current == '"')
                {
                    insideString = false;
                }
                continue;
            }

            if (current == '"')
            {
                insideString = true;
            }
            else if (current == '{')
            {
                depth++;
            }
            else if (current == '}')
            {
                depth--;
                if (depth == 0)
                {
                    var record = JsonSerializer.Deserialize<FullChainRecord>(json[objectStart..(index + 1)], options)
                                 ?? throw new InvalidOperationException("A raw campaign record could not be deserialized.");
                    records.Add(record);
                    objectStart = -1;
                }
            }
        }

        if (objectStart >= 0 || insideString || depth != 0)
        {
            throw new InvalidOperationException("Raw campaign records ended in an incomplete JSON object.");
        }

        return records;
    }
}
