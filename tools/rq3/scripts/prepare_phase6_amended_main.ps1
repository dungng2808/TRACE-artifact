param(
    [string]$Phase2Root = 'R:\BaoVer2\ThucNgiem\RQ3_Phase2',
    [string]$OutputPath = 'R:\BaoVer2\ThucNgiem\RQ3_Phase3\registry\phase6_main_amended_manifest_v1.json'
)

$ErrorActionPreference = 'Stop'

$sourceManifest = Join-Path $Phase2Root 'registry\phase2_frozen_manifest_v1.json'
$source = Get-Content -Raw -LiteralPath $sourceManifest | ConvertFrom-Json
$decisions = @($source.decisions | Where-Object {
    $_.evaluation_status -eq 'INCLUDED' -and $_.case_id -like 'RQ3-M-*'
})

if ($decisions.Count -ne 73) {
    throw "Expected 73 evidence-qualified MAIN cases, found $($decisions.Count)."
}

$cases = foreach ($decision in $decisions) {
    $casePath = Join-Path $Phase2Root "cases\public\$($decision.case_id).json"
    $goldPath = Join-Path $Phase2Root "gold\private\$($decision.case_id).json"
    $case = Get-Content -Raw -LiteralPath $casePath | ConvertFrom-Json
    $gold = Get-Content -Raw -LiteralPath $goldPath | ConvertFrom-Json
    [ordered]@{
        case_id = $decision.case_id
        project = $case.repository.project
        case_origin = $case.case_origin
        intended_gold_category = $gold.intended_gold_category
        public_case_sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $casePath).Hash
        private_gold_sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $goldPath).Hash
    }
}

$manifest = [ordered]@{
    schema_version = 'rq3-phase6-amended-main-manifest-v1'
    status = 'FROZEN_TECHNICAL_MAIN_COHORT_AI_ANNOTATED'
    created_at = [DateTimeOffset]::UtcNow.ToString('o')
    source_manifest = $sourceManifest
    source_manifest_sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $sourceManifest).Hash
    case_count = $decisions.Count
    project_count = @($cases.project | Sort-Object -Unique).Count
    protocol_amendment = 'The preregistered 144-case balanced human-annotated main design was not attainable from the frozen evidence-qualified corpus. This cohort contains only MAIN cases already INCLUDED by the Phase-2 frozen gate. Existing annotation is AI-assisted and must not be represented as human annotation.'
    limitations = @(
        'Reduced from 144 planned cases to 73 evidence-qualified cases.',
        'Fourteen rather than sixteen main repositories are represented.',
        'Category and repository counts are unbalanced.',
        'Only eight cases are natural; sixty-five are controlled injections.',
        'The earlier prototype run touched these cases, so this is not a pristine confirmatory holdout.'
    )
    decisions = $decisions
    cases = $cases
}

$parent = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Path $parent -Force | Out-Null
$manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $OutputPath -Encoding utf8
Write-Output $OutputPath

