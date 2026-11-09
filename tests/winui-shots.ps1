# Screenshots of the WinUI app's pages on the test monitor (home, a library, a show page), for looking at by eye.
# Driven through the app's test pipe (never the mouse, never UI Automation's Invoke); own data folder; only its own
# process is stopped.   powershell -ExecutionPolicy Bypass -File tests\winui-shots.ps1 [-Out <folder>]
param([string]$Out = (Join-Path $env:TEMP "haku-winui-shots"))
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'media.local.ps1'); . (Join-Path $PSScriptRoot 'safe-to-launch.ps1')
if ($why = Test-SafeToLaunch) { "SKIP  not launching: $why"; exit 2 }
Add-Type -AssemblyName System.Drawing
Add-Type -Namespace Shots -Name Win -MemberDefinition '[StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Rt, B; } [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r); [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c); [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);'
[Shots.Win]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null
$exe = Get-ChildItem (Join-Path $RepoRoot 'HakuWinUI\bin\x64\Debug') -Recurse -Filter HakuPlayer.exe | Select-Object -First 1 -ExpandProperty FullName
New-Item -ItemType Directory -Force $Out | Out-Null
$data = Join-Path $Out 'data'; if (Test-Path $data) { Remove-Item $data -Recurse -Force }; New-Item -ItemType Directory $data | Out-Null
# your real show details and covers (copied: read-only), so the pages look as they would
$real = '\\localhost\c$\Users\thoma\AppData\Local\Haku Player'
foreach ($d in 'info', 'covers') { if (Test-Path (Join-Path $real $d)) { Copy-Item (Join-Path $real $d) $data -Recurse } }
$s = Get-Content -Raw (Join-Path $real 'settings.json') | ConvertFrom-Json
$s.DiscordStatus = $false
$s | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $data 'settings.json') -Encoding utf8
$env:HAKU_DATA_DIR = $data; $env:HAKU_TEST_WINDOW = $TestWindow
try { $p = Start-Process $exe -PassThru } finally { Remove-Item Env:HAKU_DATA_DIR, Env:HAKU_TEST_WINDOW }
function Tell([string]$command) {
    $c = New-Object IO.Pipes.NamedPipeClientStream(".", "haku-test-$($p.Id)", [IO.Pipes.PipeDirection]::InOut)
    try { $c.Connect(5000); $w = New-Object IO.StreamWriter($c); $w.AutoFlush = $true; $r = New-Object IO.StreamReader($c); $w.WriteLine($command); $r.ReadLine() } finally { $c.Dispose() }
}
function Shot([string]$name) {
    $r = New-Object Shots.Win+R; [Shots.Win]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
    $bmp = New-Object System.Drawing.Bitmap ($r.Rt - $r.L), ($r.B - $r.T)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size); $g.Dispose()
    $small = New-Object System.Drawing.Bitmap 1280, ([int](1280 * $bmp.Height / $bmp.Width))
    $g = [System.Drawing.Graphics]::FromImage($small); $g.InterpolationMode = 'HighQualityBicubic'; $g.DrawImage($bmp, 0, 0, $small.Width, $small.Height); $g.Dispose()
    $small.Save((Join-Path $Out "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png); $small.Dispose(); $bmp.Dispose()
    "saved $name.png"
}
try {
    Start-Sleep -Seconds 4
    Shot 'home'
    $lib = ($s.Libs | Select-Object -First 1).Name
    "press $lib -> $(Tell "press $lib")"; Start-Sleep -Seconds 2; Shot 'library'
    $names = (Tell 'names') -split ' \| '
    $card = $names | Where-Object { $_ -match 'Naruto' } | Select-Object -First 1
    if (-not $card) { $card = $names | Select-Object -Skip 12 -First 1 }
    "press $card -> $(Tell "press $card")"; Start-Sleep -Seconds 3; Shot 'show'
    "press App settings -> $(Tell 'press App settings')"; Start-Sleep -Seconds 1; Shot 'settings-library'
    "press Settings: AI upscaling -> $(Tell 'press Settings: AI upscaling')"; Start-Sleep -Seconds 1; Shot 'settings-upscale'
    "press Settings: Stats -> $(Tell 'press Settings: Stats')"; Start-Sleep -Seconds 2; Shot 'settings-stats'
}
finally { [Shots.Win]::PostMessage($p.MainWindowHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null; if (-not $p.WaitForExit(6000)) { Stop-Process -Id $p.Id -Force } }
