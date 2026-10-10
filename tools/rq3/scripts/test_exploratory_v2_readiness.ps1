param(
    [string]$DatasetRoot='R:\BaoVer2\ThucNgiem\RQ3_Phase3_Consensus\phase6_exploratory_v2_dataset',
    [switch]$WriteReport
)
$ErrorActionPreference='Stop'
$manifestPath=Join-Path $DatasetRoot 'registry\phase6_exploratory_v2_manifest.json'
$manifest=Get-Content -Raw $manifestPath|ConvertFrom-Json
$ids=@($manifest.decisions|Where-Object evaluation_status -eq 'INCLUDED'|Select-Object -ExpandProperty case_id)
$provenance=@{};$provenanceItems=Get-Content -Raw (Join-Path $DatasetRoot 'provenance\source_provenance_private.json')|ConvertFrom-Json;for($pi=0;$pi-lt$provenanceItems.Count;$pi++){$p=$provenanceItems[$pi];$provenance[[string]$p.case_id]=$p}
$fail=[Collections.Generic.List[string]]::new();$warnings=[Collections.Generic.List[string]]::new();$weak=0
if($ids.Count-ne73){$fail.Add("Expected 73 cases, found $($ids.Count)")}
foreach($id in $ids){
    $casePath=Join-Path $DatasetRoot "cases\public\$id.json";$envelopePath=Join-Path $DatasetRoot "envelopes\$id.json";$goldPath=Join-Path $DatasetRoot "gold\private\$id.json";$toolPath=Join-Path $DatasetRoot "tool_evidence\$id.bundle.json"
    foreach($p in @($casePath,$envelopePath,$goldPath,$toolPath)){if(-not(Test-Path $p)){$fail.Add("$id missing $p")}}
    if($fail|Where-Object{$_-like"$id missing*"}){continue}
    $case=Get-Content -Raw $casePath|ConvertFrom-Json;$envRaw=Get-Content -Raw $envelopePath;$env=$envRaw|ConvertFrom-Json;$gold=Get-Content -Raw $goldPath|ConvertFrom-Json
    if([string]::IsNullOrWhiteSpace([string]$case.focal_mapping.candidate_method)){$fail.Add("$id has no candidate_method")}
    if(($case|ConvertTo-Json -Depth 20)-match '(?i)\bPending\b'){$fail.Add("$id still contains Pending")}
    foreach($field in @('summary','input_preconditions','expected_observation','structural_target')){if([string]::IsNullOrWhiteSpace([string]$case.scenario.$field)){$fail.Add("$id scenario.$field is empty")}}
    if([string]::IsNullOrWhiteSpace([string]$env.generated_test.source_text)){$fail.Add("$id has no source text")}
    if([string]$env.generated_test.source_sha256-notmatch'^[0-9A-F]{64}$'){$fail.Add("$id has invalid source hash")}
    $prov=$provenance[$id];if($null-eq$prov){$fail.Add("$id has no private source provenance")}elseif(-not(Test-Path -LiteralPath $prov.source_path)){$fail.Add("$id provenance source is missing")}else{$fileHash=(Get-FileHash -Algorithm SHA256 -LiteralPath $prov.source_path).Hash;$fileText=[IO.File]::ReadAllText($prov.source_path);if($env.generated_test.source_scope-eq'SELECTED_TEST_METHOD_WITH_IMPORT_CONTEXT'){if($env.generated_test.source_file_sha256-ne$fileHash){$fail.Add("$id compact source file hash mismatch")};$methodName=([string]$env.generated_test.test_id-split'::')[-1];if($env.generated_test.source_text-notmatch[regex]::Escape($methodName)){ $fail.Add("$id compact source omits selected method") }}else{if($fileHash-ne$env.generated_test.source_sha256){$fail.Add("$id source hash differs from materialized file")};if($fileText-ne$env.generated_test.source_text){$fail.Add("$id source text differs from materialized file")}}}
    if($env.generated_test.test_id-ne$case.base_test.test_id){$fail.Add("$id test identity mismatch")}
    if($envRaw-match'(?i)evidence_layer_operator|intended_gold_category|goldDecision|WEAKEN_ORACLE|Controlled declaration intentionally conflicts'){$fail.Add("$id leaks gold/injection metadata")}
    if($gold.goldCategory-eq'WEAK_ORACLE_RELEVANT_MUTANT_SURVIVES'){
        $weak++;if(@($env.tool_evidence.mutation.surviving_mutants).Count-eq0){$fail.Add("$id has no raw surviving mutants")};if($env.tool_evidence.mutation.relevance_disclosed-ne$false){$fail.Add("$id discloses mutation relevance")};if($env.tool_evidence.mutation.selected_test_id-ne$case.base_test.test_id){$fail.Add("$id mutation selected-test mismatch")};if([string]$env.tool_evidence.mutation.raw_report_sha256-notmatch'^[0-9A-F]{64}$'){$fail.Add("$id raw mutation report hash missing")};if(-not[bool]$gold.falsePassing){$fail.Add("$id weak gold is not falsePassing")};if($gold.mutationAdjudication.verdict-ne'RELEVANT_SURVIVOR_PRESENT'-or$gold.mutationAdjudication.disclosedToVerifier-ne$false){$fail.Add("$id private relevance adjudication invalid")};if($prov.source_path-notmatch'WEAKEN_ORACLE-variant\.java$'){$fail.Add("$id did not materialize weakened source")}
    }
    $canonical=ConvertTo-Json $env -Depth 40 -Compress;$sha=[Security.Cryptography.SHA256]::Create();try{$hash=[BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($canonical))).Replace('-','')}finally{$sha.Dispose()}
    if([string]::IsNullOrWhiteSpace($hash)){$fail.Add("$id envelope hashing failed")}
}
if($weak-ne4){$fail.Add("Expected 4 weak-oracle cases, found $weak")}
$report=[ordered]@{schema_version='rq3-exploratory-v2-readiness';checked_at=[DateTimeOffset]::UtcNow.ToString('o');cases=$ids.Count;weak_oracle_cases=$weak;passed=($fail.Count-eq0);failures=@($fail);warnings=@($warnings)}
if($WriteReport){$report|ConvertTo-Json -Depth 8|Set-Content (Join-Path $DatasetRoot 'readiness_report.json') -Encoding utf8}
$report|ConvertTo-Json -Depth 8
if($fail.Count-ne0){exit 1}
