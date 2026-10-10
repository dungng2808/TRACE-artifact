using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RQ3Evaluator.Wpf;

namespace RQ3BatchRunner;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var opt = Parse(args);
            var dataset = Need(opt, "dataset");
            var output = Need(opt, "out");
            var endpoint = opt.GetValueOrDefault("endpoint") ?? "https://openrouter.ai/api/v1/chat/completions";
            var model = opt.GetValueOrDefault("model") ?? "google/gemini-2.5-flash";
            var b1Model = opt.GetValueOrDefault("b1-model") ?? model;
            var b2Model = opt.GetValueOrDefault("b2-model") ?? model;
            var b1Endpoint = opt.GetValueOrDefault("b1-endpoint") ?? endpoint;
            var b2Endpoint = opt.GetValueOrDefault("b2-endpoint") ?? endpoint;

            var keyEnv = opt.GetValueOrDefault("api-key-env");
            var keyFile = opt.GetValueOrDefault("api-key-file");
            var runDirOpt = opt.GetValueOrDefault("run-dir");
            var isResume = opt.ContainsKey("resume");

            var maxTokens = int.TryParse(opt.GetValueOrDefault("max-tokens"), out var mt) ? mt : 2000;
            var maxConcurrency = int.TryParse(opt.GetValueOrDefault("max-concurrency"), out var mc) ? mc : 5;
            var caseConcurrency = int.TryParse(opt.GetValueOrDefault("case-concurrency"), out var cc) ? Math.Max(1, cc) : 1;
            var rateLimitRpm = int.TryParse(opt.GetValueOrDefault("rate-limit-rpm"), out var rpm) ? rpm : 0;
            var maxRetries = int.TryParse(opt.GetValueOrDefault("max-retries"), out var mr) ? mr : 3;
            var timeoutSec = int.TryParse(opt.GetValueOrDefault("timeout-sec"), out var to) ? to : 120;

            var apiKeys = !string.IsNullOrWhiteSpace(keyFile) && File.Exists(keyFile)
                ? ApiKeyPool.LoadFile(keyFile)
                : string.IsNullOrWhiteSpace(keyEnv) ? Array.Empty<string>() : new[]{Environment.GetEnvironmentVariable(keyEnv)?.Trim()??""}.Where(x=>x.Length>0).ToArray();

            var runB1 = opt.ContainsKey("b1");
            var runB2 = opt.ContainsKey("b2");
            var runB0 = opt.ContainsKey("b0") || (!runB1 && !runB2) || (opt.ContainsKey("b1") && opt.ContainsKey("b2")); // default include B0
            var score = opt.ContainsKey("score");

            if (runB1 && (string.IsNullOrWhiteSpace(b1Endpoint) || string.IsNullOrWhiteSpace(b1Model) || apiKeys.Length==0))
            {
                throw new ArgumentException("B1 requires valid endpoint, model, and api-key.");
            }
            if (runB2 && (string.IsNullOrWhiteSpace(b2Endpoint) || string.IsNullOrWhiteSpace(b2Model) || apiKeys.Length==0))
            {
                throw new ArgumentException("B2 requires valid endpoint, model, and api-key.");
            }

            string runDir;
            if (!string.IsNullOrWhiteSpace(runDirOpt))
            {
                runDir = runDirOpt;
            }
            else
            {
                runDir = Path.Combine(output, "RQ3_Batch_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss"));
            }
            Directory.CreateDirectory(runDir);

            var selectedCase = opt.GetValueOrDefault("case");
            var selectedCases = (opt.GetValueOrDefault("cases") ?? "").Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
            var manifest = opt.GetValueOrDefault("manifest");
            var evidenceRoot = opt.GetValueOrDefault("evidence-root");
            var cases = DatasetService.Load(dataset, manifest, evidenceRoot);
            var isProtocolV2 = Directory.Exists(Path.Combine(dataset, "envelopes"));
            if (isProtocolV2 && runB1 && runB2 && (!string.Equals(b1Model,b2Model,StringComparison.Ordinal) || !string.Equals(b1Endpoint,b2Endpoint,StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException("RQ3 protocol v2 requires B1 and B2 to use the same model and endpoint.");
            }
            if((runB1||runB2)&&new Uri(endpoint).Host.Equals("generativelanguage.googleapis.com",StringComparison.OrdinalIgnoreCase))
            {
                await ValidateGoogleKeyPoolAsync(endpoint,model,apiKeys);
            }
            if (!string.IsNullOrWhiteSpace(selectedCase))
            {
                cases = cases.Where(x => x.CaseId == selectedCase).ToArray();
            }
            else if(selectedCases.Length>0)
            {
                var selectedSet=new HashSet<string>(selectedCases,StringComparer.OrdinalIgnoreCase);
                cases=cases.Where(x=>selectedSet.Contains(x.CaseId)).ToArray();
            }
            if (cases.Count == 0)
            {
                throw new ArgumentException("Selected case was not found in the dataset.");
            }

            var checkpointPath = Path.Combine(runDir, "checkpoint_predictions.json");
            var keySummaryPath = Path.Combine(runDir,"api_key_pool_summary.json");
            var priorKeySummary = isResume && File.Exists(keySummaryPath)
                ? JsonSerializer.Deserialize<List<ApiKeyUsageSnapshot>>(await File.ReadAllTextAsync(keySummaryPath)) ?? new()
                : new List<ApiKeyUsageSnapshot>();
            var resultByKey = new Dictionary<string,Prediction>(StringComparer.OrdinalIgnoreCase);
            var completedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (isResume && File.Exists(checkpointPath))
            {
                try
                {
                    var existingJson = await File.ReadAllTextAsync(checkpointPath);
                    var existingList = JsonSerializer.Deserialize<List<Prediction>>(existingJson);
                    if (existingList is not null)
                    {
                        foreach (var p in existingList)
                        {
                            var key = $"{p.CaseId}|{p.Verifier}";
                            if(!resultByKey.TryGetValue(key,out var current)||current.ExecutionStatus!="OK"||p.ExecutionStatus=="OK")resultByKey[key]=p;
                            if(p.ExecutionStatus=="OK")completedKeys.Add(key);
                        }
                        Console.WriteLine($"[RESUME] Loaded {completedKeys.Count} valid predictions; invalid rows will be rerun.");
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[RESUME WARN] Failed to parse checkpoint: {ex.Message}. Starting fresh.");
                    resultByKey.Clear();
                    completedKeys.Clear();
                }
            }

            var config = new RunOptions(dataset, runDir, runB0, runB1, runB2, score, endpoint, model, apiKeys, b1Model, b2Model, b1Endpoint, b2Endpoint, maxTokens, maxConcurrency, rateLimitRpm, maxRetries, timeoutSec);
            var llm = runB1||runB2?new LlmEvaluator(config):null;

            var requiredVerifiers=new List<string>();
            if(runB0)requiredVerifiers.Add("B0_BASIC_TOOL_GATE");
            if(runB1)requiredVerifiers.Add("B1_GENERAL_REVIEWER");
            if(runB2)requiredVerifiers.Add("B2_SPECIALIZED_MULTI_AGENT");
            var initialKeys=new HashSet<string>(completedKeys,StringComparer.OrdinalIgnoreCase);
            bool CaseComplete(CaseInput c)=>requiredVerifiers.All(v=>initialKeys.Contains($"{c.CaseId}|{v}"));
            var alreadyCompleted=cases.Count(CaseComplete);
            var pendingCases=cases.Where(c=>!CaseComplete(c)).ToArray();
            var stateLock=new object();
            var checkpointLock=new SemaphoreSlim(1,1);
            var progress=alreadyCompleted;
            await Parallel.ForEachAsync(pendingCases,new ParallelOptions{MaxDegreeOfParallelism=caseConcurrency},async(c,ct)=>
            {
                var additions=new List<Prediction>();
                if(runB0&&!initialKeys.Contains($"{c.CaseId}|B0_BASIC_TOOL_GATE"))additions.Add(B0ToolGate.Evaluate(c));
                if(runB1&&!initialKeys.Contains($"{c.CaseId}|B1_GENERAL_REVIEWER"))additions.Add(await llm!.B1(c,ct));
                if(runB2&&!initialKeys.Contains($"{c.CaseId}|B2_SPECIALIZED_MULTI_AGENT"))additions.Add(await llm!.B2(c,ct));
                lock(stateLock)
                {
                    foreach(var prediction in additions)
                    {
                        var key=$"{prediction.CaseId}|{prediction.Verifier}";
                        resultByKey[key]=prediction;
                        if(prediction.ExecutionStatus=="OK")completedKeys.Add(key);else completedKeys.Remove(key);
                    }
                }
                await checkpointLock.WaitAsync(ct);
                try
                {
                    List<Prediction> snapshot;
                    lock(stateLock){snapshot=resultByKey.Values.ToList();}
                    await WriteJsonAtomicAsync(checkpointPath,snapshot);
                }
                finally{checkpointLock.Release();}
                var current=Interlocked.Increment(ref progress);
                Console.WriteLine($"[PROGRESS] Completed case ({current}/{cases.Count}): {c.CaseId}");
            });

            var caseOrder=cases.Select((c,i)=>(c.CaseId,i)).ToDictionary(x=>x.CaseId,x=>x.i,StringComparer.OrdinalIgnoreCase);
            var results=resultByKey.Values.OrderBy(x=>caseOrder[x.CaseId]).ThenBy(x=>x.Verifier).ToList();

            // Save final predictions.json
            var finalPredPath = Path.Combine(runDir, "predictions.json");
            await WriteJsonAtomicAsync(finalPredPath, results);
            var expectedRows=cases.Count*requiredVerifiers.Count;
            var hashesValid=results.GroupBy(x=>x.CaseId).All(g=>g.Select(x=>x.EvidenceEnvelopeHash).Distinct(StringComparer.OrdinalIgnoreCase).Count()==1&&g.First().EvidenceEnvelopeHash==cases.Single(c=>c.CaseId==g.Key).EvidenceEnvelopeHash);
            var invalidCount=results.Count(x=>x.ExecutionStatus!="OK");
            var complete=results.Count==expectedRows&&invalidCount==0&&hashesValid;
            Console.WriteLine(complete?$"[COMPLETE] Finalized {results.Count} valid predictions into {finalPredPath}":$"[INCOMPLETE] Finalized {results.Count} rows with {invalidCount} invalid outputs; resume is required.");

            var scored = score
                ? results.Select(p =>
                {
                    var c = cases.Single(x => x.CaseId == p.CaseId);
                    return (p, DatasetService.ReadGold(c));
                }).ToList()
                : new List<(Prediction, Gold)>();

            ExcelExporter.Export(runDir, results, scored);

            var hashes = cases.Select(c => new
            {
                case_id = c.CaseId,
                evidence_envelope_sha256 = c.EvidenceEnvelopeHash,
                b0_envelope_sha256 = c.EvidenceEnvelopeHash,
                b1_envelope_sha256 = c.EvidenceEnvelopeHash,
                b2_envelope_sha256 = c.EvidenceEnvelopeHash
            }).ToArray();

            var runManifest = new
            {
                schema_version = isProtocolV2 ? "rq3-batch-manifest-v2" : "rq3-batch-manifest-v1.3",
                created_at = DateTimeOffset.UtcNow,
                dataset,
                manifest = manifest ?? Path.Combine(dataset, "registry", "phase2_frozen_manifest_v1.json"),
                evidence_root = evidenceRoot ?? Path.Combine(dataset, "tool_evidence"),
                methods = new { b0 = runB0, b1 = runB1, b2 = runB2 },
                model = runB1 || runB2 ? model : null,
                b1_model = runB1 ? b1Model : null,
                b1_endpoint = runB1 ? b1Endpoint : null,
                b2_model = runB2 ? b2Model : null,
                b2_endpoint = runB2 ? b2Endpoint : null,
                credential_pool = new { count = apiKeys.Length, strategy = "ROUND_ROBIN_WITH_COOLDOWN", identifiers = apiKeys.Select((_,i)=>$"K{i+1:00}").ToArray() },
                completion = new { expected_rows = expectedRows, actual_rows = results.Count, invalid_outputs = invalidCount, envelope_hashes_valid = hashesValid, complete },
                score,
                evidence_hashes = hashes
            };

            await WriteJsonAtomicAsync(Path.Combine(runDir, "run_manifest.json"), runManifest);
            var currentKeySummary=llm?.GetKeyPoolSummary()??Array.Empty<ApiKeyUsageSnapshot>();
            var cumulativeKeySummary=currentKeySummary.Select(current=>
            {
                var prior=priorKeySummary.FirstOrDefault(x=>x.Id==current.Id);
                return prior is null?current:new ApiKeyUsageSnapshot(current.Id,prior.Requests+current.Requests,prior.Successes+current.Successes,prior.RateLimited+current.RateLimited,prior.Disabled+current.Disabled,prior.TransientFailures+current.TransientFailures,prior.InputTokens+current.InputTokens,prior.OutputTokens+current.OutputTokens,prior.CostUsd+current.CostUsd,current.State,current.NextAvailableAt);
            }).ToArray();
            await WriteJsonAtomicAsync(keySummaryPath,cumulativeKeySummary);
            Console.WriteLine(runDir);
            return complete?0:2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FATAL ERROR] {ex.Message}");
            return 1;
        }
    }

    private static async Task WriteJsonAtomicAsync<T>(string targetPath, T data)
    {
        var temp = targetPath + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, targetPath, true);
    }

    private static Dictionary<string, string> Parse(string[] values)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var x in values)
        {
            if (x.StartsWith("--"))
            {
                var p = x[2..].Split('=', 2);
                d[p[0]] = p.Length == 2 ? p[1] : "true";
            }
        }
        return d;
    }

    private static string Need(Dictionary<string, string> d, string key) =>
        d.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException("Missing --" + key);

    private static async Task ValidateGoogleKeyPoolAsync(string endpoint,string model,IReadOnlyList<string> keys)
    {
        var modelsUrl=endpoint.Replace("/chat/completions","/models",StringComparison.OrdinalIgnoreCase);
        using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(30)};
        var failures=new System.Collections.Concurrent.ConcurrentBag<string>();
        await Parallel.ForEachAsync(keys.Select((key,index)=>(key,index)),new ParallelOptions{MaxDegreeOfParallelism=3},async(item,ct)=>
        {
            using var request=new HttpRequestMessage(HttpMethod.Get,modelsUrl);
            request.Headers.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",item.key);
            try
            {
                using var response=await http.SendAsync(request,ct);
                if(!response.IsSuccessStatusCode){failures.Add($"K{item.index+1:00}: HTTP {(int)response.StatusCode}");return;}
                var raw=await response.Content.ReadAsStringAsync(ct);
                using var doc=JsonDocument.Parse(raw);
                var found=doc.RootElement.GetProperty("data").EnumerateArray().Any(x=>(x.GetProperty("id").GetString()??"").EndsWith(model,StringComparison.OrdinalIgnoreCase));
                if(!found)failures.Add($"K{item.index+1:00}: model unavailable");
            }
            catch(Exception ex){failures.Add($"K{item.index+1:00}: {ex.GetType().Name}");}
        });
        if(!failures.IsEmpty)throw new InvalidOperationException("Google key-pool preflight failed: "+string.Join(", ",failures.Order()));
        Console.WriteLine($"[KEY POOL] Google preflight passed for {keys.Count}/{keys.Count} keys; model {model} is available.");
    }
}
