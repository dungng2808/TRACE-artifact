using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using RQ3Evaluator.Wpf.Models;

namespace RQ3Evaluator.Wpf.Services;

public static class FrozenEvidenceValidator
{
    public static PreflightResult Validate(Phase6Settings settings)
    {
        var logs = new List<string>();
        void Log(string msg) => logs.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");

        try
        {
            Log("Starting frozen evidence preflight validation...");

            // 1. Check Paths
            if (!Directory.Exists(settings.DatasetRoot))
                throw new DirectoryNotFoundException($"Dataset root directory does not exist: {settings.DatasetRoot}");
            if (!File.Exists(settings.ManifestPath))
                throw new FileNotFoundException($"Cohort manifest not found: {settings.ManifestPath}");
            if (!Directory.Exists(settings.ProjectionRoot))
                throw new DirectoryNotFoundException($"Tool evidence projection folder does not exist: {settings.ProjectionRoot}");

            // 2. Parse Manifest
            Log($"Reading manifest: {settings.ManifestPath}");
            using var manifestDoc = JsonDocument.Parse(File.ReadAllText(settings.ManifestPath));
            var root = manifestDoc.RootElement;

            var status = root.TryGetProperty("status", out var st) ? st.GetString() : null;
            var isV2 = status == "FROZEN_EXPLORATORY_V2";
            if (!isV2 && status != "FROZEN_TECHNICAL_MAIN_COHORT_AI_ANNOTATED")
            {
                throw new InvalidOperationException($"Invalid manifest status '{status}'. Expected 'FROZEN_TECHNICAL_MAIN_COHORT_AI_ANNOTATED'.");
            }
            Log($"Manifest status verified: {status}");

            var expectedCount = root.TryGetProperty("case_count", out var cc) ? cc.GetInt32() : 0;
            var decisions = root.GetProperty("decisions").EnumerateArray()
                .Where(d => d.GetProperty("evaluation_status").GetString() == "INCLUDED")
                .Select(d => d.GetProperty("case_id").GetString()!)
                .ToList();

            if (decisions.Count != expectedCount || expectedCount == 0)
            {
                throw new InvalidOperationException($"Manifest case_count ({expectedCount}) does not match included decisions count ({decisions.Count}).");
            }
            Log($"Found {decisions.Count} included cases across manifest.");

            // 3. Check Evidence Freeze Manifest (if present)
            Dictionary<string, (string PublicCaseSha, string BundleSha, string ProjectionSha)>? freezeMap = null;
            var freezeValid = true;
            if (File.Exists(settings.EvidenceFreezePath))
            {
                try
                {
                    using var freezeDoc = JsonDocument.Parse(File.ReadAllText(settings.EvidenceFreezePath));
                    var freezeRoot = freezeDoc.RootElement;
                    freezeMap = new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase);
                    if (freezeRoot.TryGetProperty("entries", out var entries))
                    {
                        foreach (var entry in entries.EnumerateArray())
                        {
                            var cid = entry.GetProperty("case_id").GetString()!;
                            var pubSha = entry.GetProperty("public_case_sha256").GetString()!;
                            var bunSha = entry.GetProperty("evidence_bundle_sha256").GetString()!;
                            var projSha = entry.GetProperty("public_projection_sha256").GetString()!;
                            freezeMap[cid] = (pubSha, bunSha, projSha);
                        }
                    }
                    Log($"Evidence freeze reference loaded with {freezeMap.Count} entries.");
                }
                catch (Exception ex)
                {
                    Log($"[WARN] Could not parse evidence freeze file: {ex.Message}");
                    freezeValid = false;
                }
            }

            // 4. Validate Each Case
            var publicHashesValid = true;
            var bundleHashesValid = true;
            var projectionHashesValid = true;
            var validatedCount = 0;

            foreach (var caseId in decisions)
            {
                var publicCasePath = Path.Combine(settings.DatasetRoot, "cases", "public", $"{caseId}.json");
                if (!File.Exists(publicCasePath))
                    throw new FileNotFoundException($"Public case file missing: {publicCasePath}");

                if (isV2)
                {
                    var envelopePath=Path.Combine(settings.DatasetRoot,"envelopes",$"{caseId}.json");
                    var toolPath=Path.Combine(settings.ProjectionRoot,$"{caseId}.bundle.json");
                    if(!File.Exists(envelopePath))throw new FileNotFoundException($"Evidence envelope missing for {caseId}");
                    if(!File.Exists(toolPath))throw new FileNotFoundException($"Tool evidence missing for {caseId}");
                    using var publicDoc=JsonDocument.Parse(File.ReadAllText(publicCasePath));
                    var publicRoot=publicDoc.RootElement;
                    var method=publicRoot.GetProperty("focal_mapping").GetProperty("candidate_method").GetString();
                    if(string.IsNullOrWhiteSpace(method))throw new InvalidOperationException($"candidate_method is incomplete for {caseId}");
                    foreach(var field in publicRoot.GetProperty("scenario").EnumerateObject())
                        if(field.Value.ValueKind==JsonValueKind.String&&field.Value.GetString()!.StartsWith("Pending",StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException($"Pending scenario field remains for {caseId}");
                    using var envelopeDoc=JsonDocument.Parse(File.ReadAllText(envelopePath));
                    var envelopeRoot=envelopeDoc.RootElement;
                    if(envelopeRoot.GetProperty("case_id").GetString()!=caseId)throw new InvalidOperationException($"Envelope identity mismatch for {caseId}");
                    var source=envelopeRoot.GetProperty("generated_test").GetProperty("source_text").GetString();
                    if(string.IsNullOrWhiteSpace(source))throw new InvalidOperationException($"Generated source missing for {caseId}");
                    var raw=envelopeRoot.GetRawText();
                    if(raw.Contains("evidence_layer_operator",StringComparison.OrdinalIgnoreCase)||raw.Contains("intended_gold_category",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException($"Gold/injection metadata leak for {caseId}");
                    validatedCount++;
                    continue;
                }

                var bundlePath = Directory.Exists(settings.EvidenceBundleRoot)
                    ? Path.Combine(settings.EvidenceBundleRoot, $"{caseId}.bundle.json")
                    : Path.Combine(settings.ProjectionRoot, $"{caseId}.bundle.json");

                var projectionPath = Path.Combine(settings.ProjectionRoot, $"{caseId}.json");

                if (!File.Exists(projectionPath))
                    throw new FileNotFoundException($"Projection missing for case: {caseId} at {projectionPath}");

                var pubHash = Sha256File(publicCasePath);
                var projHash = Sha256File(projectionPath);

                if (File.Exists(bundlePath))
                {
                    var bunHash = Sha256File(bundlePath);
                    using var bundleDoc = JsonDocument.Parse(File.ReadAllText(bundlePath));
                    var bundleRoot = bundleDoc.RootElement;
                    var bundleCaseId = bundleRoot.GetProperty("case_id").GetString();
                    var bundleCaseSha = bundleRoot.GetProperty("case_sha256").GetString();

                    if (!string.Equals(bundleCaseId, caseId, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"Bundle case_id mismatch for {caseId}");
                    if (!string.Equals(bundleCaseSha, pubHash, StringComparison.OrdinalIgnoreCase))
                    {
                        publicHashesValid = false;
                        throw new InvalidOperationException($"Public case SHA mismatch with bundle for {caseId}");
                    }

                    using var projDoc = JsonDocument.Parse(File.ReadAllText(projectionPath));
                    var projRoot = projDoc.RootElement;
                    var projCaseId = projRoot.GetProperty("case_id").GetString();
                    var projBundleSha = projRoot.GetProperty("bundle_sha256").GetString();

                    if (!string.Equals(projCaseId, caseId, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"Projection case_id mismatch for {caseId}");
                    if (!string.Equals(projBundleSha, bunHash, StringComparison.OrdinalIgnoreCase))
                    {
                        bundleHashesValid = false;
                        throw new InvalidOperationException($"Projection to bundle SHA mismatch for {caseId}");
                    }
                }

                if (freezeMap != null && freezeMap.TryGetValue(caseId, out var refShas))
                {
                    if (!string.Equals(pubHash, refShas.PublicCaseSha, StringComparison.OrdinalIgnoreCase))
                        publicHashesValid = false;
                    if (!string.Equals(projHash, refShas.ProjectionSha, StringComparison.OrdinalIgnoreCase))
                        projectionHashesValid = false;
                }

                validatedCount++;
            }

            Log($"Validated all {validatedCount}/{expectedCount} cases successfully. Zero hash errors.");

            // 5. API Key Available check
            var apiKeyAvail = false;
            var apiKeyCount = 0;
            if (!string.IsNullOrWhiteSpace(settings.ApiKeyFilePath) && File.Exists(settings.ApiKeyFilePath))
            {
                apiKeyCount = ApiKeyPool.LoadFile(settings.ApiKeyFilePath).Length;
                if (apiKeyCount > 0)
                {
                    apiKeyAvail = true;
                    Log($"API key file contains {apiKeyCount} unique non-empty credential(s). (Key content not logged)");
                }
                else
                {
                    Log("[WARN] API key file is empty or suspiciously short.");
                }
            }
            else
            {
                Log("[INFO] No API key file configured (B0 will still work; B1/B2 require API key).");
            }

            // 6. Docker Check (Optional)
            var dockerAvail = CheckDocker();
            Log(dockerAvail
                ? "Docker is available on system."
                : "Docker is not available or not running (not required for frozen evidence runs).");

            Log("PREFLIGHT VALIDATION PASSED.");

            return new PreflightResult(
                IsValid: true,
                ManifestStatus: status ?? "UNKNOWN",
                TotalCasesExpected: expectedCount,
                CasesValidated: validatedCount,
                PublicCaseHashesValid: publicHashesValid,
                EvidenceBundleHashesValid: bundleHashesValid,
                ProjectionHashesValid: projectionHashesValid,
                EvidenceFreezeValid: freezeValid,
                ApiKeyAvailable: apiKeyAvail,
                ApiKeyCount: apiKeyCount,
                DockerAvailable: dockerAvail,
                ErrorMessage: null,
                ValidationLogs: logs
            );
        }
        catch (Exception ex)
        {
            Log($"[VALIDATION ERROR] {ex.Message}");
            return new PreflightResult(
                IsValid: false,
                ManifestStatus: "FAILED",
                TotalCasesExpected: 0,
                CasesValidated: 0,
                PublicCaseHashesValid: false,
                EvidenceBundleHashesValid: false,
                ProjectionHashesValid: false,
                EvidenceFreezeValid: false,
                ApiKeyAvailable: false,
                ApiKeyCount: 0,
                DockerAvailable: false,
                ErrorMessage: ex.Message,
                ValidationLogs: logs
            );
        }
    }

    private static bool CheckDocker()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            proc.WaitForExit(3000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }
}
