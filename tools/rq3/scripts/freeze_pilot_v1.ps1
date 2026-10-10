param(
    [string]$DatasetRoot = 'R:\BaoVer2\ThucNgiem\RQ3_Phase2',
    [string]$Phase3Root = 'R:\BaoVer2\ThucNgiem\RQ3_Phase3',
    [string]$CorpusManifest = 'R:\BaoVer2\ThucNgiem\RQ3_Phase2\registry\pilot_corpus_manifest_v1.json',
    [string]$EvidenceRoot = 'R:\BaoVer2\ThucNgiem\RQ3_Phase3\pilot_v1\tool_evidence',
    [string]$OutputPath = 'R:\BaoVer2\ThucNgiem\RQ3_Phase2\registry\pilot_frozen_manifest_v1.json'
)

$ErrorActionPreference = 'Stop'
$corpus = Get-Content -Raw -LiteralPath $CorpusManifest | ConvertFrom-Json
if ($corpus.case_count -ne 36 -or @($corpus.decisions | Where-Object evaluation_status -ne 'INCLUDED').Count -ne 0) {
    throw 'Pilot corpus manifest does not contain exactly 36 INCLUDED cases.'
}

$evidenceIndex = foreach ($decision in $corpus.decisions) {
    $id = $decision.case_id
    $bundlePath = Join-Path $EvidenceRoot "$id.bundle.json"
    $projectionPath = Join-Path $EvidenceRoot "$id.json"
    foreach ($path in @($bundlePath, $projectionPath)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing evidence artifact $path." }
    }
    $bundle = Get-Content -Raw -LiteralPath $bundlePath | ConvertFrom-Json
    $projection = Get-Content -Raw -LiteralPath $projectionPath | ConvertFrom-Json
    if ($bundle.schema_version -ne 'rq3-evidence-bundle-v1.1' -or $bundle.case_id -ne $id -or $projection.case_id -ne $id) {
        throw "Evidence identity/schema mismatch for $id."
    }
    $publicPath = Join-Path $DatasetRoot "cases\public\$id.json"
    $publicHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $publicPath).Hash
    if ($bundle.case_sha256 -ne $publicHash) { throw "Public case hash mismatch for $id." }
    $bundleHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $bundlePath).Hash
    if ($projection.bundle_sha256 -ne $bundleHash) { throw "Projection-to-bundle hash mismatch for $id." }
    [ordered]@{
        case_id = $id
        bundle_sha256 = $bundleHash
        projection_sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $projectionPath).Hash
        compile = @($bundle.tool_runs | Where-Object phase -eq 'compile')[0].status
        junit = @($bundle.tool_runs | Where-Object phase -eq 'junit')[0].status
        coverage_status = $bundle.coverage.evidence_status
        mutation_status = $bundle.mutation.evidence_status
    }
}

$lockedFiles = @(
    'PHASE4_V1_1_CONFIG.json',
    'schemas\rq3-evidence-bundle-v1.1.schema.json',
    'tools\RQ3EvidenceCollector\Program.cs',
    'tools\RQ3BatchRunner\Program.cs',
    'RQ3_Evaluator_Wpf\B0ToolGate.cs',
    'RQ3_Evaluator_Wpf\LlmEvaluator.cs',
    'RQ3_Evaluator_Wpf\DatasetService.cs',
    'RQ3_Evaluator_Wpf\Models.cs',
    'RQ3_Evaluator_Wpf\ExcelExporter.cs'
)
$lockedHashes = foreach ($relative in $lockedFiles) {
    $path = Join-Path $Phase3Root $relative
    [ordered]@{ path=$relative.Replace('\','/'); sha256=(Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash }
}
$config = Get-Content -Raw -LiteralPath (Join-Path $Phase3Root 'PHASE4_V1_1_CONFIG.json') | ConvertFrom-Json
if ($config.status -ne 'FROZEN') { throw 'PHASE4_V1_1_CONFIG.json must be marked FROZEN before the pilot freeze.' }

$output = [ordered]@{
    schema_version = 'rq3-pilot-full-freeze-manifest-v1'
    status = 'FROZEN_FOR_B0_B1_B2'
    frozen_at = [DateTimeOffset]::UtcNow.ToString('o')
    case_count = 36
    corpus_manifest = $CorpusManifest
    corpus_manifest_sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $CorpusManifest).Hash
    evidence_root = $EvidenceRoot
    evidence_count = $evidenceIndex.Count
    decisions = @($corpus.decisions)
    evidence_index = @($evidenceIndex)
    locked_implementation_prompt_config = @($lockedHashes)
    non_leakage = [ordered]@{
        verifier_input_sources = @('cases/public', 'pilot_v1/tool_evidence')
        private_gold_join_stage = 'POST_PREDICTION_SCORER_ONLY'
    }
}
$output | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
$OutputPath
