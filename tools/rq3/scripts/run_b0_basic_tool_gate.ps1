param(
    [string]$Phase3Root = 'R:\BaoVer2\ThucNgiem\RQ3_Phase3'
)

$ErrorActionPreference = 'Stop'
$index = Get-Content -LiteralPath (Join-Path $Phase3Root 'registry\public_evidence_index.json') -Raw | ConvertFrom-Json
$out = foreach ($case in $index) {
    # B0 is deliberately conservative: without per-test tool evidence it cannot accept.
    [ordered]@{
        case_id = $case.case_id
        verifier = 'B0_BASIC_TOOL_GATE'
        predicted_decision = 'ABSTAIN'
        predicted_cause = 'INSUFFICIENT_EVIDENCE'
        predicted_route = 'REQUEST_TOOL_EVIDENCE'
        abstained = $true
        input_tokens = 0
        output_tokens = 0
        evidence_status = $case.tool_evidence_status
    }
}
$out | Export-Csv -LiteralPath (Join-Path $Phase3Root 'artifacts\predictions\b0_predictions.csv') -NoTypeInformation -Encoding utf8
Write-Output "Recorded $($out.Count) conservative B0 predictions pending tool evidence."
