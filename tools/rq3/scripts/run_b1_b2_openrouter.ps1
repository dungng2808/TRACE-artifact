param(
  [string]$Phase2Root='R:\BaoVer2\ThucNgiem\RQ3_Phase2',
  [string]$Phase3Root='R:\BaoVer2\ThucNgiem\RQ3_Phase3',
  [string]$ApiKey=$env:RQ3_OPENROUTER_KEY,
  [int]$Shard=0,
  [int]$ShardCount=1
)
$ErrorActionPreference='Stop'
if([string]::IsNullOrWhiteSpace($ApiKey)){throw 'Set RQ3_OPENROUTER_KEY.'}
$frozen=Get-Content -Raw (Join-Path $Phase2Root 'registry\phase2_frozen_manifest_v1.json')|ConvertFrom-Json
$out=Join-Path $Phase3Root 'artifacts\llm_predictions'; New-Item -ItemType Directory -Force $out|Out-Null
$included=@($frozen.decisions|Where-Object evaluation_status -eq 'INCLUDED')
$roles=@{
 B1='You are a general-purpose test-suite reviewer. Return JSON with decision (VERIFIED|REPAIR_REQUIRED|ABSTAIN), cause, route, rationale. Ground every claim in supplied public evidence only.'
 CONFORMANCE='You are the conformance specialist. Return JSON finding (PASS|FAIL|UNKNOWN), cause (PLAN_TEST_MISMATCH|ASSERTION_ERROR|INSUFFICIENT_EVIDENCE), rationale.'
 RUNTIME='You are the compilation/runtime specialist. Return JSON finding (PASS|FAIL|UNKNOWN), cause (COMPILATION_ERROR|SETUP_OR_RUNTIME_ERROR|INSUFFICIENT_EVIDENCE), rationale.'
 MUTATION='You are the coverage/mutation specialist. Return JSON finding (PASS|FAIL|UNKNOWN), cause (TARGET_NOT_REACHED|WEAK_ORACLE|INSUFFICIENT_EVIDENCE), rationale.'
}
function Invoke-Review($system,$envelope){
 $body=@{model='deepseek/deepseek-r1';messages=@(@{role='system';content=$system},@{role='user';content=$envelope});response_format=@{type='json_object'};temperature=0}|ConvertTo-Json -Depth 6
 $h=@{Authorization="Bearer $ApiKey";'Content-Type'='application/json'}
 $r=Invoke-RestMethod -Uri 'https://openrouter.ai/api/v1/chat/completions' -Method Post -Headers $h -Body $body -TimeoutSec 240
 return ($r.choices[0].message.content|ConvertFrom-Json)
}
foreach($d in $included){
 if((([array]::IndexOf($included,$d)) % $ShardCount) -ne $Shard){continue}
 $target=Join-Path $out ($d.case_id+'.json'); if(Test-Path $target){continue}
 $case=Get-Content -Raw (Join-Path $Phase2Root ('cases\public\'+$d.case_id+'.json'))|ConvertFrom-Json
 $testPath=Join-Path $Phase2Root $case.base_test.artifact_path
 $test=(Get-Content -Raw $testPath);if($test.Length -gt 12000){$test=$test.Substring(0,12000)}
 $env=[ordered]@{case=$case;test_source=$test;tool_evidence=@{compile='No new compilation failure observed in retained baseline artifact';junit='Retained baseline test passed three validation runs where recorded';coverage_mutation='Only per-test mutation evidence is supplied for admitted weak-oracle cases; otherwise unavailable.'}}|ConvertTo-Json -Depth 8
 try{
   $b1=Invoke-Review $roles.B1 $env
   $c=Invoke-Review $roles.CONFORMANCE $env; $r=Invoke-Review $roles.RUNTIME $env; $m=Invoke-Review $roles.MUTATION $env
   $fail=@($c,$r,$m)|Where-Object {$_.finding -eq 'FAIL'}|Select-Object -First 1
   $unknown=@($c,$r,$m)|Where-Object {$_.finding -eq 'UNKNOWN'}
   $b2=if($fail){[ordered]@{decision='REPAIR_REQUIRED';cause=$fail.cause;route='LOCAL_TEST_REPAIR';rationale=$fail.rationale}}elseif($unknown.Count -gt 0){[ordered]@{decision='ABSTAIN';cause='INSUFFICIENT_EVIDENCE';route='REQUEST_TOOL_EVIDENCE';rationale='At least one specialized agent lacks required evidence.'}}else{[ordered]@{decision='VERIFIED';cause='NONE';route='NONE';rationale='All specialized agents passed.'}}
   [ordered]@{case_id=$d.case_id;B1=$b1;B2_agents=@{conformance=$c;runtime=$r;mutation=$m};B2=$b2}|ConvertTo-Json -Depth 8|Set-Content $target -Encoding utf8
   Write-Output "completed $($d.case_id)"
 }catch{[ordered]@{case_id=$d.case_id;error=$_.Exception.Message}|ConvertTo-Json|Set-Content ($target+'.error.json') -Encoding utf8;Write-Error "failed $($d.case_id): $($_.Exception.Message)"}
}
