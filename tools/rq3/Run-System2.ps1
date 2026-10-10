param(
    [Parameter(Mandatory = $true)][string]$Model,
    [Parameter(Mandatory = $true)][string]$Endpoint,
    [Parameter(Mandatory = $true)][string]$ApiKeyFile,
    [string]$OutputRoot = '',
    [string]$CaseId = '',
    [int]$MaxTokens = 4000,
    [int]$MaxConcurrency = 3,
    [int]$CaseConcurrency = 1,
    [int]$RateLimitRpm = 0,
    [int]$MaxRetries = 3,
    [int]$TimeoutSec = 120,
    [int]$BootstrapIterations = 5000
)

$ErrorActionPreference = 'Stop'
$systemRoot = $PSScriptRoot
$packageRoot = Split-Path -Parent $systemRoot
$datasetRoot = Join-Path $packageRoot 'datasets\rq3\phase6_exploratory_v2_dataset'
$manifestPath = Join-Path $datasetRoot 'registry\phase6_exploratory_v2_manifest.json'
$runnerProject = Join-Path $systemRoot 'RQ3BatchRunner\RQ3BatchRunner.csproj'
$runnerDll = Join-Path $systemRoot 'RQ3BatchRunner\bin\Release\net8.0\RQ3BatchRunner.dll'
$readinessScript = Join-Path $systemRoot 'scripts\test_exploratory_v2_readiness.ps1'
$freezeScript = Join-Path $systemRoot 'scripts\merge_and_freeze_exploratory_v2.ps1'
$scoreScript = Join-Path $systemRoot 'scripts\score_exploratory_v2.ps1'

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $packageRoot 'experiments\rq3\runs'
}

foreach ($requiredPath in @($datasetRoot, $manifestPath, $runnerProject, $readinessScript, $freezeScript, $scoreScript, $ApiKeyFile)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Missing required path: $requiredPath"
    }
}

Write-Host '[1/5] Check dataset readiness'
& $readinessScript -DatasetRoot $datasetRoot
if (-not $?) { throw 'Dataset readiness check failed.' }

Write-Host '[2/5] Build RQ3BatchRunner'
dotnet build $runnerProject -c Release
if ($LASTEXITCODE -ne 0) { throw 'RQ3BatchRunner build failed.' }

New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$runDir = Join-Path $OutputRoot ('RQ3_Batch_' + [DateTime]::UtcNow.ToString('yyyyMMdd_HHmmss'))
$runnerArgs = @(
    $runnerDll,
    "--dataset=$datasetRoot",
    "--manifest=$manifestPath",
    "--evidence-root=$(Join-Path $datasetRoot 'tool_evidence')",
    "--out=$OutputRoot",
    "--run-dir=$runDir",
    "--endpoint=$Endpoint",
    "--model=$Model",
    "--api-key-file=$ApiKeyFile",
    '--b0', '--b1', '--b2',
    "--max-tokens=$MaxTokens",
    "--max-concurrency=$MaxConcurrency",
    "--case-concurrency=$CaseConcurrency",
    "--rate-limit-rpm=$RateLimitRpm",
    "--max-retries=$MaxRetries",
    "--timeout-sec=$TimeoutSec"
)
if (-not [string]::IsNullOrWhiteSpace($CaseId)) {
    $runnerArgs += "--case=$CaseId"
}

Write-Host '[3/5] Run B0, B1, and B2'
& dotnet @runnerArgs
$runnerExit = $LASTEXITCODE
if ($runnerExit -ne 0) {
    Write-Warning "Runner exited with code $runnerExit. Checkpoint remains at $runDir for audit/resume."
    exit $runnerExit
}

if (-not [string]::IsNullOrWhiteSpace($CaseId)) {
    Write-Host "Smoke test completed at $runDir. Freeze/scoring skipped for a single case."
    exit 0
}

$predictionsPath = Join-Path $runDir 'predictions.json'
$freezeRoot = Join-Path $runDir 'frozen'

Write-Host '[4/5] Freeze predictions before opening private gold'
& $freezeScript -MainPredictions $predictionsPath -DatasetRoot $datasetRoot -OutputDirectory $freezeRoot
$freeze = Get-Content -LiteralPath (Join-Path $freezeRoot 'prediction_freeze_v2.json') -Raw | ConvertFrom-Json
if (-not $freeze.valid_for_complete_case_analysis) {
    throw 'Freeze is not complete-case. Scoring was not performed.'
}

Write-Host '[5/5] Join private gold and compute metrics'
$resultsRoot = Join-Path $runDir 'results'
& $scoreScript -PredictionsJson (Join-Path $freezeRoot 'predictions_merged_v2.json') -DatasetRoot $datasetRoot -OutputRoot $resultsRoot -BootstrapIterations $BootstrapIterations
if (-not $?) { throw 'Scoring failed.' }

Write-Host "Full experiment completed: $runDir"
