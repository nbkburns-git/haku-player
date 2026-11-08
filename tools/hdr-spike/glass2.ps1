# Packed glass shader: does it load and blur (frame dumps), and what does moving it every 33 ms cost.
$r = $PSScriptRoot
$bin = Split-Path (Get-ChildItem "$r\app\bin" -Recurse -Filter HdrSpike.exe | Select-Object -First 1).FullName
$out = Join-Path $r 'results'
$shader = Join-Path $r '..\..\Assets\glass.glsl'
function Run($n, $vars, $timeout = 40) {
    $log = Join-Path $out "$n.log"
    foreach ($f in $log, "$log.mpv.log") { if (Test-Path $f) { [IO.File]::Delete($f) } }
    $vars = $vars + @{ SPIKE_LOG = $log; HAKU_TEST_WINDOW = '3940,60,2360,1330'; SPIKE_GLASSFILE = $shader }
    foreach ($k in $vars.Keys) { [Environment]::SetEnvironmentVariable($k, $vars[$k]) }
    $p = Start-Process (Join-Path $bin 'HdrSpike.exe') -PassThru
    foreach ($k in $vars.Keys) { [Environment]::SetEnvironmentVariable($k, $null) }
    if (-not $p.WaitForExit($timeout * 1000)) { Stop-Process -Id $p.Id -Force }
    $m = Get-Content "$log.mpv.log" -ErrorAction SilentlyContinue
    $errs = $m | Select-String '\]\[e\]\[libmpv_render\]' | Select-Object -First 3
    $reinit = ($m | Select-String -SimpleMatch 'glsl-shader-opts' | Measure-Object).Count
    $drops = ($m | Select-String 'Dropping frame|frame drop' | Measure-Object).Count
    Get-Content $log | Select-String 'render:|glass moved|dumped|RENDER|UNHANDLED' | ForEach-Object { "$n  " + $_.ToString().Trim() }
    "$n  mpv: $drops drop lines, $reinit shader-opts lines" + ($(if ($errs) { "; ERRORS: " + ($errs -join ' | ') } else { "" }))
}
foreach ($mode in 'sdr', 'pq') { foreach ($glass in '0', '1') {
    Run "dump-sdr-$mode-glass$glass" @{ SPIKE_FILE = "$r\clips\sdr-test.mkv"; SPIKE_MODE = $mode; SPIKE_GLASS = $glass; SPIKE_START = '5'; SPIKE_DUMP = (Join-Path $out "dump-sdr-$mode-glass$glass.raw") }
} }
Run 'play-noglass' @{ SPIKE_FILE = "$r\clips\sdr-test.mkv"; SPIKE_QUIT = '12' }
Run 'play-glass-still' @{ SPIKE_FILE = "$r\clips\sdr-test.mkv"; SPIKE_GLASS = '1'; SPIKE_QUIT = '12' }
Run 'play-glass-moving' @{ SPIKE_FILE = "$r\clips\sdr-test.mkv"; SPIKE_GLASS = '1'; SPIKE_GLASSANIM = '1'; SPIKE_QUIT = '12' }
