param(
    [Parameter(Mandatory = $true)][string]$PredictionsJson,
    [string]$Phase2Root = 'R:\BaoVer2\ThucNgiem\RQ3_Phase2',
    [string]$Manifest = 'R:\BaoVer2\ThucNgiem\RQ3_Phase3\registry\phase6_main_amended_manifest_v1.json',
    [string]$OutputRoot = 'R:\BaoVer2\ThucNgiem\RQ3_Phase3\phase6_results_v1',
    [int]$BootstrapIterations = 5000
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

function Rate([int]$Numerator, [int]$Denominator) {
    if ($Denominator -eq 0) { return 0.0 }
    return [math]::Round($Numerator / [double]$Denominator, 6)
}

function WilsonInterval([int]$Successes, [int]$Total) {
    if ($Total -eq 0) { return [ordered]@{ lower = 0.0; upper = 0.0 } }
    $z = 1.95996398454005
    $p = $Successes / [double]$Total
    $denominator = 1.0 + ($z * $z / $Total)
    $center = ($p + ($z * $z / (2.0 * $Total))) / $denominator
    $half = $z * [math]::Sqrt(($p * (1.0 - $p) / $Total) + ($z * $z / (4.0 * $Total * $Total))) / $denominator
    return [ordered]@{ lower = [math]::Round($center - $half, 6); upper = [math]::Round($center + $half, 6) }
}

function MacroF1($Items, [string]$TruthField, [string]$PredictionField) {
    $labels = @($Items | Select-Object -ExpandProperty $TruthField -Unique)
    if ($labels.Count -eq 0) { return 0.0 }
    $scores = foreach ($label in $labels) {
        $tp = @($Items | Where-Object { $_.$TruthField -eq $label -and $_.$PredictionField -eq $label }).Count
        $fp = @($Items | Where-Object { $_.$TruthField -ne $label -and $_.$PredictionField -eq $label }).Count
        $fn = @($Items | Where-Object { $_.$TruthField -eq $label -and $_.$PredictionField -ne $label }).Count
        $precision = Rate $tp ($tp + $fp)
        $recall = Rate $tp ($tp + $fn)
        if (($precision + $recall) -eq 0) { 0.0 } else { 2.0 * $precision * $recall / ($precision + $recall) }
    }
    return [math]::Round((($scores | Measure-Object -Average).Average), 6)
}

function ExactMcNemarP([int]$B, [int]$C) {
    $n = $B + $C
    if ($n -eq 0) { return 1.0 }
    $tail = [math]::Min($B, $C)
    $probability = [math]::Pow(0.5, $n)
    $sum = $probability
    for ($k = 1; $k -le $tail; $k++) {
        $probability *= ($n - $k + 1) / [double]$k
        $sum += $probability
    }
    return [math]::Round([math]::Min(1.0, 2.0 * $sum), 8)
}

function Percentile([double[]]$Values, [double]$P) {
    $sorted = @($Values | Sort-Object)
    if ($sorted.Count -eq 0) { return 0.0 }
    $position = ($sorted.Count - 1) * $P
    $lower = [math]::Floor($position)
    $upper = [math]::Ceiling($position)
    if ($lower -eq $upper) { return [double]$sorted[$lower] }
    return [double]$sorted[$lower] + ($position - $lower) * ([double]$sorted[$upper] - [double]$sorted[$lower])
}

$manifestObject = Get-Content -Raw -LiteralPath $Manifest | ConvertFrom-Json
$ids = @($manifestObject.decisions | Where-Object evaluation_status -eq 'INCLUDED' | Select-Object -ExpandProperty case_id)
if ($ids.Count -ne 73) { throw "Expected 73 amended main cases, found $($ids.Count)." }

$predictionsSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $PredictionsJson).Hash
$predictions = @((Get-Content -Raw -LiteralPath $PredictionsJson | ConvertFrom-Json))
if ($predictions.Count -ne 219) { throw "Expected 219 predictions (73 x B0/B1/B2), found $($predictions.Count)." }

$duplicateKeys = @($predictions | Group-Object { "$($_.CaseId)|$($_.Verifier)" } | Where-Object Count -ne 1)
if ($duplicateKeys.Count -ne 0) { throw 'Predictions do not contain exactly one row per case/verifier.' }

$rows = foreach ($prediction in $predictions) {
    if ($prediction.CaseId -notin $ids) { throw "Prediction contains non-cohort case $($prediction.CaseId)." }
    $casePath = Join-Path $Phase2Root "cases\public\$($prediction.CaseId).json"
    $goldPath = Join-Path $Phase2Root "gold\private\$($prediction.CaseId).json"
    $case = Get-Content -Raw -LiteralPath $casePath | ConvertFrom-Json
    $gold = Get-Content -Raw -LiteralPath $goldPath | ConvertFrom-Json
    $truth = $truthMap[$gold.intended_gold_category]
    if ($null -eq $truth) { throw "Unmapped gold category $($gold.intended_gold_category)." }
    [pscustomobject]@{
        case_id = $prediction.CaseId
        project = $case.repository.project
        case_origin = $case.case_origin
        verifier = $prediction.Verifier
        decision = $prediction.Decision
        cause = $prediction.Cause
        route = $prediction.Route
        abstained = [bool]$prediction.Abstained
        execution_status = $prediction.ExecutionStatus
        latency_ms = [long]$prediction.LatencyMs
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
        decision_correct = $prediction.Decision -eq $truth[0]
        cause_correct = $prediction.Cause -eq $truth[1]
        route_correct = $prediction.Route -eq $truth[2]
    }
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
    $correct = @($r | Where-Object decision_correct).Count
    [ordered]@{
        verifier = $group.Name
        cases = $r.Count
        decision_accuracy = Rate $correct $r.Count
        decision_accuracy_wilson_95 = WilsonInterval $correct $r.Count
        false_pass_precision = $falsePassPrecision
        false_pass_recall = $falsePassRecall
        false_pass_f1 = $falsePassF1
        false_acceptance_rate = $falsePassRecall
        macro_attribution_f1 = MacroF1 $r 'gold_cause' 'cause'
        routing_accuracy = Rate (@($r | Where-Object route_correct).Count) $r.Count
        routing_macro_f1 = MacroF1 $r 'gold_route' 'route'
        abstention_rate = Rate (@($r | Where-Object decision -eq 'ABSTAIN').Count) $r.Count
        decision_coverage = Rate $covered.Count $r.Count
        selective_accuracy = Rate (@($covered | Where-Object decision_correct).Count) $covered.Count
        api_or_output_error_count = @($r | Where-Object execution_status -notin @('OK')).Count
        latency_ms = ($r | Measure-Object latency_ms -Sum).Sum
        input_tokens = ($r | Measure-Object input_tokens -Sum).Sum
        output_tokens = ($r | Measure-Object output_tokens -Sum).Sum
        api_calls = ($r | Measure-Object api_calls -Sum).Sum
        cost_usd = [math]::Round((($r | Measure-Object cost_usd -Sum).Sum), 8)
    }
}

$pairwise = foreach ($baseline in @('B0_BASIC_TOOL_GATE', 'B1_GENERAL_REVIEWER')) {
    $b = 0
    $c = 0
    foreach ($caseId in $ids) {
        $baseRow = $rows | Where-Object { $_.case_id -eq $caseId -and $_.verifier -eq $baseline }
        $b2Row = $rows | Where-Object { $_.case_id -eq $caseId -and $_.verifier -eq 'B2_SPECIALIZED_MULTI_AGENT' }
        if ($b2Row.decision_correct -and -not $baseRow.decision_correct) { $b++ }
        if ($baseRow.decision_correct -and -not $b2Row.decision_correct) { $c++ }
    }
    [pscustomobject][ordered]@{
        comparison = "B2_vs_$baseline"
        b2_correct_baseline_wrong = $b
        baseline_correct_b2_wrong = $c
        discordant_pairs = $b + $c
        exact_mcnemar_p = ExactMcNemarP $b $c
        holm_adjusted_p = $null
    }
}

$orderedPairwise = @($pairwise | Sort-Object exact_mcnemar_p)
$previous = 0.0
for ($index = 0; $index -lt $orderedPairwise.Count; $index++) {
    $adjusted = [math]::Min(1.0, ($orderedPairwise.Count - $index) * [double]$orderedPairwise[$index].exact_mcnemar_p)
    if ($adjusted -lt $previous) { $adjusted = $previous }
    $orderedPairwise[$index].holm_adjusted_p = [math]::Round($adjusted, 8)
    $previous = $adjusted
}

$random = [System.Random]::new(20260817)
$projects = @($rows.project | Sort-Object -Unique)
$bootstrap = foreach ($verifier in @($rows.verifier | Sort-Object -Unique)) {
    $clusterCounts = @{}
    foreach ($project in $projects) {
        $clusterRows = @($rows | Where-Object { $_.verifier -eq $verifier -and $_.project -eq $project })
        $clusterCounts[$project] = [pscustomobject]@{
            total = $clusterRows.Count
            correct = @($clusterRows | Where-Object decision_correct).Count
        }
    }
    $values = [System.Collections.Generic.List[double]]::new()
    for ($iteration = 0; $iteration -lt $BootstrapIterations; $iteration++) {
        $sampleTotal = 0
        $sampleCorrect = 0
        for ($draw = 0; $draw -lt $projects.Count; $draw++) {
            $project = $projects[$random.Next(0, $projects.Count)]
            $sampleTotal += $clusterCounts[$project].total
            $sampleCorrect += $clusterCounts[$project].correct
        }
        if ($sampleTotal -gt 0) { $values.Add(($sampleCorrect / [double]$sampleTotal)) }
    }
    [ordered]@{
        verifier = $verifier
        clusters = $projects.Count
        iterations = $BootstrapIterations
        decision_accuracy_clustered_bootstrap_95 = [ordered]@{
            lower = [math]::Round((Percentile $values.ToArray() 0.025), 6)
            upper = [math]::Round((Percentile $values.ToArray() 0.975), 6)
        }
    }
}

$breakdowns = [ordered]@{
    by_category = @($rows | Group-Object verifier, gold_category | ForEach-Object {
        $r = @($_.Group)
        [ordered]@{ verifier = $r[0].verifier; gold_category = $r[0].gold_category; cases = $r.Count; decision_accuracy = Rate (@($r | Where-Object decision_correct).Count) $r.Count }
    })
    by_origin = @($rows | Group-Object verifier, case_origin | ForEach-Object {
        $r = @($_.Group)
        [ordered]@{ verifier = $r[0].verifier; case_origin = $r[0].case_origin; cases = $r.Count; decision_accuracy = Rate (@($r | Where-Object decision_correct).Count) $r.Count }
    })
    by_project = @($rows | Group-Object verifier, project | ForEach-Object {
        $r = @($_.Group)
        [ordered]@{ verifier = $r[0].verifier; project = $r[0].project; cases = $r.Count; decision_accuracy = Rate (@($r | Where-Object decision_correct).Count) $r.Count }
    })
}

$confusions = [ordered]@{
    decision = @($rows | Group-Object verifier, gold_decision, decision | ForEach-Object { $r=@($_.Group); [ordered]@{verifier=$r[0].verifier;gold=$r[0].gold_decision;predicted=$r[0].decision;count=$r.Count} })
    cause = @($rows | Group-Object verifier, gold_cause, cause | ForEach-Object { $r=@($_.Group); [ordered]@{verifier=$r[0].verifier;gold=$r[0].gold_cause;predicted=$r[0].cause;count=$r.Count} })
    route = @($rows | Group-Object verifier, gold_route, route | ForEach-Object { $r=@($_.Group); [ordered]@{verifier=$r[0].verifier;gold=$r[0].gold_route;predicted=$r[0].route;count=$r.Count} })
}

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
$rows | Export-Csv -LiteralPath (Join-Path $OutputRoot 'phase6_predictions_joined_private_gold_v1.csv') -NoTypeInformation -Encoding UTF8
$result = [ordered]@{
    schema_version = 'rq3-phase6-amended-main-metrics-v1'
    generated_at = [DateTimeOffset]::UtcNow.ToString('o')
    cohort_status = $manifestObject.status
    predictions_sha256 = $predictionsSha256
    manifest_sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $Manifest).Hash
    evaluation_cases = $ids.Count
    prediction_rows = $rows.Count
    scoring_note = 'Private gold was joined only after predictions.json had been finalized and hashed.'
    limitations = $manifestObject.limitations
    metrics = @($metrics)
    paired_tests = $orderedPairwise
    clustered_bootstrap = @($bootstrap)
    breakdowns = $breakdowns
    confusion_matrices = $confusions
}
$metricsPath = Join-Path $OutputRoot 'phase6_metrics_v1.json'
$result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $metricsPath -Encoding UTF8
[ordered]@{
    predictions_sha256 = $predictionsSha256
    metrics_sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $metricsPath).Hash
    joined_csv_sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $OutputRoot 'phase6_predictions_joined_private_gold_v1.csv')).Hash
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputRoot 'phase6_result_hashes_v1.json') -Encoding UTF8
$metrics | ConvertTo-Json -Depth 8
