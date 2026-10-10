param(
    [Parameter(Mandatory = $true)][string]$PredictionsJson,
    [string]$Phase2Root = 'R:\BaoVer2\ThucNgiem\RQ3_Phase2',
    [string]$Manifest = 'R:\BaoVer2\ThucNgiem\RQ3_Phase2\registry\pilot_frozen_manifest_v1.json',
    [string]$OutputRoot = 'R:\BaoVer2\ThucNgiem\RQ3_Phase3\pilot_results_v1'
)

$ErrorActionPreference = 'Stop'
$truthMap = @{
    NONE_VALID = @('VERIFIED', 'NONE', 'NONE')
    LOCAL_COMPILATION_FAILURE = @('REPAIR_REQUIRED', 'COMPILATION_ERROR', 'LOCAL_TEST_REPAIR')
    LOCAL_RUNTIME_OR_JUNIT_FAILURE = @('REPAIR_REQUIRED', 'SETUP_OR_RUNTIME_ERROR', 'LOCAL_TEST_REPAIR')
    PLAN_TEST_MISMATCH = @('REPAIR_REQUIRED', 'PLAN_TEST_MISMATCH', 'REGENERATE_TEST')
    STRUCTURAL_TARGET_NOT_EXECUTED = @('REPAIR_REQUIRED', 'TARGET_NOT_REACHED', 'REGENERATE_TEST')
    EXPECTED_OBSERVATION_OR_ASSERTION_MISMATCH = @('REPAIR_REQUIRED', 'ASSERTION_ERROR', 'LOCAL_TEST_REPAIR')
    WEAK_ORACLE_RELEVANT_MUTANT_SURVIVES = @('REPAIR_REQUIRED', 'WEAK_ORACLE', 'LOCAL_TEST_REPAIR')
    SHARED_INFRASTRUCTURE_FAILURE = @('REPAIR_REQUIRED', 'SHARED_INFRASTRUCTURE_ERROR', 'SHARED_INFRASTRUCTURE_REPAIR')
    INSUFFICIENT_CONTEXT = @('ABSTAIN', 'INSUFFICIENT_EVIDENCE', 'REQUEST_TOOL_EVIDENCE')
}

$manifestObject = Get-Content -Raw -LiteralPath $Manifest | ConvertFrom-Json
$ids = @($manifestObject.decisions | Where-Object evaluation_status -eq 'INCLUDED' | Select-Object -ExpandProperty case_id)
if ($ids.Count -ne 36) { throw "Expected 36 frozen pilot cases, found $($ids.Count)." }
$predictions = Get-Content -Raw -LiteralPath $PredictionsJson | ConvertFrom-Json
$predictions = @($predictions)
if ($predictions.Count -ne 108) { throw "Expected 108 predictions (36 x B0/B1/B2), found $($predictions.Count)." }

$rows = foreach ($prediction in $predictions) {
    if ($prediction.CaseId -notin $ids) { throw "Prediction contains non-pilot case $($prediction.CaseId)." }
    $goldPath = Join-Path $Phase2Root "gold\private\$($prediction.CaseId).json"
    $gold = Get-Content -Raw -LiteralPath $goldPath | ConvertFrom-Json
    $truth = $truthMap[$gold.intended_gold_category]
    if ($null -eq $truth) { throw "Unmapped gold category $($gold.intended_gold_category)." }
    [pscustomobject]@{
        case_id = $prediction.CaseId
        verifier = $prediction.Verifier
        decision = $prediction.Decision
        cause = $prediction.Cause
        route = $prediction.Route
        abstained = [bool]$prediction.Abstained
        execution_status = $prediction.ExecutionStatus
        input_tokens = [int]$prediction.InputTokens
        output_tokens = [int]$prediction.OutputTokens
        api_calls = [int]$prediction.ApiCalls
        cost_usd = [double]$prediction.CostUsd
        request_hash = $prediction.RequestHash
        response_hash = $prediction.ResponseHash
        gold_category = $gold.intended_gold_category
        gold_decision = $truth[0]
        gold_cause = $truth[1]
        gold_route = $truth[2]
    }
}

function Rate([int]$Numerator, [int]$Denominator) {
    if ($Denominator -eq 0) { return 0.0 }
    return [math]::Round($Numerator / [double]$Denominator, 6)
}

function MacroCauseF1($items) {
    $labels = @($items | Select-Object -ExpandProperty gold_cause -Unique)
    $scores = foreach ($label in $labels) {
        $tp = @($items | Where-Object { $_.gold_cause -eq $label -and $_.cause -eq $label }).Count
        $fp = @($items | Where-Object { $_.gold_cause -ne $label -and $_.cause -eq $label }).Count
        $fn = @($items | Where-Object { $_.gold_cause -eq $label -and $_.cause -ne $label }).Count
        $precision = Rate $tp ($tp + $fp)
        $recall = Rate $tp ($tp + $fn)
        if (($precision + $recall) -eq 0) { 0.0 } else { 2.0 * $precision * $recall / ($precision + $recall) }
    }
    return [math]::Round((($scores | Measure-Object -Average).Average), 6)
}

$metrics = foreach ($group in ($rows | Group-Object verifier)) {
    $r = @($group.Group)
    $repair = @($r | Where-Object gold_decision -eq 'REPAIR_REQUIRED')
    $predictedVerified = @($r | Where-Object decision -eq 'VERIFIED')
    $falsePass = @($r | Where-Object { $_.gold_decision -eq 'REPAIR_REQUIRED' -and $_.decision -eq 'VERIFIED' })
    $falsePassPrecision = Rate $falsePass.Count $predictedVerified.Count
    $falsePassRecall = Rate $falsePass.Count $repair.Count
    $falsePassF1 = if (($falsePassPrecision + $falsePassRecall) -eq 0) { 0.0 } else { [math]::Round(2.0 * $falsePassPrecision * $falsePassRecall / ($falsePassPrecision + $falsePassRecall), 6) }
    $covered = @($r | Where-Object decision -ne 'ABSTAIN')
    [ordered]@{
        verifier = $group.Name
        cases = $r.Count
        decision_accuracy = Rate (@($r | Where-Object { $_.decision -eq $_.gold_decision }).Count) $r.Count
        false_pass_precision = $falsePassPrecision
        false_pass_recall = $falsePassRecall
        false_pass_f1 = $falsePassF1
        false_acceptance_rate = $falsePassRecall
        macro_attribution_f1 = MacroCauseF1 $r
        routing_accuracy = Rate (@($r | Where-Object { $_.route -eq $_.gold_route }).Count) $r.Count
        abstention_rate = Rate (@($r | Where-Object decision -eq 'ABSTAIN').Count) $r.Count
        decision_coverage = Rate $covered.Count $r.Count
        selective_accuracy = Rate (@($covered | Where-Object { $_.decision -eq $_.gold_decision }).Count) $covered.Count
        api_or_output_error_count = @($r | Where-Object execution_status -notin @('OK')).Count
        input_tokens = ($r | Measure-Object input_tokens -Sum).Sum
        output_tokens = ($r | Measure-Object output_tokens -Sum).Sum
        api_calls = ($r | Measure-Object api_calls -Sum).Sum
        cost_usd = [math]::Round((($r | Measure-Object cost_usd -Sum).Sum), 8)
    }
}

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
$rows | Export-Csv -LiteralPath (Join-Path $OutputRoot 'pilot_predictions_joined_private_gold_v1.csv') -NoTypeInformation -Encoding UTF8
$result = [ordered]@{
    schema_version = 'rq3-pilot-metrics-v1.1'
    generated_at = [DateTimeOffset]::UtcNow.ToString('o')
    predictions_sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $PredictionsJson).Hash
    manifest_sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $Manifest).Hash
    evaluation_cases = $ids.Count
    prediction_rows = $rows.Count
    scoring_note = 'Private gold was joined only after predictions.json had been finalized and hashed.'
    metrics = @($metrics)
}
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputRoot 'pilot_metrics_v1.json') -Encoding UTF8
$metrics | ConvertTo-Json -Depth 6
