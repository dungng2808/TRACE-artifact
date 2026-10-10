namespace RQ3Evaluator.Wpf;

public static class DatasetService
{
    public static IReadOnlyList<CaseInput> Load(string root,string? manifestPath=null,string? evidenceRoot=null)
    {
        var manifest=manifestPath??Path.Combine(root,"registry","phase2_frozen_manifest_v1.json");
        if(!File.Exists(manifest)) throw new InvalidOperationException("Expected registry/phase2_frozen_manifest_v1.json under the selected dataset folder.");
        using var doc=JsonDocument.Parse(File.ReadAllText(manifest));
        var list=new List<CaseInput>();
        foreach(var d in doc.RootElement.GetProperty("decisions").EnumerateArray())
        {
            if(d.GetProperty("evaluation_status").GetString()!="INCLUDED") continue;
            var id=d.GetProperty("case_id").GetString()!;
            var pub=Path.Combine(root,"cases","public",id+".json");
            var gold=Path.Combine(root,"gold","private",id+".json");
            using var c=JsonDocument.Parse(File.ReadAllText(pub));
            var toolRoot=evidenceRoot??Path.Combine(root,"tool_evidence");var fullEvidence=Path.Combine(toolRoot,id+".bundle.json");
            var evidence=File.Exists(fullEvidence)?fullEvidence:Path.Combine(toolRoot,id+".json");
            JsonElement? tool=null; if(File.Exists(evidence)){using var e=JsonDocument.Parse(File.ReadAllText(evidence));tool=e.RootElement.Clone();}
            var envelopePath=Path.Combine(root,"envelopes",id+".json");
            JsonElement envelope;
            if(File.Exists(envelopePath))
            {
                using var e=JsonDocument.Parse(File.ReadAllText(envelopePath));
                envelope=e.RootElement.Clone();
            }
            else
            {
                using var e=JsonDocument.Parse(JsonSerializer.Serialize(new { public_case=c.RootElement, tool_evidence=tool, note="Legacy envelope: generated source is not embedded." }));
                envelope=e.RootElement.Clone();
            }
            var envelopeHash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(envelope.GetRawText())));
            list.Add(new CaseInput(id,pub,File.Exists(gold)?gold:null,c.RootElement.Clone(),tool,envelope,envelopeHash));
        }
        if(list.Count==0) throw new InvalidOperationException("No INCLUDED cases were found in the frozen manifest.");
        return list;
    }
    public static Gold ReadGold(CaseInput input)
    {
        if(input.GoldPath is null) throw new InvalidOperationException("Private gold is missing for "+input.CaseId);
        using var d=JsonDocument.Parse(File.ReadAllText(input.GoldPath));
        var root=d.RootElement;
        var cat=root.TryGetProperty("goldCategory",out var gc)?gc.GetString()!:root.GetProperty("intended_gold_category").GetString()!;
        if(root.TryGetProperty("goldDecision",out var decision))
        {
            var routes=root.GetProperty("acceptableRoutes").EnumerateArray().Select(x=>x.GetString()!).ToArray();
            return new(cat,decision.GetString()!,root.GetProperty("primaryCause").GetString()!,routes,root.GetProperty("falsePassing").GetBoolean());
        }
        return cat switch {
            "NONE_VALID" => new(cat,"VERIFIED","NONE",new[]{"NONE"},false),
            "LOCAL_COMPILATION_FAILURE" => new(cat,"REPAIR_REQUIRED","COMPILATION_ERROR",new[]{"LOCAL_TEST_REPAIR"},false),
            "LOCAL_RUNTIME_OR_JUNIT_FAILURE" => new(cat,"REPAIR_REQUIRED","SETUP_OR_RUNTIME_ERROR",new[]{"LOCAL_TEST_REPAIR"},false),
            "PLAN_TEST_MISMATCH" => new(cat,"REPAIR_REQUIRED","PLAN_TEST_MISMATCH",new[]{"REGENERATE_TEST"},true),
            "STRUCTURAL_TARGET_NOT_EXECUTED" => new(cat,"REPAIR_REQUIRED","TARGET_NOT_REACHED",new[]{"REGENERATE_TEST"},true),
            "EXPECTED_OBSERVATION_OR_ASSERTION_MISMATCH" => new(cat,"REPAIR_REQUIRED","ASSERTION_ERROR",new[]{"LOCAL_TEST_REPAIR"},true),
            "WEAK_ORACLE_RELEVANT_MUTANT_SURVIVES" => new(cat,"REPAIR_REQUIRED","WEAK_ORACLE",new[]{"LOCAL_TEST_REPAIR"},true),
            "SHARED_INFRASTRUCTURE_FAILURE" => new(cat,"REPAIR_REQUIRED","SHARED_INFRASTRUCTURE_ERROR",new[]{"SHARED_INFRASTRUCTURE_REPAIR"},false),
            _ => new(cat,"ABSTAIN","INSUFFICIENT_EVIDENCE",new[]{"REQUEST_TOOL_EVIDENCE"},false)};
    }
}
