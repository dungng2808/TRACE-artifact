param(
    [string]$WorkspaceRoot = 'R:\BaoVer2',
    [string]$OutputRoot = 'R:\BaoVer2\ThucNgiem\RQ3_Phase3\pilot_runs_v1'
)

$ErrorActionPreference = 'Stop'
$dll = Join-Path $WorkspaceRoot 'ThucNgiem\RQ3_Phase3\build_verify_batch_v1_1_h\RQ3BatchRunner.dll'
$dataset = Join-Path $WorkspaceRoot 'ThucNgiem\RQ3_Phase2'
$manifest = Join-Path $dataset 'registry\pilot_frozen_manifest_v1.json'
$evidence = Join-Path $WorkspaceRoot 'ThucNgiem\RQ3_Phase3\pilot_v1\tool_evidence'
$keyFile = Join-Path $WorkspaceRoot 'OpenRouterKey.txt'

foreach ($required in @($dll, $manifest, $keyFile)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Missing required file: $required" }
}

& dotnet $dll `
    "--dataset=$dataset" `
    "--out=$OutputRoot" `
    "--manifest=$manifest" `
    "--evidence-root=$evidence" `
    '--endpoint=https://openrouter.ai/api/v1/chat/completions' `
    '--model=google/gemini-2.5-flash' `
    "--api-key-file=$keyFile" `
    '--b1' `
    '--b2'

if ($LASTEXITCODE -ne 0) { throw "RQ3BatchRunner failed with exit code $LASTEXITCODE." }
