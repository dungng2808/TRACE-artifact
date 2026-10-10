using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using RQ3Evaluator.Wpf.Models;

namespace RQ3Evaluator.Wpf.Services;

public static class ArtifactReaderService
{
    public static string ComputeSha256(string filePath)
    {
        if (!File.Exists(filePath)) return "";
        using var stream = File.OpenRead(filePath);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }

    public static List<ArtifactSummary> GetRunArtifacts(string runFolder)
    {
        var list = new List<ArtifactSummary>();
        if (!Directory.Exists(runFolder)) return list;

        var knownFiles = new (string RelPath, string Name, string Desc)[]
        {
            ("predictions/predictions.json", "predictions.json", "Raw model and tool gate prediction rows"),
            ("predictions/prediction_freeze_v2.json", "prediction_freeze_v2.json", "Tamper-evident freeze certificate prior to gold join"),
            ("predictions/checkpoint_predictions.json", "checkpoint_predictions.json", "Incremental execution checkpoint"),
            ("results/metrics_v2.json", "metrics_v2.json", "Protocol-v2 metrics including false-pass F1 and paired inference"),
            ("results/predictions_joined_private_gold_v2.csv", "predictions_joined_private_gold_v2.csv", "Joined predictions with private gold annotations"),
            ("results/phase6_result_hashes_v1.json", "phase6_result_hashes_v1.json", "Audit hashes of predictions, metrics, and CSV"),
            ("stability/phase6_stability_v1.json", "phase6_stability_v1.json", "Stability 3-repetition agreement evaluation"),
            ("stability/phase6_stability_hash_v1.json", "phase6_stability_hash_v1.json", "Audit hash of stability report"),
            ("run_manifest.json", "run_manifest.json", "WPF run parameters and evidence hashes (no API key)")
        };

        foreach (var (relPath, name, desc) in knownFiles)
        {
            var fullPath = Path.Combine(runFolder, relPath);
            if (File.Exists(fullPath))
            {
                var fi = new FileInfo(fullPath);
                var sha = ComputeSha256(fullPath);
                list.Add(new ArtifactSummary(name, relPath, fullPath, true, fi.Length, sha, desc));
            }
            else
            {
                list.Add(new ArtifactSummary(name, relPath, fullPath, false, 0, null, desc));
            }
        }

        return list;
    }

    public static List<PredictionDisplayRow> ReadJoinedCsv(string csvPath)
    {
        var rows = new List<PredictionDisplayRow>();
        if (!File.Exists(csvPath)) return rows;

        var lines = File.ReadAllLines(csvPath);
        if (lines.Length <= 1) return rows;

        var header = ParseCsvLine(lines[0]);
        var colMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < header.Length; i++)
        {
            colMap[header[i].Trim('"')] = i;
        }

        string GetVal(string[] parts, string colName)
        {
            if (colMap.TryGetValue(colName, out var idx) && idx < parts.Length)
            {
                return parts[idx].Trim('"');
            }
            return "";
        }

        bool? GetBool(string[] parts, string colName)
        {
            var v = GetVal(parts, colName);
            if (bool.TryParse(v, out var b)) return b;
            return null;
        }

        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = ParseCsvLine(line);

            double.TryParse(GetVal(parts, "cost_usd"), NumberStyles.Any, CultureInfo.InvariantCulture, out var cost);

            rows.Add(new PredictionDisplayRow(
                CaseId: GetVal(parts, "case_id"),
                Project: GetVal(parts, "project"),
                Origin: GetVal(parts, "case_origin"),
                Verifier: GetVal(parts, "verifier"),
                Decision: GetVal(parts, "decision"),
                Cause: GetVal(parts, "cause"),
                Route: GetVal(parts, "route"),
                ExecutionStatus: GetVal(parts, "execution_status"),
                CostUsd: cost,
                GoldCategory: GetVal(parts, "gold_category"),
                GoldDecision: GetVal(parts, "gold_decision"),
                DecisionCorrect: GetBool(parts, "decision_correct"),
                CauseCorrect: GetBool(parts, "cause_correct"),
                RouteCorrect: GetBool(parts, "route_correct")
            ));
        }

        return rows;
    }

    private static string[] ParseCsvLine(string line)
    {
        var list = new List<string>();
        var inQuotes = false;
        var sb = new System.Text.StringBuilder();

        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
                sb.Append(c);
            }
            else if (c == ',' && !inQuotes)
            {
                list.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }
        list.Add(sb.ToString());
        return list.ToArray();
    }
}
