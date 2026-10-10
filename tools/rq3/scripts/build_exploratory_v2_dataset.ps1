param(
    [string]$WorkspaceRoot = 'R:\BaoVer2',
    [string]$OutputRoot = 'R:\BaoVer2\ThucNgiem\RQ3_Phase3_Consensus\phase6_exploratory_v2_dataset'
)

$ErrorActionPreference = 'Stop'
$phase2 = Join-Path $WorkspaceRoot 'ThucNgiem\RQ3_Phase2'
$phase3 = Join-Path $WorkspaceRoot 'ThucNgiem\RQ3_Phase3'
$manifestPath = Join-Path $phase3 'registry\phase6_main_amended_manifest_v1.json'
$oldEvidenceRoot = Join-Path $phase3 'phase6_main_v1\tool_evidence'
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json

function Ensure-Directory([string]$Path) { New-Item -ItemType Directory -Force -Path $Path | Out-Null }
function Write-Json([string]$Path,$Value,[int]$Depth=30) { $Value | ConvertTo-Json -Depth $Depth | Set-Content -LiteralPath $Path -Encoding utf8 }
function Sha([string]$Path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash }
function Copy-Object($Value) { $Value | ConvertTo-Json -Depth 40 | ConvertFrom-Json }

$truth = @{
    NONE_VALID = @('VERIFIED','NONE',@('NONE'),$false)
    LOCAL_COMPILATION_FAILURE = @('REPAIR_REQUIRED','COMPILATION_ERROR',@('LOCAL_TEST_REPAIR'),$false)
    LOCAL_RUNTIME_OR_JUNIT_FAILURE = @('REPAIR_REQUIRED','SETUP_OR_RUNTIME_ERROR',@('LOCAL_TEST_REPAIR'),$false)
    PLAN_TEST_MISMATCH = @('REPAIR_REQUIRED','PLAN_TEST_MISMATCH',@('REGENERATE_TEST'),$true)
    STRUCTURAL_TARGET_NOT_EXECUTED = @('REPAIR_REQUIRED','TARGET_NOT_REACHED',@('REGENERATE_TEST'),$true)
    EXPECTED_OBSERVATION_OR_ASSERTION_MISMATCH = @('REPAIR_REQUIRED','ASSERTION_ERROR',@('LOCAL_TEST_REPAIR'),$true)
    WEAK_ORACLE_RELEVANT_MUTANT_SURVIVES = @('REPAIR_REQUIRED','WEAK_ORACLE',@('LOCAL_TEST_REPAIR'),$true)
    SHARED_INFRASTRUCTURE_FAILURE = @('REPAIR_REQUIRED','SHARED_INFRASTRUCTURE_ERROR',@('SHARED_INFRASTRUCTURE_REPAIR'),$false)
    INSUFFICIENT_CONTEXT = @('ABSTAIN','INSUFFICIENT_EVIDENCE',@('REQUEST_TOOL_EVIDENCE'),$false)
}

foreach($dir in @('cases\public','gold\private','tool_evidence','envelopes','registry','provenance')) { Ensure-Directory (Join-Path $OutputRoot $dir) }
$entries = [System.Collections.Generic.List[object]]::new()

foreach($caseMeta in $manifest.cases) {
    $id = [string]$caseMeta.case_id
    $category = [string]$caseMeta.intended_gold_category
    $publicPath = Join-Path $phase2 "cases\public\$id.json"
    $bundlePath = Join-Path $oldEvidenceRoot "$id.bundle.json"
    $annotationV2Path = Join-Path $phase2 "artifacts\llm_annotations_v2\$id.json"
    $annotationV1Path = Join-Path $phase2 "artifacts\llm_annotations\$id.json"
    foreach($required in @($publicPath,$bundlePath,$annotationV2Path)) { if(-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Missing required artifact: $required" } }

    $public = Get-Content -Raw -LiteralPath $publicPath | ConvertFrom-Json
    $bundle = Get-Content -Raw -LiteralPath $bundlePath | ConvertFrom-Json
    $ann2 = Get-Content -Raw -LiteralPath $annotationV2Path | ConvertFrom-Json
    $ann1 = if(Test-Path -LiteralPath $annotationV1Path){Get-Content -Raw -LiteralPath $annotationV1Path|ConvertFrom-Json}else{$null}
    if($ann2.consensus -ne 'ACCEPTED') { throw "Annotation v2 is not accepted for $id" }

    $methodA=[string]$ann2.pass_a.FocalMethodId; $methodB=[string]$ann2.pass_b.FocalMethodId
    $candidateMethod = if(-not [string]::IsNullOrWhiteSpace($methodA) -and $methodA -ne 'CLASS_LEVEL' -and ($methodA -eq $methodB -or $methodB -eq 'CLASS_LEVEL')){$methodA}elseif(-not [string]::IsNullOrWhiteSpace($methodB) -and $methodB -ne 'CLASS_LEVEL' -and $methodA -eq 'CLASS_LEVEL'){$methodB}else{'CLASS_LEVEL'}
    $scenarioSummary = if($null -ne $ann1 -and -not [string]::IsNullOrWhiteSpace([string]$ann1.pass_a.Scenario)){[string]$ann1.pass_a.Scenario}else{[string]$ann2.pass_a.Rationale}

    $sourcePath = [string]$bundle.materialization.variant_path
    $mutationAdjudication=$null
    if($category -eq 'WEAK_ORACLE_RELEVANT_MUTANT_SURVIVES') {
        $project=[string]$public.repository.project
        $sourcePath=Join-Path $phase2 "artifacts\source_injection_validation\d4j_v3\variants\$project-WEAKEN_ORACLE-variant.java"
    }
    if(-not [IO.Path]::IsPathRooted($sourcePath)){$sourcePath=Join-Path $phase2 $sourcePath}
    if(-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)){throw "Materialized source missing for ${id}: $sourcePath"}
    $sourceText=[IO.File]::ReadAllText($sourcePath)
    $sourceHash=Sha $sourcePath

    $canonical = Copy-Object $public
    $canonical.schema_version='rq3-public-case-v2'
    $canonical.focal_mapping.candidate_class=[string]$ann2.pass_a.FocalClassId
    $canonical.focal_mapping.candidate_method=$candidateMethod
    $canonical.focal_mapping.status='CONFIRMED'
    if($canonical.focal_mapping.PSObject.Properties['declared_target_note']){$canonical.focal_mapping.PSObject.Properties.Remove('declared_target_note')}
    if($canonical.focal_mapping.PSObject.Properties['declared_target_confidence']){$canonical.focal_mapping.PSObject.Properties.Remove('declared_target_confidence')}
    $canonical.scenario.summary=$scenarioSummary
    $canonical.scenario.input_preconditions="Use the setup, arguments, fixtures, and state shown in generated_test.source_text for $($public.base_test.test_id)."
    $canonical.scenario.expected_observation="The explicit assertions, expected exceptions, and state checks described by the declared scenario must be established by the generated test."
    $canonical.scenario.structural_target=[string]$ann2.pass_a.FocalClassId
    $canonical.status='READY_FOR_EXPLORATORY_V2'

    if($category -eq 'PLAN_TEST_MISMATCH') {
        $canonical.scenario.summary="Declared plan: $scenarioSummary The generated test must exercise the declared focal behavior rather than a different input path or behavior."
        $canonical.scenario.expected_observation="The generated test must demonstrate the behavior declared above at the stated focal target; assertions about a different behavior do not satisfy the plan."
    } elseif($category -eq 'EXPECTED_OBSERVATION_OR_ASSERTION_MISMATCH') {
        $canonical.scenario.summary=$scenarioSummary
        $canonical.scenario.expected_observation="The declared scenario requires the complementary valid outcome to the one currently asserted in generated_test.source_text; verify the declared outcome rather than the retained assertion."
    }

    $tool = Copy-Object $bundle
    $tool.schema_version='rq3-tool-evidence-v2'
    foreach($field in @('evidence_layer','evidence_layer_sha256','case_sha256')) { if($tool.PSObject.Properties[$field]){$tool.PSObject.Properties.Remove($field)} }
    if($tool.PSObject.Properties['materialization']){$tool.PSObject.Properties.Remove('materialization')}
    if($tool.mutation.PSObject.Properties['relevant_mutant_survives']){$tool.mutation.PSObject.Properties.Remove('relevant_mutant_survives')}
    $tool.mutation | Add-Member -NotePropertyName relevance_disclosed -NotePropertyValue $false -Force
    $tool.mutation | Add-Member -NotePropertyName surviving_mutants -NotePropertyValue @() -Force

    if($category -eq 'WEAK_ORACLE_RELEVANT_MUTANT_SURVIVES') {
        $inputPath=Join-Path $phase2 "artifacts\weak_oracle_relevance_inputs\$($public.repository.project).json"
        if(-not (Test-Path -LiteralPath $inputPath)){throw "Weak-oracle raw mutant input missing for $id"}
        $input=Get-Content -Raw -LiteralPath $inputPath|ConvertFrom-Json
        if($input.test_id -ne $public.base_test.test_id){throw "Weak-oracle selected-test mismatch for $id"}
        $mutants=@(foreach($sample in $input.live_mutant_samples){
            $match=[regex]::Match([string]$sample,'^(?<id>\d+):(?<operator>[^:]+):.*:(?<location>[^:]+):(?<line>\d+):(?<offset>\d+):(?<change>.*)$')
            if(-not $match.Success){throw "Cannot parse mutant sample for ${id}: $sample"}
            $change=$match.Groups['change'].Value -split ' \|==> ',2
            $location=$match.Groups['location'].Value -split '@',2
            [ordered]@{mutant_id=$match.Groups['id'].Value;operator=$match.Groups['operator'].Value;class=$location[0];method=if($location.Count -gt 1){$location[1]}else{'CLASS_LEVEL'};line=[int]$match.Groups['line'].Value;original=$change[0];replacement=if($change.Count -gt 1){$change[1]}else{''};status='SURVIVED'}
        })
        if($mutants.Count -eq 0){throw "Weak-oracle case $id has no raw surviving mutant"}
        $tool.mutation.surviving_mutants=$mutants
        $tool.mutation.evidence_status='AVAILABLE_RAW_SURVIVORS'
        $tool.mutation | Add-Member -NotePropertyName selected_test_id -NotePropertyValue ([string]$input.test_id) -Force
        $tool.mutation | Add-Member -NotePropertyName raw_report_sha256 -NotePropertyValue (Sha $inputPath) -Force
        $reviewPath=Join-Path $phase2 "artifacts\weak_oracle_relevance_reviews_rerun\$($public.repository.project).json"
        if(-not(Test-Path -LiteralPath $reviewPath)){throw "Private mutation review missing for $id"}
        $review=Get-Content -Raw $reviewPath|ConvertFrom-Json
        if($review.verdict-ne'RELEVANT_SURVIVOR_PRESENT'){throw "Private mutation review did not accept a relevant survivor for $id"}
        $mutationAdjudication=[ordered]@{verdict=[string]$review.verdict;selectedMutant=[string]$review.selected_mutant;reviewArtifactSha256=Sha $reviewPath;disclosedToVerifier=$false}
    }

    $publicEnvelope=Copy-Object $canonical
    if($category -eq 'INSUFFICIENT_CONTEXT') {
        $publicEnvelope | Add-Member -NotePropertyName context_redactions -NotePropertyValue @([ordered]@{field='related_method_context';status='INTENTIONALLY_REDACTED';reason='Selective-verification abstention case'}) -Force
    }
    $generated=[ordered]@{language='java';test_id=[string]$public.base_test.test_id;source_text=$sourceText;source_sha256=$sourceHash;materialized_from="source_artifact/$id.java"}
    $envelope=[ordered]@{schema_version='rq3-evidence-envelope-v2';case_id=$id;public_case=$publicEnvelope;generated_test=$generated;tool_evidence=$tool;note='No private gold, injection operator, or relevance adjudication is included.'}
    $goldTruth=$truth[$category]
    $gold=[ordered]@{schema_version='rq3-private-gold-v2';case_id=$id;goldCategory=$category;goldDecision=$goldTruth[0];primaryCause=$goldTruth[1];acceptableRoutes=@($goldTruth[2]);falsePassing=[bool]$goldTruth[3]}
    if($null-ne$mutationAdjudication){$gold['mutationAdjudication']=$mutationAdjudication}

    Write-Json (Join-Path $OutputRoot "cases\public\$id.json") $canonical
    Write-Json (Join-Path $OutputRoot "tool_evidence\$id.bundle.json") $tool
    Write-Json (Join-Path $OutputRoot "envelopes\$id.json") $envelope
    Write-Json (Join-Path $OutputRoot "gold\private\$id.json") $gold
    $entries.Add([ordered]@{case_id=$id;category=$category;source_path=$sourcePath;source_sha256=$sourceHash;envelope_sha256=Sha (Join-Path $OutputRoot "envelopes\$id.json")})
}

$v2Manifest=[ordered]@{schema_version='rq3-exploratory-v2-manifest';status='FROZEN_EXPLORATORY_V2';created_at=[DateTimeOffset]::UtcNow.ToString('o');case_count=$entries.Count;source_manifest=$manifestPath;decisions=@($manifest.decisions);cases=@($entries)}
Write-Json (Join-Path $OutputRoot 'registry\phase6_exploratory_v2_manifest.json') $v2Manifest
Write-Json (Join-Path $OutputRoot 'provenance\source_provenance_private.json') @($entries)
Write-Output "Built exploratory v2 dataset: $OutputRoot ($($entries.Count) cases)"
