# On the HDR monitor: what Windows actually shows for each clip and mode (Desktop Duplication in FP16 scRGB).
param([string]$Window = '200,120,1600,960', [string]$Opts = '', [string]$Tag = '')
$r = $PSScriptRoot
$bin = Split-Path (Get-ChildItem "$r\app\bin" -Recurse -Filter HdrSpike.exe | Select-Object -First 1).FullName
$out = Join-Path $r 'results'
if (Get-Process Wow -ErrorAction SilentlyContinue) { throw "WoW is running" }
foreach ($clip in 'sdr', 'hdr') { foreach ($mode in 'sdr', 'pq') {
    $n = "screen-$clip-$mode$Tag"
    $log = Join-Path $out "$n.log"
    foreach ($f in $log, "$log.mpv.log") { if (Test-Path $f) { [IO.File]::Delete($f) } }
    $vars = @{ SPIKE_LOG = $log; HAKU_TEST_WINDOW = $Window; SPIKE_FILE = "$r\clips\$clip-test.mkv"; SPIKE_MODE = $mode; SPIKE_START = '5'; SPIKE_CAPTURE = (Join-Path $out "$n.raw"); SPIKE_OPTS = $Opts }
    foreach ($k in $vars.Keys) { [Environment]::SetEnvironmentVariable($k, $vars[$k]) }
    $p = Start-Process (Join-Path $bin 'HdrSpike.exe') -PassThru
    foreach ($k in $vars.Keys) { [Environment]::SetEnvironmentVariable($k, $null) }
    if (-not $p.WaitForExit(30000)) { Stop-Process -Id $p.Id -Force }
    Get-Content $log | Select-String 'captured|CAPTURE|RENDER|UNHANDLED|option' | ForEach-Object { "$n  " + $_.ToString().Trim() }
} }
