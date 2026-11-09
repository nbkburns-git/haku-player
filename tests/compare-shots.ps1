# Side-by-side screenshots of the WPF app and the WinUI app on the same pages (home, library, show, settings, player),
# to check the WinUI port looks the same. Test monitor only, own data folders (a copy of your covers and details),
# never the mouse; WPF is driven by UI Automation, WinUI by its test pipe (UIA Invoke would activate a WinUI window).
#   powershell -ExecutionPolicy Bypass -File tests\compare-shots.ps1 -Wpf <folder with the WPF HakuPlayer.exe> [-Out <folder>] [-Only home,show]
param([Parameter(Mandatory)][string]$Wpf, [string]$Out = (Join-Path $env:TEMP "haku-compare"), [string[]]$Only = @(), [string[]]$Which = @('wpf', 'winui'), [string]$Show = 'Cowboy Bebop')
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'media.local.ps1'); . (Join-Path $PSScriptRoot 'safe-to-launch.ps1')
Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes
Add-Type -Namespace Cmp -Name Win -MemberDefinition '[StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Rt, B; } [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r); [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c); [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l); [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);'
[Cmp.Win]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null
$UIA = [System.Windows.Automation.AutomationElement]
$apps = [ordered]@{
    wpf   = Join-Path $Wpf 'HakuPlayer.exe'
    winui = if ($env:HAKU_WINUI_EXE) { $env:HAKU_WINUI_EXE } else { Get-ChildItem (Join-Path $RepoRoot 'HakuWinUI\bin\x64\Debug') -Recurse -Filter HakuPlayer.exe | Select-Object -First 1 -ExpandProperty FullName }
}
New-Item -ItemType Directory -Force $Out | Out-Null
$real = '\\localhost\c$\Users\thoma\AppData\Local\Haku Player'
$base = Get-Content -Raw (Join-Path $real 'settings.json') | ConvertFrom-Json
$base.DiscordStatus = $false; $base.Upscale = $false; $base.HideAfter = 5
function Want($n) { $Only.Count -eq 0 -or $Only -contains $n }

function Start-App($kind, [string[]]$argList) {
    $data = Join-Path $Out "data-$kind"; if (Test-Path $data) { Remove-Item $data -Recurse -Force }; New-Item -ItemType Directory $data | Out-Null
    foreach ($d in 'info', 'covers') { if (Test-Path (Join-Path $real $d)) { Copy-Item (Join-Path $real $d) $data -Recurse } }
    if (Test-Path (Join-Path $real 'watched.json')) { Copy-Item (Join-Path $real 'watched.json') $data }
    $base | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $data 'settings.json') -Encoding utf8
    $env:HAKU_DATA_DIR = $data; $env:HAKU_TEST_WINDOW = $TestWindow
    try { if ($argList) { Start-Process $apps[$kind] -ArgumentList ($argList | ForEach-Object { "`"$_`"" }) -PassThru } else { Start-Process $apps[$kind] -PassThru } }
    finally { Remove-Item Env:HAKU_DATA_DIR, Env:HAKU_TEST_WINDOW }
}
function Stop-App($p) { [Cmp.Win]::PostMessage($p.MainWindowHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null; if (-not $p.WaitForExit(8000)) { Stop-Process -Id $p.Id -Force } }
function Window($p) { $UIA::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition($UIA::ProcessIdProperty, $p.Id))) }
function Press($kind, $p, [string]$name) {
    if ($kind -eq 'winui') {
        $c = New-Object IO.Pipes.NamedPipeClientStream(".", "haku-test-$($p.Id)", [IO.Pipes.PipeDirection]::InOut)
        try { $c.Connect(5000); $w = New-Object IO.StreamWriter($c); $w.AutoFlush = $true; $r = New-Object IO.StreamReader($c); $w.WriteLine("press $name"); return $r.ReadLine() } finally { $c.Dispose() }
    }
    $win = Window $p; if (-not $win) { return 'no window' }
    $el = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($UIA::NameProperty, $name)))
    if (-not $el) { return "not found: $name" }
    $pat = $null
    if ($el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pat)) { $pat.Invoke(); return 'ok' }
    if ($el.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pat)) { $pat.Select(); return 'ok' }
    'no invoke'
}
function Shot($p, [string]$name) {
    $r = New-Object Cmp.Win+R; [Cmp.Win]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
    $bmp = New-Object System.Drawing.Bitmap ($r.Rt - $r.L), ($r.B - $r.T)
    # the window's own picture (PW_RENDERFULLCONTENT), so whatever else is on that monitor can't cover it
    $g = [System.Drawing.Graphics]::FromImage($bmp); $dc = $g.GetHdc(); [Cmp.Win]::PrintWindow($p.MainWindowHandle, $dc, 2) | Out-Null; $g.ReleaseHdc($dc); $g.Dispose()
    $bmp.Save((Join-Path $Out "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
}

$lib = ($base.Libs | Select-Object -First 1).Name
foreach ($kind in @($apps.Keys | Where-Object { $Which -contains $_ })) {
    if ($why = Test-SafeToLaunch) { "SKIP  not launching: $why"; exit 2 }
    $p = Start-App $kind
    try {
        Start-Sleep -Seconds 5; Shot $p "home-$kind"
        "$kind press $lib -> $(Press $kind $p $lib)"; Start-Sleep -Seconds 2; Shot $p "library-$kind"
        "$kind press $Show -> $(Press $kind $p $Show)"; Start-Sleep -Seconds 3; Shot $p "show-$kind"
        "$kind settings -> $(Press $kind $p 'App settings')"; Start-Sleep -Seconds 1; Shot $p "settings-$kind"
        "$kind upscale -> $(Press $kind $p 'Settings: AI upscaling')"; Start-Sleep -Seconds 1; Shot $p "settings-ai-$kind"
    }
    finally { Stop-App $p }
    if ((Want 'player') -and $Media.Subs) {
        if ($why = Test-SafeToLaunch) { "SKIP  not launching: $why"; exit 2 }
        $p = Start-App $kind @($Media.Subs)
        try { Start-Sleep -Seconds 4; Shot $p "player-$kind" } finally { Stop-App $p }
    }
}
# pairs, WPF on the left
foreach ($n in 'home', 'library', 'show', 'settings', 'settings-ai', 'player') {
    $a = Join-Path $Out "$n-wpf.png"; $b = Join-Path $Out "$n-winui.png"
    if (-not ((Test-Path $a) -and (Test-Path $b))) { continue }
    $ia = [System.Drawing.Image]::FromFile($a); $ib = [System.Drawing.Image]::FromFile($b)
    $w = 1200; $h = [int]($w * $ia.Height / $ia.Width)
    $pair = New-Object System.Drawing.Bitmap ($w * 2 + 8), $h
    $g = [System.Drawing.Graphics]::FromImage($pair); $g.InterpolationMode = 'HighQualityBicubic'; $g.Clear([System.Drawing.Color]::Magenta)
    $g.DrawImage($ia, 0, 0, $w, $h); $g.DrawImage($ib, $w + 8, 0, $w, [int]($w * $ib.Height / $ib.Width)); $g.Dispose()
    $pair.Save((Join-Path $Out "pair-$n.png"), [System.Drawing.Imaging.ImageFormat]::Png); $pair.Dispose(); $ia.Dispose(); $ib.Dispose()
    "pair-$n.png"
}
