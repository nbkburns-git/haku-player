# Runs the WinUI go/no-go checks. Windows open on the second monitor without taking focus; only this script's own
# processes (tracked by PID) are ever stopped. Refuses to run while WoW is open (GPU tests).
param([string[]]$Only = @('startup', 'dumps', 'resize', 'automation', 'upscale'), [int]$StartupRuns = 5)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$exe = Get-ChildItem "$root\app\bin" -Recurse -Filter HdrSpike.exe | Where-Object FullName -match 'Release' | Select-Object -First 1 -ExpandProperty FullName
$wpf = Get-ChildItem "$root\wpfbase\bin" -Recurse -Filter WpfBase.exe -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
$out = "$root\results"; New-Item -ItemType Directory -Force $out | Out-Null
$clips = "$root\clips"
if (Get-Process Wow -ErrorAction SilentlyContinue) { throw "WoW is running: GPU tests wait until it's closed" }
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

function Run([string]$name, [hashtable]$envs, [int]$timeoutSec = 30, [scriptblock]$during = $null, [string]$app = $exe) {
    $log = "$out\$name.log"; Remove-Item "$log*" -ErrorAction SilentlyContinue
    $all = @{ SPIKE_LOG = $log; HAKU_TEST_WINDOW = '3940,60,2360,1330' } + $envs
    foreach ($k in $all.Keys) { Set-Item "Env:$k" $all[$k] }
    try { $p = Start-Process $app -PassThru } finally { foreach ($k in $all.Keys) { Remove-Item "Env:$k" -ErrorAction SilentlyContinue } }
    if ($during) { & $during $p }
    if (-not $p.WaitForExit($timeoutSec * 1000)) { Write-Host "  $name timed out; stopping PID $($p.Id)"; Stop-Process -Id $p.Id -Force }
    Start-Sleep -Milliseconds 300
    if (Test-Path $log) { Get-Content $log } else { "  (no log)" }
}

if ($Only -contains 'startup') {
    "== startup (WinUI, SDR clip, $StartupRuns runs) =="
    for ($i = 1; $i -le $StartupRuns; $i++) { Run "startup-winui-$i" @{ SPIKE_FILE = "$clips\sdr-test.mkv"; SPIKE_QUIT = '3' } | Select-String 'first XAML frame|first frame presented|window shown' }
    if ($wpf) {
        "== startup (bare WPF window, $StartupRuns runs) =="
        for ($i = 1; $i -le $StartupRuns; $i++) { Run "startup-wpf-$i" @{} 15 $null $wpf | Select-String 'first frame' }
    }
}

if ($Only -contains 'dumps') {
    "== frame dumps =="
    foreach ($clip in 'sdr', 'hdr') { foreach ($mode in 'sdr', 'pq', 'fp16') { foreach ($glass in '0', '1') {
        $n = "dump-$clip-$mode-glass$glass"
        Run $n @{ SPIKE_FILE = "$clips\$clip-test.mkv"; SPIKE_MODE = $mode; SPIKE_GLASS = $glass; SPIKE_START = '5'; SPIKE_DUMP = "$out\$n.raw" } 40 |
            Select-String 'display:|swap chain|dumped|FAILED|RENDER|UNHANDLED|option'
    } } }
}

if ($Only -contains 'resize') {
    "== resize sweep =="
    foreach ($mode in 'sdr', 'pq') { Run "resize-$mode" @{ SPIKE_FILE = "$clips\hdr-test.mkv"; SPIKE_MODE = $mode; SPIKE_RESIZE = '1' } 60 | Select-String 'resize lag|FAILED|RENDER|UNHANDLED' }
}

if ($Only -contains 'automation') {
    "== UI automation =="
    $UIA = [System.Windows.Automation.AutomationElement]
    Run 'automation' @{ SPIKE_FILE = "$clips\sdr-test.mkv"; SPIKE_QUIT = '8' } 20 {
        param($p)
        $btn = $null
        for ($i = 0; $i -lt 40 -and -not $btn; $i++) {
            Start-Sleep -Milliseconds 200
            $w = $UIA::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition($UIA::ProcessIdProperty, $p.Id)))
            if ($w) { $btn = $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($UIA::NameProperty, 'Probe'))) }
        }
        if ($btn) { $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Write-Host "  found and invoked 'Probe'" } else { Write-Host "  'Probe' NOT found" }
    } | Select-String 'probe button|window shown'
}

if ($Only -contains 'upscale') {
    "== AI upscaler (vf_hakusr) and RTX Video (d3d11vpp) =="
    $up = 'C:\Users\thoma\AppData\Local\Haku Upscaler'
    $lib = 'A:\Anime Player\lib\haku_sr.dll'
    $eng = "$up\engines\2x_AnimeJaNai_HD_V3Sharp1_UltraCompact_fp16.max2560x1728.engine"
    function Q([string]$s) { "%$([Text.Encoding]::UTF8.GetByteCount($s))%$s" }
    $env:PATH = "$up;$env:PATH"
    try {
        Run 'upscale-hakusr-pq' @{ SPIKE_FILE = "$clips\sdr-test.mkv"; SPIKE_MODE = 'pq'; SPIKE_HWDEC = 'd3d11va'; SPIKE_QUIT = '12'; SPIKE_VF = "@hakusr:hakusr=lib=$(Q $lib):engine=$(Q $eng):frames=24:vram=1600" } 30
        Run 'upscale-vpp-pq' @{ SPIKE_FILE = "$clips\sdr-test.mkv"; SPIKE_MODE = 'pq'; SPIKE_HWDEC = 'd3d11va'; SPIKE_QUIT = '8'; SPIKE_VF = 'd3d11vpp=scale=2:scaling-mode=nvidia:format=nv12' } 25
    } finally { $env:PATH = ($env:PATH -split ';' | Where-Object { $_ -ne $up }) -join ';' }
    foreach ($n in 'upscale-hakusr-pq', 'upscale-vpp-pq') {
        "-- $n (mpv log)"
        if (Test-Path "$out\$n.log.mpv.log") { Get-Content "$out\$n.log.mpv.log" | Select-String 'hakusr|d3d11vpp|vf:|Error|error|failed' | Select-Object -First 15 }
    }
}
