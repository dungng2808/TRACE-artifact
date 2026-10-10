param(
  [int]$Shard=0,
  [int]$ShardCount=1,
  [string]$Phase2Root='R:\BaoVer2\ThucNgiem\RQ3_Phase2',
  [string]$Phase3Root='R:\BaoVer2\ThucNgiem\RQ3_Phase3'
)
$ErrorActionPreference='Stop'
if([string]::IsNullOrWhiteSpace($env:RQ3_OPENROUTER_KEY)){throw 'Set RQ3_OPENROUTER_KEY.'}
$tool=Join-Path $Phase3Root 'tools\RQ3Phase3Reviewer'
$out=Join-Path $Phase3Root 'artifacts\llm_predictions_gemini_flash_v1';New-Item -ItemType Directory -Force $out|Out-Null
$frozen=Get-Content -Raw (Join-Path $Phase2Root 'registry\phase2_frozen_manifest_v1.json')|ConvertFrom-Json
$cases=@($frozen.decisions|Where-Object evaluation_status -eq 'INCLUDED')
for($i=$Shard;$i -lt $cases.Count;$i+=$ShardCount){
  $id=$cases[$i].case_id;$target=Join-Path $out ($id+'.json');if(Test-Path $target){continue}
  try{
    & dotnet run --project $tool --no-build -- --case (Join-Path $Phase2Root ('cases\public\'+$id+'.json')) --phase2 $Phase2Root --out $target
    if($LASTEXITCODE -ne 0){throw "reviewer exit $LASTEXITCODE"}
    Write-Output "completed $id"
  }catch{[ordered]@{case_id=$id;error=$_.Exception.Message}|ConvertTo-Json|Set-Content ($target+'.error.json') -Encoding utf8;Write-Warning "failed $id; continuing shard"}
}
