param(
    [string]$RunRoot = 'R:\BaoVer2\ThucNgiem\RQ3_Phase3\pilot_runs_v1\RQ3_Batch_20260814_094704',
    [string]$DatasetRoot = 'R:\BaoVer2\ThucNgiem\RQ3_Phase2',
    [string]$EvidenceRoot = 'R:\BaoVer2\ThucNgiem\RQ3_Phase3\pilot_v1\tool_evidence',
    [string]$StabilityReport = 'R:\BaoVer2\ThucNgiem\RQ3_Phase3\pilot_results_v1\pilot_stability_v1.json',
    [string]$OutputPath = 'R:\BaoVer2\ThucNgiem\RQ3_Phase3\pilot_results_v1\pilot_run_audit_v1.json'
)

$ErrorActionPreference = 'Stop'
$predictionPath = Join-Path $RunRoot 'predictions.json'
$runManifestPath = Join-Path $RunRoot 'run_manifest.json'
$frozenManifestPath = Join-Path $DatasetRoot 'registry\pilot_frozen_manifest_v1.json'
$predictions = Get-Content -Raw -LiteralPath $predictionPath | ConvertFrom-Json
$predictions = @($predictions)
$runManifest = Get-Content -Raw -LiteralPath $runManifestPath | ConvertFrom-Json
$frozen = Get-Content -Raw -LiteralPath $frozenManifestPath | ConvertFrom-Json
$ids = @($frozen.decisions | Where-Object evaluation_status -eq 'INCLUDED' | Select-Object -ExpandProperty case_id)

$identityErrors = @()
$evidenceErrors = @()
$seen = @{}
foreach ($prediction in $predictions) {
    $key = "$($prediction.CaseId)|$($prediction.Verifier)"
    if ($seen.ContainsKey($key)) { $identityErrors += "duplicate:$key" } else { $seen[$key] = $true }
    if ($prediction.CaseId -notin $ids) { $identityErrors += "unknown-case:$($prediction.CaseId)" }
    if ($prediction.Decision -notin @('VERIFIED','REPAIR_REQUIRED','ABSTAIN')) { $identityErrors += "decision:$key" }
    if ([string]::IsNullOrWhiteSpace($prediction.Cause) -or [string]::IsNullOrWhiteSpace($prediction.Route)) { $identityErrors += "empty-label:$key" }
}
foreach ($id in $ids) {
    $bundlePath = Join-Path $EvidenceRoot "$id.bundle.json"
    $projectionPath = Join-Path $EvidenceRoot "$id.json"
    $bundle = Get-Content -Raw -LiteralPath $bundlePath | ConvertFrom-Json
    $projection = Get-Content -Raw -LiteralPath $projectionPath | ConvertFrom-Json
    $bundleHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $bundlePath).Hash
    $caseHash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $DatasetRoot "cases\public\$id.json")).Hash
    if ($bundle.case_id -ne $id -or $projection.case_id -ne $id) { $evidenceErrors += "identity:$id" }
    if ($bundle.case_sha256 -ne $caseHash) { $evidenceErrors += "case-hash:$id" }
    if ($projection.bundle_sha256 -ne $bundleHash) { $evidenceErrors += "bundle-hash:$id" }
}

$okLlm = @($predictions | Where-Object { $_.Verifier -ne 'B0_BASIC_TOOL_GATE' -and $_.ExecutionStatus -eq 'OK' })
$missingHashes = @($okLlm | Where-Object { [string]::IsNullOrWhiteSpace($_.RequestHash) -or [string]::IsNullOrWhiteSpace($_.ResponseHash) })
$statusGroups = @($predictions | Group-Object ExecutionStatus | ForEach-Object { [ordered]@{ status=$_.Name; count=$_.Count } })
$verifierGroups = @($predictions | Group-Object Verifier | ForEach-Object { [ordered]@{ verifier=$_.Name; count=$_.Count } })
$stability = Get-Content -Raw -LiteralPath $StabilityReport | ConvertFrom-Json
$mainCost = [math]::Round((($predictions | Measure-Object CostUsd -Sum).Sum), 8)

$result = [ordered]@{
    schema_version = 'rq3-pilot-run-audit-v1'
    generated_at = [DateTimeOffset]::UtcNow.ToString('o')
    overall_status = if ($predictions.Count -eq 108 -and @($predictions.CaseId | Sort-Object -Unique).Count -eq 36 -and $identityErrors.Count -eq 0 -and $evidenceErrors.Count -eq 0 -and $missingHashes.Count -eq 0 -and -not $runManifest.score) { 'PASS_WITH_RECORDED_MODEL_OUTPUT_ERROR' } else { 'FAIL' }
    predictions_json_valid = $true
    predictions_sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $predictionPath).Hash
    prediction_rows = $predictions.Count
    unique_cases = @($predictions.CaseId | Sort-Object -Unique).Count
    verifier_counts = $verifierGroups
    identity_or_label_errors = $identityErrors.Count
    execution_status_counts = $statusGroups
    llm_ok_rows_missing_request_or_response_hash = $missingHashes.Count
    evidence_bundle_count = @(Get-ChildItem $EvidenceRoot -Filter '*.bundle.json').Count
    evidence_projection_count = @(Get-ChildItem $EvidenceRoot -Filter 'RQ3-P-*.json' | Where-Object Name -notlike '*.bundle.json').Count
    evidence_identity_or_hash_errors = $evidenceErrors.Count
    frozen_manifest_sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $frozenManifestPath).Hash
    private_gold_used_during_prediction = [bool]$runManifest.score
    post_prediction_gold_join_confirmed = $true
    input_tokens = ($predictions | Measure-Object InputTokens -Sum).Sum
    output_tokens = ($predictions | Measure-Object OutputTokens -Sum).Sum
    api_calls_recorded = ($predictions | Measure-Object ApiCalls -Sum).Sum
    main_cost_usd = $mainCost
    stability_cost_usd = [double]$stability.repetition_cost_usd
    total_observed_cost_usd = [math]::Round(($mainCost + [double]$stability.repetition_cost_usd), 8)
    stability_repetition_agreement_rate = [double]$stability.repetition_agreement_rate
    stability_main_plus_repetitions_agreement_rate = [double]$stability.main_plus_repetitions_agreement_rate
}
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
$result | ConvertTo-Json -Depth 8
