param(
    [Parameter(Mandatory = $true)][ValidateRange(0, 15)][int]$ShardIndex,
    [ValidateRange(1, 16)][int]$ShardCount = 4,
    [string]$Dataset = 'R:\BaoVer2\ThucNgiem\RQ3_Phase2',
    [string]$Manifest = 'R:\BaoVer2\ThucNgiem\RQ3_Phase3\registry\phase6_main_amended_manifest_v1.json',
    [string]$OutputRoot = 'R:\BaoVer2\ThucNgiem\RQ3_Phase3\phase6_main_v1',
    [string]$DockerImage = 'rq3-defects4j:3.0.1'
)

$ErrorActionPreference = 'Stop'
if ($ShardIndex -ge $ShardCount) { throw 'ShardIndex must be smaller than ShardCount.' }

$collector = 'R:\BaoVer2\ThucNgiem\RQ3_Phase3\tools\RQ3EvidenceCollector\bin\Release\net8.0\RQ3EvidenceCollector.dll'
$evidenceRoot = Join-Path $OutputRoot 'evidence'
$toolEvidenceRoot = Join-Path $OutputRoot 'tool_evidence'
$progressPath = Join-Path $OutputRoot "evidence_shard_${ShardIndex}_of_${ShardCount}.json"
$logPath = Join-Path $OutputRoot "evidence_shard_${ShardIndex}_of_${ShardCount}.log"

$manifestObject = Get-Content -Raw -LiteralPath $Manifest | ConvertFrom-Json
$allIds = @($manifestObject.decisions | ForEach-Object { $_.case_id })
$caseIds = @()
for ($index = $allIds.Count - 1; $index -ge 0; $index--) {
    if (($index % $ShardCount) -eq $ShardIndex) { $caseIds += $allIds[$index] }
}
$records = [System.Collections.Generic.List[object]]::new()

function Save-Progress {
    [ordered]@{
        schema_version = 'rq3-phase6-evidence-shard-progress-v1'
        shard_index = $ShardIndex
        shard_count = $ShardCount
        assigned_cases = $caseIds.Count
        updated_at = [DateTimeOffset]::UtcNow.ToString('o')
        records = $records
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $progressPath -Encoding utf8
}

foreach ($caseId in $caseIds) {
    $bundle = Join-Path $evidenceRoot "$caseId\evidence_bundle.json"
    $caseDirectory = Join-Path $evidenceRoot $caseId
    if (Test-Path -LiteralPath $bundle) {
        $records.Add([ordered]@{ case_id = $caseId; status = 'SKIPPED_EXISTING'; exit_code = 0 })
        Save-Progress
        continue
    }
    if (Test-Path -LiteralPath $caseDirectory) {
        $records.Add([ordered]@{ case_id = $caseId; status = 'SKIPPED_IN_PROGRESS_OR_INCOMPLETE'; exit_code = $null })
        Save-Progress
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
    Save-Progress
}

Save-Progress

