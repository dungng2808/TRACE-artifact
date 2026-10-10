param(
    [string]$DatasetRoot='R:\BaoVer2\ThucNgiem\RQ3_Phase3_Consensus\phase6_exploratory_v2_dataset',
    [string[]]$CaseIds=@('RQ3-M-MATH-06','RQ3-M-COMPRESS-01','RQ3-M-CSV-06')
)
$ErrorActionPreference='Stop'
function Text-Sha([string]$Text){$sha=[Security.Cryptography.SHA256]::Create();try{[BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Text))).Replace('-','')}finally{$sha.Dispose()}}
foreach($id in $CaseIds){
    $path=Join-Path $DatasetRoot "envelopes\$id.json";$env=Get-Content -Raw $path|ConvertFrom-Json;$source=[string]$env.generated_test.source_text;$methodName=([string]$env.generated_test.test_id -split '::')[-1]
    if($env.generated_test.source_scope-eq'SELECTED_TEST_METHOD_WITH_IMPORT_CONTEXT'){
        Write-Output "$id already compacted: $($source.Length) chars"
        continue
    }
    $pattern='(?m)^[ \t]*(?:@[\w.()"'', =]+\r?\n[ \t]*)*(?:(?:public|protected|private|static|final|synchronized)\s+)*(?:void|[\w<>\[\].?]+)\s+'+[regex]::Escape($methodName)+'\s*\('
    $match=[regex]::Match($source,$pattern);if(-not$match.Success){throw "Cannot locate selected test method $methodName in $id"}
    $open=$source.IndexOf('{',$match.Index+$match.Length);if($open-lt0){throw "Cannot locate opening brace for $id"};$depth=0;$end=-1
    for($i=$open;$i-lt$source.Length;$i++){if($source[$i]-eq'{'){$depth++}elseif($source[$i]-eq'}'){$depth--;if($depth-eq0){$end=$i+1;break}}}
    if($end-lt0){throw "Cannot locate closing brace for $id"}
    $context=([regex]::Matches($source,'(?m)^(?:package|import)\s+.*;\r?$')|ForEach-Object Value)-join"`n"
    $selected=($context+"`n`n"+$source.Substring($match.Index,$end-$match.Index)).Trim()
    $fileHash=[string]$env.generated_test.source_sha256
    $env.generated_test.source_text=$selected;$env.generated_test.source_sha256=Text-Sha $selected
    $env.generated_test|Add-Member -NotePropertyName source_scope -NotePropertyValue 'SELECTED_TEST_METHOD_WITH_IMPORT_CONTEXT' -Force
    $env.generated_test|Add-Member -NotePropertyName source_file_sha256 -NotePropertyValue $fileHash -Force
    $env|ConvertTo-Json -Depth 40|Set-Content $path -Encoding utf8
    Write-Output "$id compacted: $($source.Length) -> $($selected.Length) chars"
}

$provenancePath=Join-Path $DatasetRoot 'provenance\source_provenance_private.json'
if(Test-Path -LiteralPath $provenancePath){
    $provenance=Get-Content -Raw -LiteralPath $provenancePath|ConvertFrom-Json
    foreach($id in $CaseIds){
        $entry=@($provenance)|Where-Object {$_.case_id -eq $id}|Select-Object -First 1
        if($null-eq$entry){throw "Missing provenance entry for $id"}
        $entry.envelope_sha256=(Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $DatasetRoot "envelopes\$id.json")).Hash
    }
    $provenance|ConvertTo-Json -Depth 20|Set-Content -LiteralPath $provenancePath -Encoding utf8
}
