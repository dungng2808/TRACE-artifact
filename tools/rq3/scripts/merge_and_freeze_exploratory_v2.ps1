param(
    [Parameter(Mandatory=$true)][string]$MainPredictions,
    [string[]]$RepairPredictions=@(),
    [Parameter(Mandatory=$true)][string]$DatasetRoot,
    [Parameter(Mandatory=$true)][string]$OutputDirectory
)
$ErrorActionPreference='Stop'

function Text-Sha([string]$Text){
    $sha=[Security.Cryptography.SHA256]::Create()
    try{[BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Text))).Replace('-','')}
    finally{$sha.Dispose()}
}

$rows=Get-Content -Raw -LiteralPath $MainPredictions|ConvertFrom-Json
if($rows.Count-ne219){throw "Expected 219 main predictions, found $($rows.Count)"}
$byKey=@{}
foreach($row in $rows){
    $key="$($row.CaseId)|$($row.Verifier)"
    if($byKey.ContainsKey($key)){throw "Duplicate main prediction: $key"}
    $byKey[$key]=$row
}

$replacements=New-Object 'System.Collections.Generic.List[object]'
foreach($repairPath in $RepairPredictions){
    if(-not(Test-Path -LiteralPath $repairPath)){throw "Repair predictions not found: $repairPath"}
    $repairRows=Get-Content -Raw -LiteralPath $repairPath|ConvertFrom-Json
    foreach($candidate in $repairRows){
        $key="$($candidate.CaseId)|$($candidate.Verifier)"
        if(-not$byKey.ContainsKey($key)){continue}
        $current=$byKey[$key]
        if($current.ExecutionStatus-ne'OK'-and$candidate.ExecutionStatus-eq'OK'){
            $byKey[$key]=$candidate
            $replacements.Add([ordered]@{key=$key;source=$repairPath})
        }
    }
}

$merged=@($rows|ForEach-Object {$byKey["$($_.CaseId)|$($_.Verifier)"]})
if($merged.Count-ne219){throw 'Merged prediction count changed.'}
if(@($merged|Group-Object CaseId,Verifier|Where-Object {$_.Count-ne1}).Count-ne0){throw 'Merged predictions are not unique.'}

foreach($group in $merged|Group-Object CaseId){
    if($group.Count-ne3){throw "$($group.Name) does not have exactly three verifier outputs."}
    $envelopePath=Join-Path $DatasetRoot "envelopes\$($group.Name).json"
    # JsonDocument.RootElement.GetRawText() excludes whitespace outside the root token.
    $expectedHash=Text-Sha ([IO.File]::ReadAllText($envelopePath).Trim())
    $hashes=@($group.Group|Select-Object -ExpandProperty EvidenceEnvelopeHash -Unique)
    if($hashes.Count-ne1-or$hashes[0]-ne$expectedHash){
        throw "$($group.Name) envelope hash mismatch: predictions=$($hashes-join',') dataset=$expectedHash"
    }
}

New-Item -ItemType Directory -Force -Path $OutputDirectory|Out-Null
$mergedPath=Join-Path $OutputDirectory 'predictions_merged_v2.json'
$merged|ConvertTo-Json -Depth 30|Set-Content -LiteralPath $mergedPath -Encoding utf8
$invalid=@($merged|Where-Object {$_.ExecutionStatus-ne'OK'}|ForEach-Object {[ordered]@{case_id=$_.CaseId;verifier=$_.Verifier;status=$_.ExecutionStatus}})
$uniqueCaseCount=($merged|Select-Object -ExpandProperty CaseId -Unique).Count
$replacementArray=$replacements.ToArray()
$freeze=[ordered]@{
    schema_version='rq3-prediction-freeze-v2'
    frozen_at=[DateTimeOffset]::UtcNow.ToString('o')
    predictions_file=$mergedPath
    predictions_sha256=(Get-FileHash -Algorithm SHA256 -LiteralPath $mergedPath).Hash
    prediction_count=$merged.Count
    unique_case_count=$uniqueCaseCount
    private_gold_joined=$false
    replacements=$replacementArray
    invalid_outputs=$invalid
    valid_for_complete_case_analysis=($invalid.Count-eq0)
}
$freezePath=Join-Path $OutputDirectory 'prediction_freeze_v2.json'
$freeze|ConvertTo-Json -Depth 20|Set-Content -LiteralPath $freezePath -Encoding utf8
$freeze|ConvertTo-Json -Depth 20
