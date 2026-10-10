param(
    [Parameter(Mandatory = $true)][string]$MainPredictionsJson,
    [string]$WorkspaceRoot = 'R:\BaoVer2',
    [string]$Manifest = 'R:\BaoVer2\ThucNgiem\RQ3_Phase3\registry\phase6_main_amended_manifest_v1.json',
    [string]$EvidenceRoot = 'R:\BaoVer2\ThucNgiem\RQ3_Phase3\phase6_main_v1\tool_evidence',
    [string]$OutputRoot = 'R:\BaoVer2\ThucNgiem\RQ3_Phase3\phase6_stability_v1',
    [string]$Model = 'google/gemini-2.5-flash',
    [string]$Endpoint = 'https://openrouter.ai/api/v1/chat/completions',
    [string]$ApiKeyFile = ''
)

$ErrorActionPreference = 'Stop'
$runner = Join-Path $WorkspaceRoot 'ThucNgiem\RQ3_Phase3\tools\RQ3BatchRunner\bin\Release\net8.0\RQ3BatchRunner.dll'
$dataset = Join-Path $WorkspaceRoot 'ThucNgiem\RQ3_Phase2'
$keyFile = if (-not [string]::IsNullOrWhiteSpace($ApiKeyFile) -and (Test-Path -LiteralPath $ApiKeyFile)) { $ApiKeyFile } else { Join-Path $WorkspaceRoot 'OpenRouterKey.txt' }
$manifestObject = Get-Content -Raw -LiteralPath $Manifest | ConvertFrom-Json
$main = @((Get-Content -Raw -LiteralPath $MainPredictionsJson | ConvertFrom-Json))

$groups = @($manifestObject.cases | Group-Object intended_gold_category | Sort-Object Name)
$selected = [System.Collections.Generic.List[string]]::new()
for ($round = 0; $selected.Count -lt 15; $round++) {
    foreach ($group in $groups) {
        $items = @($group.Group | Sort-Object case_id)
        if ($round -lt $items.Count -and $selected.Count -lt 15) { $selected.Add($items[$round].case_id) }
    }
}

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
$records = [System.Collections.Generic.List[object]]::new()
foreach ($repetition in 1..3) {
    $repetitionRoot = Join-Path $OutputRoot "rep$repetition"
    $existingByCase = @{}
    foreach ($directory in @(Get-ChildItem -LiteralPath $repetitionRoot -Directory -ErrorAction SilentlyContinue | Sort-Object Name)) {
        $candidatePath = Join-Path $directory.FullName 'predictions.json'
        if (Test-Path -LiteralPath $candidatePath) {
            $candidatePredictions = @((Get-Content -Raw -LiteralPath $candidatePath | ConvertFrom-Json))
            $candidateCaseIds = @($candidatePredictions.CaseId | Sort-Object -Unique)
            $candidateVerifiers = @($candidatePredictions.Verifier | Sort-Object -Unique)
            if ($candidateCaseIds.Count -eq 1 -and
                $candidateVerifiers -contains 'B1_GENERAL_REVIEWER' -and
                $candidateVerifiers -contains 'B2_SPECIALIZED_MULTI_AGENT' -and
                -not $existingByCase.ContainsKey($candidateCaseIds[0])) {
                $existingByCase[$candidateCaseIds[0]] = [pscustomobject]@{ directory = $directory; predictions = $candidatePredictions }
            }
        }
    }
    foreach ($caseId in $selected) {
        if ($existingByCase.ContainsKey($caseId)) {
            $newDirectory = $existingByCase[$caseId].directory
            $repeatPredictions = $existingByCase[$caseId].predictions
        } else {
            $before = @(Get-ChildItem -LiteralPath $repetitionRoot -Directory -ErrorAction SilentlyContinue).FullName
            & dotnet $runner `
                "--dataset=$dataset" `
                "--out=$repetitionRoot" `
                "--manifest=$Manifest" `
                "--evidence-root=$EvidenceRoot" `
                "--endpoint=$Endpoint" `
                "--model=$Model" `
                "--api-key-file=$keyFile" `
                "--case=$caseId" `
                '--b1' `
                '--b2'
            if ($LASTEXITCODE -ne 0) { throw "Stability runner failed for repetition $repetition, case $caseId." }
            $newDirectory = Get-ChildItem -LiteralPath $repetitionRoot -Directory | Where-Object FullName -notin $before | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
            $repeatPredictions = @((Get-Content -Raw -LiteralPath (Join-Path $newDirectory.FullName 'predictions.json') | ConvertFrom-Json))
        }
        foreach ($verifier in @('B1_GENERAL_REVIEWER', 'B2_SPECIALIZED_MULTI_AGENT')) {
            $reference = $main | Where-Object { $_.CaseId -eq $caseId -and $_.Verifier -eq $verifier }
            $repeat = $repeatPredictions | Where-Object { $_.CaseId -eq $caseId -and $_.Verifier -eq $verifier }
            $records.Add([pscustomobject][ordered]@{
                repetition = $repetition
                case_id = $caseId
                verifier = $verifier
                reference_decision = $reference.Decision
                repeated_decision = $repeat.Decision
                reference_cause = $reference.Cause
                repeated_cause = $repeat.Cause
                reference_route = $reference.Route
                repeated_route = $repeat.Route
                tuple_agreement = ($reference.Decision -eq $repeat.Decision -and $reference.Cause -eq $repeat.Cause -and $reference.Route -eq $repeat.Route)
                execution_status = $repeat.ExecutionStatus
                input_tokens = [int]$repeat.InputTokens
                output_tokens = [int]$repeat.OutputTokens
                api_calls = [int]$repeat.ApiCalls
                cost_usd = [double]$repeat.CostUsd
                run_directory = $newDirectory.FullName
            })
        }
    }
}

$result = [ordered]@{
    schema_version = 'rq3-phase6-stability-v1'
    generated_at = [DateTimeOffset]::UtcNow.ToString('o')
    main_predictions_sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $MainPredictionsJson).Hash
    selected_case_count = $selected.Count
    selected_cases = $selected
    repetitions = 3
    comparison_tuples = $records.Count
    tuple_agreement_count = @($records | Where-Object tuple_agreement).Count
    tuple_agreement_rate = [math]::Round(@($records | Where-Object tuple_agreement).Count / [double]$records.Count, 6)
    api_or_output_errors = @($records | Where-Object execution_status -ne 'OK').Count
    input_tokens = ($records | Measure-Object input_tokens -Sum).Sum
    output_tokens = ($records | Measure-Object output_tokens -Sum).Sum
    api_calls = ($records | Measure-Object api_calls -Sum).Sum
    cost_usd = [math]::Round((($records | Measure-Object cost_usd -Sum).Sum), 8)
    records = $records
}
$resultPath = Join-Path $OutputRoot 'phase6_stability_v1.json'
$result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $resultPath -Encoding utf8
[ordered]@{ stability_sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $resultPath).Hash } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputRoot 'phase6_stability_hash_v1.json') -Encoding utf8
