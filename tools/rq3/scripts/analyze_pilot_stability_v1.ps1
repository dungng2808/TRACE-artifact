param(
    [string]$MainPredictions = 'R:\BaoVer2\ThucNgiem\RQ3_Phase3\pilot_runs_v1\RQ3_Batch_20260814_094704\predictions.json',
    [string]$StabilityRoot = 'R:\BaoVer2\ThucNgiem\RQ3_Phase3\stability_runs_v1',
    [string]$OutputPath = 'R:\BaoVer2\ThucNgiem\RQ3_Phase3\pilot_results_v1\pilot_stability_v1.json'
)

$ErrorActionPreference = 'Stop'
$selectedCases = @(
    'RQ3-P-COMMONSDBUTILS-01',
    'RQ3-P-COMMONSJEXL-03',
    'RQ3-P-COMMONSTEXT-07',
    'RQ3-P-COMMONSVALIDATOR-09'
)
$verifiers = @('B1_GENERAL_REVIEWER', 'B2_SPECIALIZED_MULTI_AGENT')
$main = Get-Content -Raw -LiteralPath $MainPredictions | ConvertFrom-Json
$main = @($main)
$repeatMaps = @{}
foreach ($repetition in 1..2) {
    $repeatRows = @()
    Get-ChildItem (Join-Path $StabilityRoot "rep$repetition") -Recurse -Filter 'predictions.json' | ForEach-Object {
        $parsed = Get-Content -Raw -LiteralPath $_.FullName | ConvertFrom-Json
        $repeatRows += $parsed
    }
    if ($repeatRows.Count -ne 12) { throw "Expected 12 prediction rows for repetition $repetition, found $($repeatRows.Count)." }
    $repeatMaps[$repetition] = $repeatRows
}

$comparisons = foreach ($caseId in $selectedCases) {
    foreach ($verifier in $verifiers) {
        $m = @($main | Where-Object { $_.CaseId -eq $caseId -and $_.Verifier -eq $verifier })[0]
        $r1 = @($repeatMaps[1] | Where-Object { $_.CaseId -eq $caseId -and $_.Verifier -eq $verifier })[0]
        $r2 = @($repeatMaps[2] | Where-Object { $_.CaseId -eq $caseId -and $_.Verifier -eq $verifier })[0]
        $mainTuple = "$($m.Decision)|$($m.Cause)|$($m.Route)"
        $rep1Tuple = "$($r1.Decision)|$($r1.Cause)|$($r1.Route)"
        $rep2Tuple = "$($r2.Decision)|$($r2.Cause)|$($r2.Route)"
        [ordered]@{
            case_id = $caseId
            verifier = $verifier
            main = $mainTuple
            repetition_1 = $rep1Tuple
            repetition_2 = $rep2Tuple
            repetitions_agree = ($rep1Tuple -eq $rep2Tuple)
            all_three_agree = ($mainTuple -eq $rep1Tuple -and $rep1Tuple -eq $rep2Tuple)
            execution_statuses = @($m.ExecutionStatus, $r1.ExecutionStatus, $r2.ExecutionStatus)
        }
    }
}

$allRepeatPredictions = @($repeatMaps[1]) + @($repeatMaps[2])
$result = [ordered]@{
    schema_version = 'rq3-pilot-stability-v1'
    generated_at = [DateTimeOffset]::UtcNow.ToString('o')
    selected_cases = $selectedCases
    repetitions_per_case = 2
    comparison_tuples = 'decision|cause|route'
    comparisons = @($comparisons)
    repetition_agreement_rate = [math]::Round((@($comparisons | Where-Object repetitions_agree).Count / [double]$comparisons.Count), 6)
    main_plus_repetitions_agreement_rate = [math]::Round((@($comparisons | Where-Object all_three_agree).Count / [double]$comparisons.Count), 6)
    api_or_output_errors_in_repetitions = @($allRepeatPredictions | Where-Object ExecutionStatus -ne 'OK').Count
    repetition_cost_usd = [math]::Round((($allRepeatPredictions | Measure-Object CostUsd -Sum).Sum), 8)
}
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
$result | ConvertTo-Json -Depth 8
