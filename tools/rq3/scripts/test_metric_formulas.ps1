$ErrorActionPreference='Stop'
function McNemar([int]$b,[int]$c){
    $n=$b+$c;if($n-eq0){return 1.0}
    $tail=[math]::Min($b,$c);$prob=[math]::Pow(.5,$n);$sum=$prob
    for($k=1;$k-le$tail;$k++){$prob*=($n-$k+1)/[double]$k;$sum+=$prob}
    [math]::Round([math]::Min(1.0,2.0*$sum),8)
}
function Rate([int]$n,[int]$d){if($d-eq0){0.0}else{$n/[double]$d}}
function F1([int]$tp,[int]$fp,[int]$fn){$p=Rate $tp ($tp+$fp);$r=Rate $tp ($tp+$fn);if(($p+$r)-eq0){0.0}else{2*$p*$r/($p+$r)}}
function Near([double]$expected,[double]$actual,[double]$epsilon,[string]$name){if([math]::Abs($expected-$actual)-gt$epsilon){throw "$name expected $expected, got $actual"}}

Near 0.06356812 (McNemar 5 14) 0.00000001 'exact McNemar'
Near 1.0 (McNemar 0 0) 0.0 'McNemar no discordance'
Near 0.8 (F1 8 2 2) 0.00000001 'false-pass F1'
Near 0.25 (Rate 2 8) 0.0 'false-acceptance rate'
Write-Output 'RQ3 metric formula tests passed.'
