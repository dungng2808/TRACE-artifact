param(
    [Parameter(Mandatory=$true)][string]$PredictionsJson,
    [string]$DatasetRoot='R:\BaoVer2\ThucNgiem\RQ3_Phase3_Consensus\phase6_exploratory_v2_dataset',
    [string]$OutputRoot='R:\BaoVer2\ThucNgiem\RQ3_Phase3_Consensus\phase6_exploratory_v2_results',
    [int]$BootstrapIterations=5000
)
$ErrorActionPreference='Stop'
function Rate([int]$n,[int]$d){if($d -eq 0){0.0}else{[math]::Round($n/[double]$d,6)}}
function Wilson([int]$n,[int]$d){if($d-eq0){return [ordered]@{lower=0.0;upper=0.0}};$z=1.95996398454005;$p=$n/[double]$d;$den=1+$z*$z/$d;$center=($p+$z*$z/(2*$d))/$den;$half=$z*[math]::Sqrt($p*(1-$p)/$d+$z*$z/(4*$d*$d))/$den;[ordered]@{lower=[math]::Round($center-$half,6);upper=[math]::Round($center+$half,6)}}
function F1([int]$tp,[int]$fp,[int]$fn){$p=Rate $tp ($tp+$fp);$r=Rate $tp ($tp+$fn);if(($p+$r)-eq 0){0.0}else{[math]::Round(2*$p*$r/($p+$r),6)}}
function MacroF1($items,[string]$truth,[string]$prediction){$labels=@($items|Select-Object -ExpandProperty $truth -Unique);if($labels.Count-eq 0){return 0.0};$scores=@(foreach($l in $labels){$tp=@($items|Where-Object{$_.$truth-eq$l-and$_.$prediction-eq$l}).Count;$fp=@($items|Where-Object{$_.$truth-ne$l-and$_.$prediction-eq$l}).Count;$fn=@($items|Where-Object{$_.$truth-eq$l-and$_.$prediction-ne$l}).Count;F1 $tp $fp $fn});[math]::Round(($scores|Measure-Object -Average).Average,6)}
function Percentile([double[]]$v,[double]$p){$s=@($v|Sort-Object);if($s.Count-eq 0){return 0};$pos=($s.Count-1)*$p;$lo=[math]::Floor($pos);$hi=[math]::Ceiling($pos);if($lo-eq$hi){return [double]$s[$lo]};[double]$s[$lo]+($pos-$lo)*([double]$s[$hi]-[double]$s[$lo])}
function McNemar([int]$b,[int]$c){$n=$b+$c;if($n-eq 0){return 1.0};$tail=[math]::Min($b,$c);$prob=[math]::Pow(.5,$n);$sum=$prob;for($k=1;$k-le$tail;$k++){$prob*=($n-$k+1)/[double]$k;$sum+=$prob};[math]::Round([math]::Min(1.0,2.0*$sum),8)}

$manifest=Get-Content -Raw (Join-Path $DatasetRoot 'registry\phase6_exploratory_v2_manifest.json')|ConvertFrom-Json
$ids=@($manifest.decisions|Where-Object evaluation_status -eq 'INCLUDED'|Select-Object -ExpandProperty case_id)
$predictionItems=Get-Content -Raw $PredictionsJson|ConvertFrom-Json;$predictions=@(for($predictionIndex=0;$predictionIndex-lt$predictionItems.Count;$predictionIndex++){$predictionItems[$predictionIndex]})
if($predictions.Count-ne($ids.Count*3)){throw "Expected $($ids.Count*3) predictions, found $($predictions.Count)"}
$falseCauses=@('PLAN_TEST_MISMATCH','TARGET_NOT_REACHED','ASSERTION_ERROR','WEAK_ORACLE')
$rows=@(foreach($p in $predictions){
    $g=Get-Content -Raw (Join-Path $DatasetRoot "gold\private\$($p.CaseId).json")|ConvertFrom-Json
    $c=Get-Content -Raw (Join-Path $DatasetRoot "cases\public\$($p.CaseId).json")|ConvertFrom-Json
    $predictedFalse=($p.Decision-eq'REPAIR_REQUIRED'-and$p.Cause-in$falseCauses)
    [pscustomobject]@{case_id=$p.CaseId;project=$c.repository.project;verifier=$p.Verifier;decision=$p.Decision;cause=$p.Cause;route=$p.Route;abstained=[bool]$p.Abstained;execution_status=$p.ExecutionStatus;input_tokens=[int]$p.InputTokens;output_tokens=[int]$p.OutputTokens;api_calls=[int]$p.ApiCalls;cost_usd=[double]$p.CostUsd;envelope_hash=$p.EvidenceEnvelopeHash;gold_decision=$g.goldDecision;gold_cause=$g.primaryCause;acceptable_routes=(@($g.acceptableRoutes)-join '|');gold_false_passing=[bool]$g.falsePassing;predicted_false_passing=$predictedFalse;decision_correct=($p.Decision-eq$g.goldDecision);cause_correct=($p.Cause-eq$g.primaryCause);route_correct=($p.Route-in@($g.acceptableRoutes))}
})
$rowIndex=@{};foreach($row in $rows){$rowIndex["$($row.verifier)|$($row.case_id)"]=$row}

$metrics=@(foreach($group in ($rows|Group-Object verifier)){
    $r=@($group.Group);$tp=@($r|Where-Object{$_.gold_false_passing-and$_.predicted_false_passing}).Count;$fp=@($r|Where-Object{-not$_.gold_false_passing-and$_.predicted_false_passing}).Count;$fn=@($r|Where-Object{$_.gold_false_passing-and-not$_.predicted_false_passing}).Count;$goldFp=@($r|Where-Object gold_false_passing);$covered=@($r|Where-Object decision -ne 'ABSTAIN');$defects=@($r|Where-Object gold_decision -eq 'REPAIR_REQUIRED');
    $correct=@($r|Where-Object decision_correct).Count;$macro=MacroF1 $defects 'gold_cause' 'cause'
    [pscustomobject][ordered]@{verifier=$group.Name;cases=$r.Count;false_pass_precision=Rate $tp ($tp+$fp);false_pass_recall=Rate $tp ($tp+$fn);false_pass_f1=F1 $tp $fp $fn;false_acceptance_rate=Rate (@($goldFp|Where-Object decision -eq 'VERIFIED').Count) $goldFp.Count;decision_accuracy=Rate $correct $r.Count;decision_accuracy_wilson_95=Wilson $correct $r.Count;attribution_macro_f1=$macro;macro_attribution_f1=$macro;attribution_macro_f1_all_labels=MacroF1 $r 'gold_cause' 'cause';routing_accuracy=Rate (@($defects|Where-Object route_correct).Count) $defects.Count;abstention_rate=Rate (@($r|Where-Object decision -eq 'ABSTAIN').Count) $r.Count;decision_coverage=Rate $covered.Count $r.Count;selective_accuracy=Rate (@($covered|Where-Object decision_correct).Count) $covered.Count;api_or_output_error_count=@($r|Where-Object execution_status -ne 'OK').Count;input_tokens=($r|Measure-Object input_tokens -Sum).Sum;output_tokens=($r|Measure-Object output_tokens -Sum).Sum;api_calls=($r|Measure-Object api_calls -Sum).Sum;cost_usd=[math]::Round(($r|Measure-Object cost_usd -Sum).Sum,8)}
})

$b=0;$c=0;foreach($id in $ids){$r1=$rowIndex["B1_GENERAL_REVIEWER|$id"];$r2=$rowIndex["B2_SPECIALIZED_MULTI_AGENT|$id"];if($r2.decision_correct-and-not$r1.decision_correct){$b++};if($r1.decision_correct-and-not$r2.decision_correct){$c++}}
$random=[Random]::new(20260826);$deltas=[Collections.Generic.List[double]]::new();for($i=0;$i-lt$BootstrapIterations;$i++){$sample=@(for($j=0;$j-lt$ids.Count;$j++){$ids[$random.Next(0,$ids.Count)]});$vals=@{};foreach($v in @('B1_GENERAL_REVIEWER','B2_SPECIALIZED_MULTI_AGENT')){$s=@(foreach($id in $sample){$rowIndex["$v|$id"]});$tp=@($s|Where-Object{$_.gold_false_passing-and$_.predicted_false_passing}).Count;$fp=@($s|Where-Object{-not$_.gold_false_passing-and$_.predicted_false_passing}).Count;$fn=@($s|Where-Object{$_.gold_false_passing-and-not$_.predicted_false_passing}).Count;$vals[$v]=F1 $tp $fp $fn};$deltas.Add($vals['B2_SPECIALIZED_MULTI_AGENT']-$vals['B1_GENERAL_REVIEWER'])}

New-Item -ItemType Directory -Force $OutputRoot|Out-Null
$rows|Export-Csv (Join-Path $OutputRoot 'predictions_joined_private_gold_v2.csv') -NoTypeInformation -Encoding utf8
$pair=[ordered]@{comparison='B2_vs_B1_GENERAL_REVIEWER';b2_correct_baseline_wrong=$b;baseline_correct_b2_wrong=$c;discordant_pairs=$b+$c;exact_mcnemar_p=McNemar $b $c;holm_adjusted_p=$null}
$result=[ordered]@{schema_version='rq3-exploratory-v2-metrics';generated_at=[DateTimeOffset]::UtcNow.ToString('o');cohort_status=$manifest.status;predictions_sha256=(Get-FileHash -Algorithm SHA256 $PredictionsJson).Hash;manifest_sha256=(Get-FileHash -Algorithm SHA256 (Join-Path $DatasetRoot 'registry\phase6_exploratory_v2_manifest.json')).Hash;evaluation_cases=$ids.Count;prediction_rows=$rows.Count;metrics=$metrics;paired_tests=@($pair);b2_vs_b1=[ordered]@{decision_mcnemar=[ordered]@{b2_correct_b1_wrong=$b;b1_correct_b2_wrong=$c;p_value=McNemar $b $c};false_pass_f1_delta_bootstrap_95=[ordered]@{lower=[math]::Round((Percentile $deltas.ToArray() .025),6);upper=[math]::Round((Percentile $deltas.ToArray() .975),6)}};interpretation_rule='Claim B2 superiority only when the prespecified effect favors B2 and its uncertainty test supports the claim.'}
$result|ConvertTo-Json -Depth 12|Set-Content (Join-Path $OutputRoot 'metrics_v2.json') -Encoding utf8
$metrics|Format-Table verifier,false_pass_precision,false_pass_recall,false_pass_f1,false_acceptance_rate,attribution_macro_f1,routing_accuracy,abstention_rate,selective_accuracy -AutoSize
