param(
    [string]$Dataset = 'R:\BaoVer2\ThucNgiem\RQ3_Phase2',
    [string]$Manifest = 'R:\BaoVer2\ThucNgiem\RQ3_Phase3\registry\phase6_main_amended_manifest_v1.json',
    [string]$OutputRoot = 'R:\BaoVer2\ThucNgiem\RQ3_Phase3\phase6_main_v1',
    [string]$DockerImage = 'rq3-defects4j:3.0.1'
)

$ErrorActionPreference = 'Stop'

$collector = 'R:\BaoVer2\ThucNgiem\RQ3_Phase3\tools\RQ3EvidenceCollector\bin\Release\net8.0\RQ3EvidenceCollector.dll'
$evidenceRoot = Join-Path $OutputRoot 'evidence'
$toolEvidenceRoot = Join-Path $OutputRoot 'tool_evidence'
$progressPath = Join-Path $OutputRoot 'evidence_batch_progress.json'
$logPath = Join-Path $OutputRoot 'evidence_batch.log'

foreach ($required in @($Dataset, $Manifest, $collector)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Required path is missing: $required"
    }
}

New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$manifestObject = Get-Content -Raw -LiteralPath $Manifest | ConvertFrom-Json
$caseIds = @($manifestObject.decisions | ForEach-Object { $_.case_id })
$records = [System.Collections.Generic.List[object]]::new()

function Write-ProgressFile {
    $completed = @($records | Where-Object { $_.status -eq 'COMPLETE' }).Count
    $failed = @($records | Where-Object { $_.status -notin @('COMPLETE', 'SKIPPED_EXISTING') }).Count
    [ordered]@{
        schema_version = 'rq3-phase6-evidence-progress-v1'
        updated_at = [DateTimeOffset]::UtcNow.ToString('o')
        total_cases = $caseIds.Count
        processed_records = $records.Count
        completed = $completed
        failed_or_incomplete = $failed
        records = $records
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $progressPath -Encoding utf8
}

foreach ($caseId in $caseIds) {
    $bundle = Join-Path $evidenceRoot "$caseId\evidence_bundle.json"
    $caseDirectory = Join-Path $evidenceRoot $caseId
    if (Test-Path -LiteralPath $bundle) {
        $records.Add([ordered]@{ case_id = $caseId; status = 'SKIPPED_EXISTING'; exit_code = 0 })
        Write-ProgressFile
        continue
    }
    if (Test-Path -LiteralPath $caseDirectory) {
        $records.Add([ordered]@{ case_id = $caseId; status = 'INCOMPLETE_EXISTING_WORKSPACE'; exit_code = $null })
        Write-ProgressFile
        continue
    }

    $started = [DateTimeOffset]::UtcNow
    Add-Content -LiteralPath $logPath -Value "$($started.ToString('o')) START $caseId"
    & dotnet $collector `
        --dataset $Dataset `
        --manifest $Manifest `
        --out $evidenceRoot `
        --tool-evidence-out $toolEvidenceRoot `
        --docker-image $DockerImage `
        --case $caseId *>> $logPath
    $exitCode = $LASTEXITCODE
    $status = if ($exitCode -eq 0 -and (Test-Path -LiteralPath $bundle)) { 'COMPLETE' } else { 'FAILED' }
    $records.Add([ordered]@{
        case_id = $caseId
        status = $status
        exit_code = $exitCode
        started_at = $started.ToString('o')
        finished_at = [DateTimeOffset]::UtcNow.ToString('o')
    })
    Add-Content -LiteralPath $logPath -Value "$([DateTimeOffset]::UtcNow.ToString('o')) END $caseId $status exit=$exitCode"
    Write-ProgressFile
}

Write-ProgressFile

