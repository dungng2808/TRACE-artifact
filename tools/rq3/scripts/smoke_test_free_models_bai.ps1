<#
.SYNOPSIS
  Smoke test cho 5 model free mới trên B.AI trước khi chạy full experiment.
  Chạy 1 case (RQ3-M-MATH-04) với từng model và xác nhận JSON schema hợp lệ.
#>

$ErrorActionPreference = "Stop"

$RUNNER_DLL   = "R:\BaoVer2\ThucNgiem\RQ3_Phase3_Consensus\tools\RQ3BatchRunner\bin\Release\net8.0\RQ3BatchRunner.dll"
$DATASET      = "R:\BaoVer2\ThucNgiem\RQ3_Phase3_Consensus\phase6_exploratory_v2_dataset"
$MANIFEST     = "$DATASET\registry\phase6_exploratory_v2_manifest.json"
$EVIDENCE     = "$DATASET\tool_evidence"
$KEY_FILE     = "R:\BaoVer2\ApiKey_bai_clean.txt"
$ENDPOINT     = "https://api.b.ai/v1/chat/completions"
$SMOKE_DIR    = "R:\BaoVer2\ThucNgiem\RQ3_Phase3_Consensus\test_output\smoke_free_models_bai"
$SMOKE_CASE   = "RQ3-M-MATH-04"

$MODELS = @(
    "deepseek-v4-flash-vision-exp",
    "glm-5.3-flash",
    "qwen3.8-flash",
    "hy3",
    "mimo-v2.5"
)

Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  SMOKE TEST - 5 Free Models B.AI - He Thong 2 (Consensus)" -ForegroundColor Cyan
Write-Host "  Case test: $SMOKE_CASE"  -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""

$results = @()

foreach ($model in $MODELS) {
    $slug    = $model -replace "[^a-zA-Z0-9]", "_"
    $runDir  = "$SMOKE_DIR\$slug"
    New-Item -ItemType Directory -Force -Path $runDir | Out-Null

    Write-Host ">>> Testing model: $model" -ForegroundColor Yellow

    $exitCode = 0
    try {
        $dotnetArgs = @(
            $RUNNER_DLL,
            "--dataset=$DATASET",
            "--out=$SMOKE_DIR",
            "--run-dir=$runDir",
            "--manifest=$MANIFEST",
            "--evidence-root=$EVIDENCE",
            "--endpoint=$ENDPOINT",
            "--b1-model=$model",
            "--b2-model=$model",
            "--api-key-file=$KEY_FILE",
            "--max-tokens=4000",
            "--max-concurrency=3",
            "--rate-limit-rpm=10",
            "--max-retries=2",
            "--timeout-sec=120",
            "--b1", "--b2", "--b0",
            "--case=$SMOKE_CASE"
        )
        & dotnet @dotnetArgs
        $exitCode = $LASTEXITCODE
    } catch {
        $exitCode = 1
        Write-Host "  [ERROR] Exception: $($_.Exception.Message)" -ForegroundColor Red
    }

    # Check predictions.json
    $predFile = "$runDir\predictions.json"
    $status = "UNKNOWN"
    $details = ""

    if (Test-Path $predFile) {
        try {
            $preds = Get-Content $predFile | ConvertFrom-Json
            $b2pred = $preds | Where-Object { $_.verifier -eq "B2_SPECIALIZED_MULTI_AGENT" }
            $b1pred = $preds | Where-Object { $_.verifier -eq "B1_GENERAL_REVIEWER" }
            $validDecisions = @("VERIFIED", "REPAIR_REQUIRED", "ABSTAIN")

            if ($b2pred -and $validDecisions -contains $b2pred.decision) {
                $status = "PASS"
                $details = "B1=$($b1pred.decision) | B2=$($b2pred.decision) | ExecStatus=$($b2pred.execution_status)"
            } else {
                $status = "FAIL_SCHEMA"
                $details = "B2 prediction missing or invalid decision"
            }
        } catch {
            $status = "FAIL_PARSE"
            $details = $_.Exception.Message
        }
    } else {
        $status = "FAIL_NO_FILE"
        $details = "predictions.json not created (exit=$exitCode)"
    }

    $color = if ($status -eq "PASS") { "Green" } else { "Red" }
    Write-Host "  Status : $status" -ForegroundColor $color
    Write-Host "  Details: $details" -ForegroundColor Gray
    Write-Host ""

    $results += [PSCustomObject]@{
        Model   = $model
        Status  = $status
        Details = $details
    }
}

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  SMOKE TEST SUMMARY" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
$results | Format-Table -AutoSize

$failCount = ($results | Where-Object { $_.Status -ne "PASS" }).Count
if ($failCount -eq 0) {
    Write-Host "ALL MODELS PASSED smoke test. Ready for full run!" -ForegroundColor Green
    exit 0
} else {
    Write-Host "$failCount model(s) FAILED smoke test. Check logs above." -ForegroundColor Red
    exit 1
}
