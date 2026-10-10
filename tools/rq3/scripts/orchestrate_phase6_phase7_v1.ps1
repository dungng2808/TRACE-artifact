param(
    [string]$WorkspaceRoot = 'R:\BaoVer2',
    [int]$PollSeconds = 60,
    [int]$MaximumWaitHours = 72,
    [string]$ResumeRunDirectory = ''
)

$ErrorActionPreference = 'Stop'
$phase2 = Join-Path $WorkspaceRoot 'ThucNgiem\RQ3_Phase2'
$phase3 = Join-Path $WorkspaceRoot 'ThucNgiem\RQ3_Phase3'
$manifest = Join-Path $phase3 'registry\phase6_main_amended_manifest_v1.json'
$evidenceRoot = Join-Path $phase3 'phase6_main_v1\evidence'
$toolEvidenceRoot = Join-Path $phase3 'phase6_main_v1\tool_evidence'
$statusPath = Join-Path $phase3 'phase6_main_v1\orchestration_status.json'
$freezePath = Join-Path $phase3 'phase6_main_v1\phase6_evidence_freeze_v1.json'
$runner = Join-Path $phase3 'tools\RQ3BatchRunner\bin\Release\net8.0\RQ3BatchRunner.dll'
$keyFile = Join-Path $WorkspaceRoot 'OpenRouterKey.txt'
$runsRoot = Join-Path $phase3 'phase6_runs_v1'
$resultsRoot = Join-Path $phase3 'phase6_results_v1'
$scoreScript = Join-Path $phase3 'scripts\score_phase6_amended_main_v1.ps1'
$stabilityScript = Join-Path $phase3 'scripts\run_phase6_stability_v1.ps1'

function Save-Status([string]$Stage, [string]$Message, [int]$Bundles) {
    [ordered]@{
        schema_version = 'rq3-phase6-orchestration-status-v1'
        updated_at = [DateTimeOffset]::UtcNow.ToString('o')
        stage = $Stage
        message = $Message
        evidence_bundles = $Bundles
        required_bundles = 73
    } | ConvertTo-Json | Set-Content -LiteralPath $statusPath -Encoding utf8
}

$manifestObject = Get-Content -Raw -LiteralPath $manifest | ConvertFrom-Json
$ids = @($manifestObject.decisions | Select-Object -ExpandProperty case_id)
$deadline = [DateTimeOffset]::UtcNow.AddHours($MaximumWaitHours)
while ($true) {
    $bundleCount = @($ids | Where-Object { Test-Path -LiteralPath (Join-Path $evidenceRoot "$_\evidence_bundle.json") }).Count
    Save-Status 'WAITING_FOR_EVIDENCE' "Waiting for all evidence workers." $bundleCount
    if ($bundleCount -eq $ids.Count) { break }
    if ([DateTimeOffset]::UtcNow -ge $deadline) { throw "Evidence wait exceeded $MaximumWaitHours hours; bundles=$bundleCount/$($ids.Count)." }
    $workers = @(Get-CimInstance Win32_Process | Where-Object {
        $_.Name -eq 'powershell.exe' -and $_.ProcessId -ne $PID -and $_.CommandLine -match 'run_phase6_evidence_(batch|shard)\.ps1'
    })
    $collectors = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'dotnet.exe' -and $_.CommandLine -match 'RQ3EvidenceCollector' })
    if ($workers.Count -eq 0 -and $collectors.Count -eq 0) { throw "Evidence workers stopped before completion; bundles=$bundleCount/$($ids.Count)." }
    Start-Sleep -Seconds $PollSeconds
}

Save-Status 'VALIDATING_EVIDENCE' 'Validating public-case, bundle, projection, and identity hashes.' $ids.Count
$entries = foreach ($caseId in $ids) {
    $casePath = Join-Path $phase2 "cases\public\$caseId.json"
    $bundlePath = Join-Path $evidenceRoot "$caseId\evidence_bundle.json"
    $toolBundlePath = Join-Path $toolEvidenceRoot "$caseId.bundle.json"
    $projectionPath = Join-Path $toolEvidenceRoot "$caseId.json"
    foreach ($required in @($casePath, $bundlePath, $toolBundlePath, $projectionPath)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Missing evidence artifact: $required" }
    }
    $bundle = Get-Content -Raw -LiteralPath $bundlePath | ConvertFrom-Json
    $projection = Get-Content -Raw -LiteralPath $projectionPath | ConvertFrom-Json
    $caseHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $casePath).Hash
    $bundleHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $bundlePath).Hash
    $toolBundleHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $toolBundlePath).Hash
    if ($bundle.case_id -ne $caseId -or $projection.case_id -ne $caseId) { throw "Case identity mismatch for $caseId." }
    if ($bundle.case_sha256 -ne $caseHash) { throw "Public-case hash mismatch for $caseId." }
    if ($projection.bundle_sha256 -ne $bundleHash) { throw "Projection-to-bundle hash mismatch for $caseId." }
    if ($toolBundleHash -ne $bundleHash) { throw "Tool bundle differs from frozen evidence bundle for $caseId." }
    [ordered]@{
        case_id = $caseId
        public_case_sha256 = $caseHash
        evidence_bundle_sha256 = $bundleHash
        public_projection_sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $projectionPath).Hash
    }
}

[ordered]@{
    schema_version = 'rq3-phase6-evidence-freeze-v1'
    frozen_at = [DateTimeOffset]::UtcNow.ToString('o')
    protocol_amendment_sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $phase3 'protocol\PHASE6_PROTOCOL_AMENDMENT_V1.md')).Hash
    cohort_manifest_sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $manifest).Hash
    evidence_count = $entries.Count
    entries = $entries
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $freezePath -Encoding utf8

Save-Status 'RUNNING_B0_B1_B2' 'Evidence frozen; running blind predictions without scoring.' $ids.Count
New-Item -ItemType Directory -Force -Path $runsRoot | Out-Null
if ([string]::IsNullOrWhiteSpace($ResumeRunDirectory)) {
    $before = @(Get-ChildItem -LiteralPath $runsRoot -Directory -ErrorAction SilentlyContinue).FullName
    & dotnet $runner `
        "--dataset=$phase2" `
        "--out=$runsRoot" `
        "--manifest=$manifest" `
        "--evidence-root=$toolEvidenceRoot" `
        '--endpoint=https://openrouter.ai/api/v1/chat/completions' `
        '--model=google/gemini-2.5-flash' `
        "--api-key-file=$keyFile" `
        '--b1' `
        '--b2'
    if ($LASTEXITCODE -ne 0) { throw "Main B0/B1/B2 runner failed with exit code $LASTEXITCODE." }
    $runDirectory = Get-ChildItem -LiteralPath $runsRoot -Directory | Where-Object FullName -notin $before | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
} else {
    $runDirectory = Get-Item -LiteralPath $ResumeRunDirectory
}
$predictionsPath = Join-Path $runDirectory.FullName 'predictions.json'
$predictions = @((Get-Content -Raw -LiteralPath $predictionsPath | ConvertFrom-Json))
if ($predictions.Count -ne 219) { throw "Expected 219 finalized predictions, found $($predictions.Count)." }
$predictionFreezePath = Join-Path $runDirectory.FullName 'prediction_freeze_v1.json'
[ordered]@{
    schema_version = 'rq3-phase6-prediction-freeze-v1'
    frozen_at = [DateTimeOffset]::UtcNow.ToString('o')
    predictions_sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $predictionsPath).Hash
    prediction_rows = $predictions.Count
    evidence_freeze_sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $freezePath).Hash
    private_gold_joined = $false
} | ConvertTo-Json | Set-Content -LiteralPath $predictionFreezePath -Encoding utf8

Save-Status 'SCORING' 'Predictions frozen; joining private gold in the separate scorer.' $ids.Count
& $scoreScript -PredictionsJson $predictionsPath -Manifest $manifest -OutputRoot $resultsRoot
if (-not $?) { throw 'Phase 6 scorer failed.' }

Save-Status 'RUNNING_STABILITY' 'Running the preregistered 20 percent stability subset.' $ids.Count
& $stabilityScript -MainPredictionsJson $predictionsPath -Manifest $manifest -EvidenceRoot $toolEvidenceRoot
if (-not $?) { throw 'Phase 6 stability run failed.' }

Save-Status 'COMPLETE' "Phase 6 and Phase 7 machine analysis completed. Main run: $($runDirectory.FullName)" $ids.Count
