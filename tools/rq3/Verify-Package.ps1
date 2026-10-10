$ErrorActionPreference = 'Stop'
$systemRoot = $PSScriptRoot
$packageRoot = Split-Path -Parent $systemRoot
$datasetRoot = Join-Path $packageRoot 'datasets\rq3\phase6_exploratory_v2_dataset'
$readinessScript = Join-Path $systemRoot 'scripts\test_exploratory_v2_readiness.ps1'

Write-Host 'Checking dataset...'
& $readinessScript -DatasetRoot $datasetRoot
if (-not $?) { throw 'Dataset readiness failed.' }

$expected = @{
    'KQ_Thucnghiem_VerHeThong2_gemini_3_5_flash_google_ai_studio_exploratory_v2_20260826_101118' = 0
    'KQ_Thucnghiem_VerHeThong2_deepseek_v4_pro_exploratory_v2_free_repaired_20260831' = 0
    'KQ_Thucnghiem_VerHeThong2_glm_5_3_flash_bai_free_20260831' = 87
    'KQ_Thucnghiem_VerHeThong2_deepseek_v4_flash_bai_free_20260831' = 0
    'KQ_Thucnghiem_VerHeThong2_gemini_3_7_flash_google_ai_studio_exploratory_v2_20260826_185008' = 0
    'KQ_Thucnghiem_VerHeThong2_gemini_3_7_flash_medium_local_api_exploratory_v2_20260829_232624' = 0
    'KQ_Thucnghiem_VerHeThong2_qwen3_8_flash_bai_free_20260904' = 0
}

$resultRoots = @(
    (Join-Path $packageRoot 'results\rq3\in_paper'),
    (Join-Path $packageRoot 'results\rq3\valid_not_in_paper')
)

$verified = 0
foreach ($resultRoot in $resultRoots) {
    foreach ($run in Get-ChildItem -LiteralPath $resultRoot -Directory) {
        if (-not $expected.ContainsKey($run.Name)) { throw "Unexpected run: $($run.FullName)" }

        $metricsFile = Get-ChildItem -LiteralPath $run.FullName -Recurse -File -Filter 'metrics_v2.json' | Select-Object -First 1
        $joinedFile = Get-ChildItem -LiteralPath $run.FullName -Recurse -File -Filter 'predictions_joined_private_gold_v2.csv' | Select-Object -First 1
        if (-not $metricsFile -or -not $joinedFile) { throw "Missing metrics/CSV: $($run.Name)" }

        $metrics = Get-Content -LiteralPath $metricsFile.FullName -Raw | ConvertFrom-Json
        $csvCount = @(Import-Csv -LiteralPath $joinedFile.FullName).Count
        $errorCount = (@($metrics.metrics) | Measure-Object -Property api_or_output_error_count -Sum).Sum
        if ($metrics.evaluation_cases -ne 73 -or $metrics.prediction_rows -ne 219 -or $csvCount -ne 219) {
            throw "Incorrect case/prediction count: $($run.Name)"
        }
        if ($errorCount -ne $expected[$run.Name]) {
            throw "Incorrect output error count in $($run.Name): got $errorCount, expected $($expected[$run.Name])"
        }

        $hashMatched = $false
        foreach ($candidate in Get-ChildItem -LiteralPath $run.FullName -Recurse -File | Where-Object { $_.Name -in @('predictions_merged_v2.json', 'predictions.json') }) {
            if ((Get-FileHash -LiteralPath $candidate.FullName -Algorithm SHA256).Hash -eq $metrics.predictions_sha256) {
                $hashMatched = $true
                break
            }
        }
        if (-not $hashMatched) { throw "No prediction file matches the metric hash: $($run.Name)" }

        Write-Host "PASS $($run.Name) - 73 cases, 219 rows, errors=$errorCount"
        $verified++
    }
}

if ($verified -ne $expected.Count) { throw "Verified only $verified/$($expected.Count) runs." }

$summaryCsv = Join-Path $packageRoot 'results\rq3\csv\TONG_HOP_KET_QUA.csv'
if (@(Import-Csv -LiteralPath $summaryCsv).Count -ne 14) { throw 'TONG_HOP_KET_QUA.csv must contain 14 B1/B2 rows.' }

Write-Host "Package valid: 73 cases, $verified runs, 14 B1/B2 summary rows."
