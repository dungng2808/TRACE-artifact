using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace RQ3Evaluator.Wpf;

public sealed class LlmEvaluator
{
    private readonly RunOptions _o;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _semaphore;
    private readonly ApiKeyPool _keyPool;

    public LlmEvaluator(RunOptions options)
    {
        _o=options;
        _http=new HttpClient{Timeout=TimeSpan.FromSeconds(Math.Max(10,options.RequestTimeoutSec))};
        _semaphore=new SemaphoreSlim(Math.Max(1,options.MaxConcurrency),Math.Max(1,options.MaxConcurrency));
        _keyPool=new ApiKeyPool(options.ApiKeys,options.RateLimitRpm);
    }

    public IReadOnlyList<ApiKeyUsageSnapshot> GetKeyPoolSummary()=>_keyPool.Snapshot();

    public async Task<Prediction> B1(CaseInput c,CancellationToken ct)
    {
        var model=!string.IsNullOrWhiteSpace(_o.B1Model)?_o.B1Model:_o.Model;
        var endpoint=!string.IsNullOrWhiteSpace(_o.B1Endpoint)?_o.B1Endpoint:_o.Endpoint;
        const string prompt="You are the RQ3 general reviewer. Inspect the exact generated_test.source_text, declared focal mapping and scenario, compiler, selected-JUnit, per-test coverage, raw surviving-mutant, and bounded diagnostic evidence. Return exactly one JSON object {decision,cause,route,rationale}; no Markdown. Labels: VERIFIED|REPAIR_REQUIRED|ABSTAIN; causes COMPILATION_ERROR, SETUP_OR_RUNTIME_ERROR, PLAN_TEST_MISMATCH, TARGET_NOT_REACHED, ASSERTION_ERROR, WEAK_ORACLE, SHARED_INFRASTRUCTURE_ERROR, INSUFFICIENT_EVIDENCE, NONE; routes LOCAL_TEST_REPAIR, REGENERATE_TEST, SHARED_INFRASTRUCTURE_REPAIR, REQUEST_TOOL_EVIDENCE, NONE. Do not infer hidden gold and do not treat a path or hash as source code.";
        var result=await Review(c,"B1_GENERAL_REVIEWER",prompt,model,endpoint,ct);
        return result with{EvidenceEnvelopeHash=c.EvidenceEnvelopeHash};
    }

    public async Task<Prediction> B2(CaseInput c,CancellationToken ct)
    {
        var model=!string.IsNullOrWhiteSpace(_o.B2Model)?_o.B2Model:_o.Model;
        var endpoint=!string.IsNullOrWhiteSpace(_o.B2Endpoint)?_o.B2Endpoint:_o.Endpoint;
        var specialists=await Task.WhenAll(
            Review(c,"B2_CONFORMANCE",FindingPrompt("conformance","PLAN_TEST_MISMATCH|ASSERTION_ERROR","Compare exact test source with the declared focal mapping, inputs, and expected observation."),model,endpoint,ct,true,new[]{"PLAN_TEST_MISMATCH","ASSERTION_ERROR"}),
            Review(c,"B2_RUNTIME",FindingPrompt("runtime","COMPILATION_ERROR|SETUP_OR_RUNTIME_ERROR|SHARED_INFRASTRUCTURE_ERROR","Classify compiler, selected-test runtime, and shared-infrastructure evidence only."),model,endpoint,ct,true,new[]{"COMPILATION_ERROR","SETUP_OR_RUNTIME_ERROR","SHARED_INFRASTRUCTURE_ERROR"}),
            Review(c,"B2_COVERAGE_MUTATION",FindingPrompt("coverage/mutation","TARGET_NOT_REACHED|WEAK_ORACLE","Use per-test target coverage and raw surviving-mutant transformations; assess relevance without assuming hidden gold."),model,endpoint,ct,true,new[]{"TARGET_NOT_REACHED","WEAK_ORACLE"}));
        var roles=new[]{"CONFORMANCE","RUNTIME","COVERAGE_MUTATION"};
        var findings=specialists.Select((x,i)=>new SpecialistFinding(
            roles[i],x.Decision switch{"REPAIR_REQUIRED"=>"DETECTED","VERIFIED"=>"CLEAR",_=>"INSUFFICIENT"},
            x.Cause,x.Rationale,x.ExecutionStatus,x.InputTokens,x.OutputTokens,x.ApiCalls,x.CostUsd,x.LatencyMs,x.RequestHash,x.ResponseHash)).ToArray();
        return B2Aggregator.Aggregate(c.CaseId,c.EvidenceEnvelopeHash,findings);
    }

    private static string FindingPrompt(string role,string causes,string instruction)=>
        $"You are the RQ3 {role} specialist. {instruction} Return exactly one JSON object {{status,cause,rationale}}; no Markdown. status is DETECTED, CLEAR, or INSUFFICIENT. DETECTED cause must be one of {causes}. CLEAR uses NONE. INSUFFICIENT uses INSUFFICIENT_EVIDENCE. Never return a system decision or repair route.";

    private async Task<Prediction> Review(CaseInput c,string verifier,string system,string model,string endpoint,CancellationToken ct,bool findingMode=false,string[]? allowedFindingCauses=null)
    {
        var sw=Stopwatch.StartNew();
        try
        {
            var envelope=c.EvidenceEnvelope.GetRawText();
            var payload=new{model,messages=new[]{new{role="system",content=system},new{role="user",content=envelope}},response_format=new{type="json_object"},temperature=0,max_tokens=_o.MaxTokens>0?_o.MaxTokens:2000};
            var requestJson=JsonSerializer.Serialize(payload);
            var attempts=0;
            var maxAttempts=Math.Max(1,_keyPool.Count+Math.Max(0,_o.MaxRetries));
            while(attempts<maxAttempts)
            {
                var lease=await _keyPool.AcquireAsync(TimeSpan.FromSeconds(Math.Max(10,_o.RequestTimeoutSec)),ct);
                attempts++;
                HttpResponseMessage? response=null;
                string raw="";
                try
                {
                    await _semaphore.WaitAsync(ct);
                    try
                    {
                        using var request=new HttpRequestMessage(HttpMethod.Post,endpoint);
                        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",lease.Key);
                        request.Content=new StringContent(requestJson,Encoding.UTF8,"application/json");
                        response=await _http.SendAsync(request,ct);
                        raw=await response.Content.ReadAsStringAsync(ct);
                    }
                    finally{_semaphore.Release();}

                    var code=(int)response.StatusCode;
                    if(code==429)
                    {
                        _keyPool.ReportRateLimit(lease.Id,response.Headers.RetryAfter?.Delta,raw);
                        Console.Error.WriteLine($"[HTTP 429] {lease.Id} cooled down; rotating key for {c.CaseId} ({verifier}), attempt {attempts}/{maxAttempts}.");
                        continue;
                    }
                    if(code is 401 or 402 or 403)
                    {
                        _keyPool.Disable(lease.Id);
                        Console.Error.WriteLine($"[HTTP {code}] {lease.Id} disabled for this run; rotating key for {c.CaseId} ({verifier}).");
                        continue;
                    }
                    if(code is 408 or 425||code>=500)
                    {
                        _keyPool.ReportTransientFailure(lease.Id);
                        Console.Error.WriteLine($"[HTTP {code}] transient failure on {lease.Id}; rotating key for {c.CaseId} ({verifier}).");
                        continue;
                    }
                    if(code is 400 or 404 or 422)throw new HttpRequestException($"Non-rotatable HTTP {code} from model endpoint.",null,response.StatusCode);
                    response.EnsureSuccessStatusCode();

                using var api=JsonDocument.Parse(raw);
                var text=api.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()??throw new InvalidOperationException("Model returned empty content.");
                text=StripFence(text);
                var usage=api.RootElement.TryGetProperty("usage",out var u)?u:default;
                var input=usage.ValueKind==JsonValueKind.Object&&usage.TryGetProperty("prompt_tokens",out var i)?i.GetInt32():0;
                var output=usage.ValueKind==JsonValueKind.Object&&usage.TryGetProperty("completion_tokens",out var o)?o.GetInt32():0;
                var cost=usage.ValueKind==JsonValueKind.Object&&usage.TryGetProperty("cost",out var q)&&q.TryGetDouble(out var usd)?usd:0;
                _keyPool.ReportSuccess(lease.Id,input,output,cost);
                using var answer=JsonDocument.Parse(text);
                var root=answer.RootElement;
                string S(string name,string fallback)=>root.TryGetProperty(name,out var value)?value.GetString()??fallback:fallback;
                var status=findingMode?S("status","INSUFFICIENT").ToUpperInvariant():"";
                var decision=findingMode?status switch{"DETECTED"=>"REPAIR_REQUIRED","CLEAR"=>"VERIFIED",_=>"ABSTAIN"}:S("decision","ABSTAIN").ToUpperInvariant();
                var cause=S("cause","INSUFFICIENT_EVIDENCE").ToUpperInvariant();
                var route=findingMode?B2Aggregator.RouteFor(cause):S("route","REQUEST_TOOL_EVIDENCE").ToUpperInvariant();
                if(decision=="ABSTAIN"){cause="INSUFFICIENT_EVIDENCE";route="REQUEST_TOOL_EVIDENCE";}
                if(decision=="VERIFIED"){cause="NONE";route="NONE";}
                var findingValid=!findingMode||(new[]{"DETECTED","CLEAR","INSUFFICIENT"}.Contains(status)&&(status!="DETECTED"||(allowedFindingCauses?.Contains(cause)??false)));
                if(!Valid(decision,cause,route)||!findingValid)
                    return Invalid(c,verifier,sw,"Model returned out-of-schema labels; output discarded.","INVALID_SCHEMA",requestJson,raw,attempts);

                return new(c.CaseId,verifier,decision,cause,route,decision=="ABSTAIN",S("rationale","No rationale."),sw.Elapsed.TotalMilliseconds,input,output,"OK",Hash(requestJson),Hash(raw),attempts,cost){EvidenceEnvelopeHash=c.EvidenceEnvelopeHash};
                }
                catch(OperationCanceledException) when(!ct.IsCancellationRequested)
                {
                    _keyPool.ReportTransientFailure(lease.Id);
                    if(attempts>=maxAttempts)throw new TimeoutException($"Model request timed out after {attempts} key attempts.");
                }
                catch(HttpRequestException) when(response is null&&attempts<maxAttempts)
                {
                    _keyPool.ReportTransientFailure(lease.Id);
                }
                finally{response?.Dispose();}
            }
            throw new InvalidOperationException($"API key attempts exhausted after {attempts} attempts.");
        }
        catch(JsonException ex){return Invalid(c,verifier,sw,"Invalid model JSON discarded: "+ex.Message,"INVALID_OUTPUT");}
        catch(Exception ex){return Invalid(c,verifier,sw,"Model/API call failed: "+ex.Message,"API_OR_RUNTIME_ERROR");}
    }

    private static Prediction Invalid(CaseInput c,string verifier,Stopwatch sw,string rationale,string status,string request="",string response="",int calls=0)=>
        new(c.CaseId,verifier,"ABSTAIN","INSUFFICIENT_EVIDENCE","REQUEST_TOOL_EVIDENCE",true,rationale,sw.Elapsed.TotalMilliseconds,ExecutionStatus:status,RequestHash:string.IsNullOrEmpty(request)?null:Hash(request),ResponseHash:string.IsNullOrEmpty(response)?null:Hash(response),ApiCalls:calls){EvidenceEnvelopeHash=c.EvidenceEnvelopeHash};

    private static string StripFence(string value)
    {
        var text=value.Trim();
        if(text.StartsWith("```json",StringComparison.OrdinalIgnoreCase))text=text[7..];
        else if(text.StartsWith("```",StringComparison.OrdinalIgnoreCase))text=text[3..];
        if(text.EndsWith("```",StringComparison.OrdinalIgnoreCase))text=text[..^3];
        return text.Trim();
    }

    private static bool Valid(string decision,string cause,string route)=>
        new[]{"VERIFIED","REPAIR_REQUIRED","ABSTAIN"}.Contains(decision)&&
        new[]{"COMPILATION_ERROR","SETUP_OR_RUNTIME_ERROR","PLAN_TEST_MISMATCH","TARGET_NOT_REACHED","ASSERTION_ERROR","WEAK_ORACLE","SHARED_INFRASTRUCTURE_ERROR","INSUFFICIENT_EVIDENCE","NONE"}.Contains(cause)&&
        new[]{"LOCAL_TEST_REPAIR","REGENERATE_TEST","SHARED_INFRASTRUCTURE_REPAIR","REQUEST_TOOL_EVIDENCE","NONE"}.Contains(route);

    private static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
