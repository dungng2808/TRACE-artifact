<#
.SYNOPSIS
  Chạy thực nghiệm cho các model được chọn (GLM-5.3-Flash và Qwen3.8-Flash) trên Hệ Thống 2 (Multi-Agent Consensus).
  Tự động resume glm-5.3-flash từ checkpoint đã có (61/73 cases), sau đó chạy tiếp qwen3.8-flash.
#>

$ErrorActionPreference = "Stop"

$RUNNER_DLL   = "R:\BaoVer2\ThucNgiem\RQ3_Phase3_Consensus\tools\RQ3BatchRunner\bin\Release\net8.0\RQ3BatchRunner.dll"
$DATASET      = "R:\BaoVer2\ThucNgiem\RQ3_Phase3_Consensus\phase6_exploratory_v2_dataset"
$MANIFEST     = "$DATASET\registry\phase6_exploratory_v2_manifest.json"
$EVIDENCE     = "$DATASET\tool_evidence"
$KEY_FILE     = "R:\BaoVer2\ApiKey_bai_clean.txt"
$ENDPOINT     = "https://api.b.ai/v1/chat/completions"
$KQ_ROOT      = "R:\BaoVer2\ThucNgiem\KQ_Thucnghiem"
$SCORER       = "R:\BaoVer2\ThucNgiem\RQ3_Phase3_Consensus\scripts\score_exploratory_v2.ps1"

# Danh sách model chuẩn cần chạy (Chỉ chạy glm-5.3-flash theo yêu cầu của user)
$MODELS = @(
    "glm-5.3-flash"
)

Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  RUN EXPERIMENT: GLM-5.3-Flash & Qwen3.8-Flash (He Thong 2)" -ForegroundColor Cyan
Write-Host "  Dataset: 73 cases | Endpoint: api.b.ai" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""

$globalResults = @()
$totalStart = Get-Date

foreach ($model in $MODELS) {
    $slug       = $model -replace "[^a-zA-Z0-9]", "_"
    $outName    = "KQ_Thucnghiem_VerHeThong2_$slug"
    $outDir     = "$KQ_ROOT\$outName"
    $predDir    = "$outDir\predictions"
    $resultsDir = "$outDir\results"
    $stabilityDir = "$outDir\stability"

    foreach ($d in @($outDir, $predDir, $resultsDir, $stabilityDir)) {
        New-Item -ItemType Directory -Force -Path $d | Out-Null
    }

    Write-Host "================================================================" -ForegroundColor Magenta
    Write-Host "  MODEL: $model" -ForegroundColor Magenta
    Write-Host "  Output: $outDir" -ForegroundColor Gray
    Write-Host "  Start: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -ForegroundColor Gray
    Write-Host "================================================================" -ForegroundColor Magenta

    $modelStart = Get-Date
    $exitCode = 0
    $runDir = $null

    # Kiểm tra xem có batch cũ để resume không
    $existingBatch = Get-ChildItem $predDir -Directory -Filter "RQ3_Batch_*" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $isResume = $false
    if ($existingBatch -and (Test-Path "$($existingBatch.FullName)\checkpoint_predictions.json")) {
        $runDir = $existingBatch.FullName
        $isResume = $true
        Write-Host "  [RESUME] Phát hiện checkpoint cũ tại $runDir. Tiếp tục chạy các ca còn lại..." -ForegroundColor Yellow
    }

    try {
        $dotnetArgs = @(
            $RUNNER_DLL,
            "--dataset=$DATASET",
            "--out=$predDir",
            "--manifest=$MANIFEST",
            "--evidence-root=$EVIDENCE",
            "--endpoint=$ENDPOINT",
            "--b1-model=$model",
            "--b2-model=$model",
            "--api-key-file=$KEY_FILE",
            "--max-tokens=4000",
            "--max-concurrency=3",
            "--rate-limit-rpm=10",
            "--max-retries=3",
            "--timeout-sec=120",
            "--b1", "--b2", "--b0"
        )

        if ($isResume -and $runDir) {
            $dotnetArgs += "--run-dir=$runDir"
            $dotnetArgs += "--resume"
        }

        $runnerOutput = & dotnet @dotnetArgs
        $exitCode = $LASTEXITCODE
        $lastLine = $runnerOutput | Where-Object { $_ -match '^R:\\' -or $_ -match 'RQ3_Batch_' } | Select-Object -Last 1
        if ($lastLine) { $runDir = $lastLine.Trim() }
        Write-Host "  Runner exit code: $exitCode | Run dir: $runDir" -ForegroundColor Gray
    } catch {
        $exitCode = 99
        Write-Host "  [EXCEPTION] $($_.Exception.Message)" -ForegroundColor Red
    }

    $duration = [math]::Round(((Get-Date) - $modelStart).TotalMinutes, 1)

    # Tìm file predictions.json trong runDir hoặc predDir
    $foundPredJson = $null
    if ($runDir -and (Test-Path "$runDir\predictions.json")) {
        $foundPredJson = "$runDir\predictions.json"
    } else {
        $search = Get-ChildItem $predDir -Recurse -Filter "predictions.json" -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($search) { $foundPredJson = $search.FullName; $runDir = $search.DirectoryName }
    }

    $status = "UNKNOWN"
    $b2Acc = "N/A"; $abstRate = "N/A"; $coverage = "N/A"; $macroF1 = "N/A"

    if ($foundPredJson -and (Test-Path $foundPredJson)) {
        # Copy các file kết quả vào predictions/
        Copy-Item $foundPredJson -Destination "$predDir\predictions.json" -Force
        if (Test-Path "$runDir\run_manifest.json") {
            Copy-Item "$runDir\run_manifest.json" -Destination "$predDir\run_manifest.json" -Force
        }
        if (Test-Path "$runDir\rq3_summary.xlsx") {
            Copy-Item "$runDir\rq3_summary.xlsx" -Destination "$predDir\rq3_summary.xlsx" -Force
        }
        if (Test-Path "$runDir\rq3_detailed_predictions.xlsx") {
            Copy-Item "$runDir\rq3_detailed_predictions.xlsx" -Destination "$predDir\rq3_detailed_predictions.xlsx" -Force
        }

        # Chạy scorer
        $finalPredJson = "$predDir\predictions.json"
        Write-Host "  Running scorer on: $finalPredJson" -ForegroundColor Yellow
        try {
            & powershell -ExecutionPolicy Bypass -File $SCORER `
                -PredictionsJson $finalPredJson `
                -OutputRoot $resultsDir 2>&1 | Tee-Object -Variable scorerOutput
            
            $metricsFile = Get-ChildItem $resultsDir -Filter "*.json" -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($metricsFile) {
                $metrics = Get-Content $metricsFile.FullName | ConvertFrom-Json
                $b2Metric = $metrics.metrics | Where-Object { $_.verifier -eq "B2_SPECIALIZED_MULTI_AGENT" }
                if ($b2Metric) {
                    $b2Acc    = "$([math]::Round($b2Metric.decision_accuracy * 100, 2))%"
                    $abstRate = "$([math]::Round($b2Metric.abstention_rate * 100, 2))%"
                    $coverage = "$([math]::Round($b2Metric.decision_coverage * 100, 2))%"
                    $macroF1  = "$([math]::Round($b2Metric.macro_attribution_f1, 4))"
                }
                $status = "COMPLETE"
            } else {
                $status = "SCORED_NO_METRICS"
            }
        } catch {
            $status = "SCORE_ERROR"
            Write-Host "  [SCORE ERROR] $($_.Exception.Message)" -ForegroundColor Red
        }

        # Tạo file CAU_HINH_MODEL.md chi tiết
        $mdContent = @"
# CẤU HÌNH MÔ HÌNH VÀ BÁO CÁO THỰC NGHIỆM HỆ THỐNG BẢN 2 (MULTI-AGENT CONSENSUS)
**Model sử dụng**: `$model`  
**Kiến trúc hệ thống**: Hệ thống Bản 2 (RQ3 Phase 3 Consensus - 3-Tier Multi-Agent Consensus)

---

## 1. Thông tin đợt chạy (Run Metadata)
* **Tên thư mục lưu trữ**: `$outName`
* **Thời gian thực hiện**: `$(Get-Date -Format 'dd/MM/yyyy HH:mm:ss')`
* **Nền tảng thực thi**: `RQ3_Phase3_Consensus` (WPF Control Center Harness .NET 8)
* **Quy mô tập dữ liệu**: **73 cases** (Phase 6 Main Amended Cohort)
* **API Endpoint**: `$ENDPOINT`

---

## 2. Bảng tổng hợp kết quả thực nghiệm (B2 Multi-Agent Consensus)
* **Decision Accuracy**: **$b2Acc**
* **Abstention Rate**: **$abstRate**
* **Decision Coverage**: **$coverage**
* **Macro Attribution F1**: **$macroF1**
"@
        $mdContent | Set-Content "$outDir\CAU_HINH_MODEL.md" -Encoding UTF8
    } else {
        $status = if ($exitCode -ne 0) { "RUN_FAILED" } else { "NO_PREDICTIONS" }
    }

    # Tạo CAU_HINH_MODEL.json
    $configJson = @{
        model        = $model
        system       = "RQ3_Phase3_Consensus (He Thong 2 - Multi-Agent Consensus)"
        endpoint     = $ENDPOINT
        key_file     = $KEY_FILE
        max_tokens   = 4000
        max_concurrency = 3
        rate_limit_rpm = 10
        dataset_cases = 73
        run_dir      = $runDir
        exit_code    = $exitCode
        status       = $status
        duration_min = $duration
        b2_decision_accuracy = $b2Acc
        b2_abstention_rate   = $abstRate
        b2_decision_coverage = $coverage
        b2_macro_f1          = $macroF1
    }
    $configJson | ConvertTo-Json -Depth 3 | Set-Content "$outDir\CAU_HINH_MODEL.json" -Encoding UTF8

    $color = if ($status -eq "COMPLETE") { "Green" } else { "Red" }
    Write-Host ""
    Write-Host "  STATUS   : $status" -ForegroundColor $color
    Write-Host "  Duration : $duration min"
    Write-Host "  B2 Accuracy : $b2Acc | Abstention: $abstRate | Coverage: $coverage | Macro F1: $macroF1"
    Write-Host ""

    $globalResults += [PSCustomObject]@{
        Model         = $model
        Status        = $status
        DurationMin   = $duration
        B2_Accuracy   = $b2Acc
        AbstentionRate = $abstRate
        Coverage      = $coverage
        MacroF1       = $macroF1
    }
}

$totalDuration = [math]::Round(((Get-Date) - $totalStart).TotalMinutes, 1)
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  KET QUA TONG HOP THUC NGHIEM (HE THONG 2)" -ForegroundColor Cyan
Write-Host "  Total time: $totalDuration min" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
$globalResults | Format-Table -AutoSize
