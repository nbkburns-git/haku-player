# Launches a test copy of Haku on the test monitor and reports every change of the foreground window for a few seconds:
# a test copy must never become the foreground window (you may be in a game). Only the process it starts is stopped.
#   powershell -ExecutionPolicy Bypass -File tests\focus-watch.ps1 -Exe <HakuPlayer.exe> [-File <video>] [-Seconds 8]
param([Parameter(Mandatory)][string]$Exe, [string]$File = '', [double]$Seconds = 8, [string]$Window = $env:HAKU_TEST_WINDOW)
$ErrorActionPreference = 'Stop'
if (-not $Window) { . (Join-Path $PSScriptRoot 'media.local.ps1'); $Window = $TestWindow }
. (Join-Path $PSScriptRoot 'safe-to-launch.ps1'); if ($why = Test-SafeToLaunch) { "SKIP  not launching: $why"; exit 2 }
Add-Type -Namespace FocusWatch -Name Win -MemberDefinition '[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow(); [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid); [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);'
$data = Join-Path $env:TEMP "haku-focus-watch"; if (Test-Path $data) { Remove-Item $data -Recurse -Force }; New-Item -ItemType Directory $data | Out-Null
@{ SettingsVersion = 3; Upscale = $false; Resume = $false } | ConvertTo-Json | Set-Content (Join-Path $data "settings.json") -Encoding utf8
$env:HAKU_DATA_DIR = $data; $env:HAKU_TEST_WINDOW = $Window
$sw = [Diagnostics.Stopwatch]::StartNew()
try { $p = if ($File) { Start-Process $Exe -ArgumentList "`"$File`"" -PassThru } else { Start-Process $Exe -PassThru } }
finally { Remove-Item Env:HAKU_DATA_DIR, Env:HAKU_TEST_WINDOW }
$last = ''; $took = $null
try {
    while ($sw.Elapsed.TotalSeconds -lt $Seconds) {
        $h = [FocusWatch.Win]::GetForegroundWindow(); $fp = 0; [FocusWatch.Win]::GetWindowThreadProcessId($h, [ref]$fp) | Out-Null
        $cls = New-Object Text.StringBuilder 128; [FocusWatch.Win]::GetClassName($h, $cls, 128) | Out-Null
        $now = "$(if ($fp -eq $p.Id) { 'TEST COPY' } else { "pid $fp" }) ($cls)"
        if ($now -ne $last) { "{0,6} ms  foreground: {1}" -f $sw.ElapsedMilliseconds, $now; $last = $now }
        if ($fp -eq $p.Id -and -not $took) { $took = $sw.ElapsedMilliseconds }
        Start-Sleep -Milliseconds 25
    }
} finally { if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force } }
if ($took) { "FAIL  the test copy became the foreground window at $took ms"; exit 1 } else { "PASS  the test copy never became the foreground window" }
