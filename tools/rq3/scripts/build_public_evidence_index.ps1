param(
    [string]$Phase2Root = 'R:\BaoVer2\ThucNgiem\RQ3_Phase2',
    [string]$Phase3Root = 'R:\BaoVer2\ThucNgiem\RQ3_Phase3'
)

$ErrorActionPreference = 'Stop'
$rows = Get-ChildItem -LiteralPath (Join-Path $Phase2Root 'cases\public') -Filter '*.json' -File | ForEach-Object {
    $case = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
    [ordered]@{
        case_id = $case.case_id
        split = $case.split
        repository_id = $case.repository.id
        test_id = $case.base_test.test_id
        test_source_path = (Join-Path $Phase2Root $case.base_test.artifact_path)
        public_case_path = $_.FullName
        focal_candidate = $case.focal_mapping.candidate_class
        scenario_summary = $case.scenario.summary
        tool_evidence_status = 'PENDING_PER_TEST_EVIDENCE'
    }
}
$rows | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Phase3Root 'registry\public_evidence_index.json') -Encoding utf8
Write-Output "Built a public-only index for $($rows.Count) cases."
