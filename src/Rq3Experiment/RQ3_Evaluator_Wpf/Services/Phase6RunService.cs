using System.Security.Cryptography;
using System.Text.Json;
using RQ3Evaluator.Wpf.Models;

namespace RQ3Evaluator.Wpf.Services;

public sealed class Phase6RunService
{
    private readonly ProcessExecutionService _execService = new();

    public ProcessExecutionService ProcessService => _execService;

    public string CreateRunDirectory(string outputRoot, string b1Model, string b2Model)
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var b1Tag = NormalizeModelTag(b1Model);
        var b2Tag = NormalizeModelTag(b2Model);
        var modelTag = string.Equals(b1Tag, b2Tag, StringComparison.OrdinalIgnoreCase)
            ? b1Tag
            : $"B1_{b1Tag}_B2_{b2Tag}";
        var runFolder = Path.Combine(
            outputRoot,
            $"KQ_Thucnghiem_VerHeThong2_{modelTag}_{timestamp}");
        Directory.CreateDirectory(runFolder);
        Directory.CreateDirectory(Path.Combine(runFolder, "predictions"));
        Directory.CreateDirectory(Path.Combine(runFolder, "results"));
        Directory.CreateDirectory(Path.Combine(runFolder, "stability"));
        return runFolder;
    }

    private static string NormalizeModelTag(string? model)
    {
        var value = string.IsNullOrWhiteSpace(model) ? "unknown_model" : model.Trim();
        var chars = value
            .Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_')
            .ToArray();
        var normalized = string.Join('_', new string(chars)
            .Split('_', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(normalized) ? "unknown_model" : normalized;
    }

    public async Task<int> RunPredictionsAsync(
        Phase6Settings settings,
        string runFolder,
        bool isResume,
        Action<string> onLog,
        CancellationToken ct)
    {
        var predictionsDir = Path.Combine(runFolder, "predictions");
        Directory.CreateDirectory(predictionsDir);

        var runnerDll = Phase6PathResolver.GetBatchRunnerDllPath(settings.WorkspaceRoot);
        if (!File.Exists(runnerDll))
        {
            throw new FileNotFoundException($"RQ3BatchRunner.dll not found at: {runnerDll}. Please build RQ3BatchRunner first.");
        }

        var args = new List<string>
        {
            runnerDll,
            $"--dataset={settings.DatasetRoot}",
            $"--out={runFolder}",
            $"--run-dir={predictionsDir}",
            $"--manifest={settings.ManifestPath}",
            $"--evidence-root={settings.ProjectionRoot}",
            $"--endpoint={settings.Endpoint}",
            $"--model={settings.Model}",
            $"--b1-model={settings.B1Model}",
            $"--b2-model={settings.B2Model}",
            $"--b1-endpoint={settings.B1Endpoint}",
            $"--b2-endpoint={settings.B2Endpoint}"
        };

        if (!string.IsNullOrWhiteSpace(settings.ApiKeyFilePath) && File.Exists(settings.ApiKeyFilePath))
        {
            args.Add($"--api-key-file={settings.ApiKeyFilePath}");
        }

        args.Add($"--max-tokens={settings.MaxTokens}");
        args.Add($"--max-concurrency={settings.MaxConcurrency}");
        args.Add($"--rate-limit-rpm={settings.RateLimitRpm}");
        args.Add($"--max-retries={settings.MaxRetries}");
        args.Add($"--timeout-sec={settings.RequestTimeoutSec}");

        if (settings.RunB0) args.Add("--b0");
        if (settings.RunB1) args.Add("--b1");
        if (settings.RunB2) args.Add("--b2");
        if (isResume) args.Add("--resume");

        onLog($"[RUNNER] Launching dotnet {runnerDll} with clean argument isolation.");
        onLog($"[RUNNER] Predictions target: {predictionsDir}");

        var exitCode = await _execService.ExecuteAsync(
            "dotnet",
            args,
            settings.WorkspaceRoot,
            line => onLog(line),
            err => onLog($"[STDERR] {err}"),
            ct
        );

        return exitCode;
    }

    public bool FreezePredictions(Phase6Settings settings, string runFolder, Action<string> onLog)
    {
        var predFile = Path.Combine(runFolder, "predictions", "predictions.json");
        if (!File.Exists(predFile))
        {
            onLog($"[FREEZE ERROR] predictions.json not found in {predFile}");
            return false;
        }

        var predSha = ArtifactReaderService.ComputeSha256(predFile);
        var freezeFile = Path.Combine(runFolder, "predictions", "prediction_freeze_v2.json");

        var lines = File.ReadAllText(predFile);
        using var doc = JsonDocument.Parse(lines);
        var rows = doc.RootElement.GetArrayLength();
        var expectedCases = 73;
        if (File.Exists(settings.ManifestPath))
        {
            using var manifestDoc=JsonDocument.Parse(File.ReadAllText(settings.ManifestPath));
            if(manifestDoc.RootElement.TryGetProperty("decisions",out var decisions)&&decisions.ValueKind==JsonValueKind.Array)
                expectedCases=decisions.EnumerateArray().Count(x=>!x.TryGetProperty("evaluation_status",out var status)||status.GetString()=="INCLUDED");
        }
        var expectedVerifiers=(settings.RunB0?1:0)+(settings.RunB1?1:0)+(settings.RunB2?1:0);
        var expectedRows=expectedCases*expectedVerifiers;
        var items=doc.RootElement.EnumerateArray().ToArray();
        var uniqueKeys=items.Select(x=>$"{x.GetProperty("CaseId").GetString()}|{x.GetProperty("Verifier").GetString()}").Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var invalid=items.Count(x=>!x.TryGetProperty("ExecutionStatus",out var status)||status.GetString()!="OK");
        var hashMismatch=items.GroupBy(x=>x.GetProperty("CaseId").GetString()??"").Any(g=>g.Select(x=>x.TryGetProperty("EvidenceEnvelopeHash",out var h)?h.GetString():null).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=1);
        if(rows!=expectedRows||uniqueKeys!=rows||invalid>0||hashMismatch)
        {
            onLog($"[FREEZE ERROR] Completion gate failed: rows={rows}/{expectedRows}, unique={uniqueKeys}, invalid={invalid}, hashMismatch={hashMismatch}.");
            return false;
        }

        var evidenceFreezeSha = File.Exists(settings.EvidenceFreezePath)
            ? ArtifactReaderService.ComputeSha256(settings.EvidenceFreezePath)
            : "";

        var freezeObj = new
        {
            schema_version = "rq3-exploratory-prediction-freeze-v2",
            frozen_at = DateTimeOffset.UtcNow.ToString("o"),
            predictions_sha256 = predSha,
            prediction_rows = rows,
            completion_gate = new { expected_rows = expectedRows, unique_rows = uniqueKeys, invalid_outputs = invalid, envelope_hashes_consistent = !hashMismatch },
            evidence_freeze_sha256 = evidenceFreezeSha,
            private_gold_joined = false,
            note = "Predictions finalized and hashed before any private gold inspection."
        };

        var temp = freezeFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(freezeObj, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, freezeFile, true);

        onLog($"[PREDICTION FREEZE] Successfully created prediction_freeze_v2.json");
        onLog($"[PREDICTION FREEZE] SHA-256: {predSha} ({rows} prediction rows)");
        return true;
    }

    public async Task<int> RunScoringAsync(
        Phase6Settings settings,
        string runFolder,
        Action<string> onLog,
        CancellationToken ct)
    {
        var predFile = Path.Combine(runFolder, "predictions", "predictions.json");
        var freezeFile = Path.Combine(runFolder, "predictions", "prediction_freeze_v2.json");
        var resultsDir = Path.Combine(runFolder, "results");
        Directory.CreateDirectory(resultsDir);

        if (!File.Exists(freezeFile))
        {
            throw new InvalidOperationException("Cannot score: prediction_freeze_v2.json is missing. Freeze predictions first.");
        }

        var scorerScript = Phase6PathResolver.GetScorerScriptPath(settings.WorkspaceRoot);
        if (!File.Exists(scorerScript))
        {
            throw new FileNotFoundException($"Scorer script not found at: {scorerScript}");
        }

        var args = new List<string>
        {
            "-NoProfile",
            "-ExecutionPolicy", "Bypass",
            "-File", scorerScript,
            "-PredictionsJson", predFile,
            "-DatasetRoot", settings.DatasetRoot,
            "-OutputRoot", resultsDir
        };

        onLog($"[SCORER] Executing PowerShell scorer script: {scorerScript}");

        var exitCode = await _execService.ExecuteAsync(
            "powershell.exe",
            args,
            settings.WorkspaceRoot,
            line => onLog(line),
            err => onLog($"[STDERR] {err}"),
            ct
        );

        return exitCode;
    }

    public async Task<int> RunStabilityAsync(
        Phase6Settings settings,
        string runFolder,
        Action<string> onLog,
        CancellationToken ct)
    {
        var predFile = Path.Combine(runFolder, "predictions", "predictions.json");
        var stabilityDir = Path.Combine(runFolder, "stability");
        Directory.CreateDirectory(stabilityDir);

        if (!File.Exists(predFile))
        {
            throw new InvalidOperationException("Cannot run stability: predictions.json is missing.");
        }

        var stabilityScript = Phase6PathResolver.GetStabilityScriptPath(settings.WorkspaceRoot);
        if (!File.Exists(stabilityScript))
        {
            throw new FileNotFoundException($"Stability script not found at: {stabilityScript}");
        }

        var args = new List<string>
        {
            "-NoProfile",
            "-ExecutionPolicy", "Bypass",
            "-File", stabilityScript,
            "-MainPredictionsJson", predFile,
            "-WorkspaceRoot", settings.WorkspaceRoot,
            "-Manifest", settings.ManifestPath,
            "-EvidenceRoot", settings.ProjectionRoot,
            "-OutputRoot", stabilityDir,
            "-Model", settings.StabilityModel,
            "-Endpoint", settings.StabilityEndpoint
        };

        if (!string.IsNullOrWhiteSpace(settings.ApiKeyFilePath) && File.Exists(settings.ApiKeyFilePath))
        {
            args.Add("-ApiKeyFile");
            args.Add(settings.ApiKeyFilePath);
        }

        onLog($"[STABILITY] Executing PowerShell stability runner: {stabilityScript}");

        var exitCode = await _execService.ExecuteAsync(
            "powershell.exe",
            args,
            settings.WorkspaceRoot,
            line => onLog(line),
            err => onLog($"[STDERR] {err}"),
            ct
        );

        return exitCode;
    }
}
