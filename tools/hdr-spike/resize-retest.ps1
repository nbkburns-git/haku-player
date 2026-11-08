# Resize sweep: render-thread resize vs drawing inside the layout pass (SPIKE_SYNCRESIZE=1).
$r = $PSScriptRoot
$bin = Split-Path (Get-ChildItem "$r\app\bin" -Recurse -Filter HdrSpike.exe | Select-Object -First 1).FullName
$out = Join-Path $r 'results'
foreach ($sync in '0', '1') { foreach ($run in 1, 2) {
    $log = Join-Path $out "resize-pq-sync$sync-$run.log"
    foreach ($f in $log, "$log.mpv.log") { if (Test-Path $f) { [IO.File]::Delete($f) } }
    $vars = @{ SPIKE_LOG = $log; HAKU_TEST_WINDOW = '3940,60,2360,1330'; SPIKE_FILE = "$r\clips\hdr-test.mkv"; SPIKE_MODE = 'pq'; SPIKE_RESIZE = '1'; SPIKE_SYNCRESIZE = $sync }
    foreach ($k in $vars.Keys) { [Environment]::SetEnvironmentVariable($k, $vars[$k]) }
    $p = Start-Process (Join-Path $bin 'HdrSpike.exe') -PassThru
    foreach ($k in $vars.Keys) { [Environment]::SetEnvironmentVariable($k, $null) }
    if (-not $p.WaitForExit(60000)) { Stop-Process -Id $p.Id -Force }
    Get-Content $log | Select-String 'resize frames|resize lag|SYNC|RENDER|UNHANDLED' | ForEach-Object { "sync=$sync run $run " + $_.ToString().Trim() }
} }
