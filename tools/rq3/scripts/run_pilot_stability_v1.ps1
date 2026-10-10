param(
    [string]$WorkspaceRoot = 'R:\BaoVer2',
    [string]$OutputRoot = 'R:\BaoVer2\ThucNgiem\RQ3_Phase3\stability_runs_v1'
)

$ErrorActionPreference = 'Stop'
$dll = Join-Path $WorkspaceRoot 'ThucNgiem\RQ3_Phase3\build_verify_batch_v1_1_h\RQ3BatchRunner.dll'
$dataset = Join-Path $WorkspaceRoot 'ThucNgiem\RQ3_Phase2'
$manifest = Join-Path $dataset 'registry\pilot_frozen_manifest_v1.json'
$evidence = Join-Path $WorkspaceRoot 'ThucNgiem\RQ3_Phase3\pilot_v1\tool_evidence'
$keyFile = Join-Path $WorkspaceRoot 'OpenRouterKey.txt'
$cases = @(
    'RQ3-P-COMMONSDBUTILS-01',
    'RQ3-P-COMMONSJEXL-03',
    'RQ3-P-COMMONSTEXT-07',
    'RQ3-P-COMMONSVALIDATOR-09'
)

foreach ($repetition in 1..2) {
    $repetitionRoot = Join-Path $OutputRoot "rep$repetition"
    foreach ($caseId in $cases) {
        & dotnet $dll `
            "--dataset=$dataset" `
            "--out=$repetitionRoot" `
            "--manifest=$manifest" `
            "--evidence-root=$evidence" `
            '--endpoint=https://openrouter.ai/api/v1/chat/completions' `
            '--model=google/gemini-2.5-flash' `
            "--api-key-file=$keyFile" `
            "--case=$caseId" `
            '--b1' `
            '--b2'
        if ($LASTEXITCODE -ne 0) { throw "Stability run failed for repetition $repetition, case $caseId." }
    }
}
