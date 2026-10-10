param(
    [Parameter(Mandatory=$true)][string]$CandidateRoot,
    [string]$ExploratoryRoot='R:\BaoVer2\ThucNgiem\RQ3_Phase3_Consensus\phase6_exploratory_v2_dataset'
)
$ErrorActionPreference='Stop'
$manifestPath=Join-Path $CandidateRoot 'registry\confirmatory_holdout_v1_manifest.json'
if(-not(Test-Path $manifestPath)){throw "Confirmatory manifest missing: $manifestPath"}
$manifest=Get-Content -Raw $manifestPath|ConvertFrom-Json
$cases=@($manifest.cases);if($cases.Count-ne72){throw "Holdout must contain exactly 72 cases; found $($cases.Count)"}
$expected=@('NONE_VALID','LOCAL_COMPILATION_FAILURE','LOCAL_RUNTIME_OR_JUNIT_FAILURE','PLAN_TEST_MISMATCH','STRUCTURAL_TARGET_NOT_EXECUTED','EXPECTED_OBSERVATION_OR_ASSERTION_MISMATCH','WEAK_ORACLE_RELEVANT_MUTANT_SURVIVES','SHARED_INFRASTRUCTURE_FAILURE','INSUFFICIENT_CONTEXT')
foreach($category in $expected){$count=@($cases|Where-Object gold_category -eq $category).Count;if($count-ne8){throw "Category $category must contain 8 cases; found $count"}}
$exploratory=Get-Content -Raw (Join-Path $ExploratoryRoot 'provenance\source_provenance_private.json')|ConvertFrom-Json
$oldIds=@{};$oldHashes=@{};for($i=0;$i-lt$exploratory.Count;$i++){$oldIds[[string]$exploratory[$i].case_id]=$true;$oldHashes[[string]$exploratory[$i].source_sha256]=$true}
foreach($case in $cases){if($oldIds.ContainsKey([string]$case.case_id)){throw "Holdout case ID overlaps exploratory cohort: $($case.case_id)"};if($oldHashes.ContainsKey([string]$case.source_sha256)){throw "Holdout source hash overlaps exploratory cohort: $($case.case_id)"};$env=Join-Path $CandidateRoot "envelopes\$($case.case_id).json";if(-not(Test-Path $env)){throw "Missing holdout envelope: $env"};$raw=Get-Content -Raw $env;if($raw-match'(?i)evidence_layer_operator|intended_gold_category|goldDecision|WEAKEN_ORACLE'){throw "Holdout envelope leaks private metadata: $($case.case_id)"}}
[ordered]@{passed=$true;cases=72;categories=9;cases_per_category=8;overlap_with_exploratory=0}|ConvertTo-Json
