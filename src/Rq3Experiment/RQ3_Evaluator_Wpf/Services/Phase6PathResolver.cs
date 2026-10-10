using RQ3Evaluator.Wpf.Models;

namespace RQ3Evaluator.Wpf.Services;

public static class Phase6PathResolver
{
    public static string FindWorkspaceRoot()
    {
        var candidates = new[] { @"R:\BaoVer2", Environment.CurrentDirectory, AppContext.BaseDirectory };
        foreach (var start in candidates)
        {
            if (string.IsNullOrWhiteSpace(start)) continue;
            var dir = new DirectoryInfo(start);
            for (var i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "ThucNgiem", "RQ3_Phase2")) &&
                    Directory.Exists(Path.Combine(dir.FullName, "ThucNgiem", "RQ3_Phase3_Consensus")))
                {
                    return dir.FullName;
                }
            }
        }
        return @"R:\BaoVer2";
    }

    public static Phase6Settings GetDefaultSettings(string? workspaceRoot = null)
    {
        var ws = workspaceRoot ?? FindWorkspaceRoot();
        var dataset = Path.Combine(ws, "ThucNgiem", "RQ3_Phase3_Consensus", "phase6_exploratory_v2_dataset");
        var manifest = Path.Combine(dataset, "registry", "phase6_exploratory_v2_manifest.json");
        var evidenceBundle = Path.Combine(dataset, "tool_evidence");
        var projection = evidenceBundle;
        var freeze = Path.Combine(dataset, "readiness_report.json");
        var output = Path.Combine(ws, "ThucNgiem", "RQ3_Phase3_Consensus", "wpf_phase6_runs_v2");
        var keyFile = File.Exists(Path.Combine(ws, "ApiKeyTeamorouter.txt"))
            ? Path.Combine(ws, "ApiKeyTeamorouter.txt")
            : Path.Combine(ws, "ApiKeyhatBAI.txt");

        var defaultEndpoint = "https://api.teamorouter.com/v1/chat/completions";
        var defaultModel = "deepseek-v4-pro";

        return new Phase6Settings(
            WorkspaceRoot: ws,
            DatasetRoot: dataset,
            ManifestPath: manifest,
            EvidenceBundleRoot: evidenceBundle,
            ProjectionRoot: projection,
            EvidenceFreezePath: freeze,
            OutputRoot: output,
            Endpoint: defaultEndpoint,
            Model: defaultModel,
            B1Endpoint: defaultEndpoint,
            B1Model: defaultModel,
            B2Endpoint: defaultEndpoint,
            B2Model: defaultModel,
            StabilityEndpoint: defaultEndpoint,
            StabilityModel: defaultModel,
            ApiKeyFilePath: File.Exists(keyFile) ? keyFile : "",
            RunB0: true,
            RunB1: true,
            RunB2: true,
            Score: false,
            MaxTokens: 4000,
            MaxConcurrency: 3,
            RateLimitRpm: 10,
            MaxRetries: 3,
            RequestTimeoutSec: 120
        );
    }

    public static string GetBatchRunnerDllPath(string workspaceRoot) =>
        Path.Combine(workspaceRoot, "ThucNgiem", "RQ3_Phase3_Consensus", "tools", "RQ3BatchRunner", "bin", "Release", "net8.0", "RQ3BatchRunner.dll");

    public static string GetScorerScriptPath(string workspaceRoot) =>
        Path.Combine(workspaceRoot, "ThucNgiem", "RQ3_Phase3_Consensus", "scripts", "score_exploratory_v2.ps1");

    public static string GetStabilityScriptPath(string workspaceRoot) =>
        Path.Combine(workspaceRoot, "ThucNgiem", "RQ3_Phase3_Consensus", "scripts", "run_phase6_stability_v1.ps1");
}
