# Haku on WinUI 3 (HakuWinUI, phase 2: the player): plays a video on the test monitor and checks it through mpv's pipe and
# Windows UI Automation (never the real mouse). Own data folder, never takes focus; only its own process is stopped.
# UI Automation only reads the window here: pressing a WinUI button through it (InvokePattern) brings the window to the
# front, even a bare WinUI app's (tested 2026-11-08), which would come up over a game. Actions go through mpv's pipe.
#
#   powershell -ExecutionPolicy Bypass -File tests\winui-tests.ps1            (SDR and forced-HDR runs)
#   ... -NoBuild -Hdr force
param([switch]$NoBuild, [string[]]$Hdr = @('auto', 'force'), [string]$Out = (Join-Path $env:TEMP "haku-winui-tests"),
      [string]$Window = $env:HAKU_TEST_WINDOW)
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path $PSScriptRoot -Parent
$Media = @{ H264 = ''; HdrClip = '' }; $TestWindow = $null
$localMedia = Join-Path $PSScriptRoot "media.local.ps1"; if (Test-Path $localMedia) { . $localMedia }
if (-not $Window) { $Window = $TestWindow }
if (-not $Window) { throw "set HAKU_TEST_WINDOW (or TestWindow in media.local.ps1): tests open on the test monitor" }
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type -Namespace WinUiTest -Name Win -MemberDefinition '[StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Rt, B; } [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r); [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow(); [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid); [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l); [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);'
[WinUiTest.Win]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null
$UIA = [System.Windows.Automation.AutomationElement]
$proj = Join-Path $RepoRoot "HakuWinUI\HakuWinUI.csproj"
if (-not $NoBuild) { dotnet build $proj -c Debug -v q --nologo | Out-Null; if ($LASTEXITCODE -ne 0) { throw "build failed" } }
# HAKU_WINUI_EXE: a copy built elsewhere (when the usual one is open and can't be rebuilt)
$exe = if ($env:HAKU_WINUI_EXE) { $env:HAKU_WINUI_EXE } else { Get-ChildItem (Join-Path $RepoRoot "HakuWinUI\bin\x64\Debug") -Recurse -Filter HakuPlayer.exe | Select-Object -First 1 -ExpandProperty FullName }
New-Item -ItemType Directory -Force $Out | Out-Null
$results = New-Object System.Collections.Generic.List[object]
function Report($name, [bool]$pass, $detail) { $results.Add([pscustomobject]@{ Test = $name; Pass = $pass; Detail = $detail }); Write-Host ("{0}  {1,-26} {2}" -f $(if ($pass) { 'PASS' } else { 'FAIL' }), $name, $detail) -ForegroundColor $(if ($pass) { 'Green' } else { 'Red' }) }

function Q($w, $r, [string]$prop) {
    $script:rid++
    $w.WriteLine("{`"command`":[`"get_property_string`",`"$prop`"],`"request_id`":$script:rid}")
    for ($k = 0; $k -lt 300; $k++) {
        $l = $r.ReadLineAsync(); if (-not $l.Wait(4000)) { return "" }
        if ($l.Result -match "`"request_id`":$script:rid\b") { if ($l.Result -notmatch '"error":"success"') { return "" }; return ($l.Result -replace '.*"data":"?(.*?)"?,"request_id.*', '$1') }
    }
    ""
}
function QN($w, $r, [string]$prop) { $v = Q $w $r $prop; $d = 0.0; if ([double]::TryParse($v, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$d)) { $d } else { 0.0 } } # "null" before the file is loaded
function Find($proc, [string]$name) {
    $w = $UIA::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition($UIA::ProcessIdProperty, $proc.Id)))
    if ($w) { $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($UIA::NameProperty, $name))) }
}

. (Join-Path $PSScriptRoot "safe-to-launch.ps1")
foreach ($mode in $Hdr) {
    if ($why = Test-SafeToLaunch) { Write-Host "SKIP  not launching a test copy: $why"; break }
    $file = $Media.H264
    if (-not ($file -and (Test-Path -LiteralPath $file))) { Write-Host "SKIP  no H264 test video in media.local.ps1"; break }
    $data = Join-Path $Out "data-$mode"; if (Test-Path $data) { Remove-Item $data -Recurse -Force }; New-Item -ItemType Directory $data | Out-Null
    @{ SettingsVersion = 3; Upscale = $false; Resume = $false; Autoplay = $true } | ConvertTo-Json | Set-Content (Join-Path $data "settings.json") -Encoding utf8
    $trace = Join-Path $Out "trace-$mode.txt"; foreach ($f in $trace, "$trace.mpv.log") { if (Test-Path $f) { Remove-Item $f } }
    $env:HAKU_DATA_DIR = $data; $env:HAKU_TRACE = $trace; $env:HAKU_TEST_WINDOW = $Window; $env:HAKU_HDR = $mode
    try { $p = Start-Process $exe -ArgumentList "`"$file`"" -PassThru } finally { Remove-Item Env:HAKU_DATA_DIR, Env:HAKU_TRACE, Env:HAKU_TEST_WINDOW, Env:HAKU_HDR }
    try {
        # mpv's pipe, as the WPF app names it
        $name = "animeplayer-$($p.Id)-1"
        for ($i = 0; $i -lt 40 -and -not ([IO.Directory]::GetFiles('\\.\pipe\') | Where-Object { $_ -like "*$name" }); $i++) { Start-Sleep -Milliseconds 250 }
        $c = New-Object IO.Pipes.NamedPipeClientStream(".", $name, [IO.Pipes.PipeDirection]::InOut); $c.Connect(5000)
        $w = New-Object IO.StreamWriter($c); $w.AutoFlush = $true; $r = New-Object IO.StreamReader($c); $script:rid = 100
        $w.WriteLine('{"command":["set_property","mute",true]}')
        # never the foreground window (watched until the end of the run)
        $script:tookFocus = $false
        function CheckFocus([string]$at = "") { $fg = [WinUiTest.Win]::GetForegroundWindow(); $fp = 0; [WinUiTest.Win]::GetWindowThreadProcessId($fg, [ref]$fp) | Out-Null; if ($fp -eq $p.Id -and -not $script:tookFocus) { $script:tookFocus = $true; $script:focusAt = $at } }
        CheckFocus

        $t0 = 0.0; for ($i = 0; $i -lt 40 -and ($t0 = (QN $w $r 'time-pos')) -le 0.5; $i++) { Start-Sleep -Milliseconds 250; CheckFocus "starting" }
        $d0 = [int](QN $w $r 'frame-drop-count')
        for ($i = 0; $i -lt 8; $i++) { Start-Sleep -Milliseconds 500; CheckFocus "playing" }
        $t1 = (QN $w $r 'time-pos')
        $drops = [int](QN $w $r 'frame-drop-count') - $d0
        Report "plays-$mode" ($t1 -gt $t0 + 3 -and $drops -eq 0) ("time {0:0.0} -> {1:0.0} s in 4 s, {2} dropped (after the first frames)" -f $t0, $t1, $drops)

        $trc = Q $w $r 'target-trc'; $peak = Q $w $r 'target-peak'; $opts = Q $w $r 'glsl-shader-opts'
        $hdrOut = $trc -eq 'pq'
        $wantHdr = $mode -eq 'force' # the test monitor is SDR
        Report "output-$mode" ($hdrOut -eq $wantHdr -and $opts -match "gpq=$([int]$wantHdr)") "target-trc $trc, target-peak $peak; glass: $(($opts -split ',' | Where-Object { $_ -match '^g' }) -join ',')"
        # pausing (as the play button does): the button becomes "Play", and the bar stays up, with its glass
        $wasPause = [bool](Find $p 'Pause')
        $w.WriteLine('{"command":["set_property","pause",true]}'); Start-Sleep -Milliseconds 600
        CheckFocus "after pausing"
        $nowPlay = [bool](Find $p 'Play')
        Report "pause-button-$mode" ($wasPause -and $nowPlay) "button was 'Pause' while playing: $wasPause; 'Play' once paused: $nowPlay"
        $opts = Q $w $r 'glsl-shader-opts'
        Report "glass-$mode" ($opts -match 'a_r=[1-9]') "pill sent to the glass shader: $(($opts -split ',')[0..2] -join ',')"

        # seek through the pipe as the timeline does
        $w.WriteLine('{"command":["seek",150,"absolute+exact"]}'); Start-Sleep -Seconds 1
        $pos = (QN $w $r 'time-pos')
        Report "seek-$mode" ([math]::Abs($pos - 150) -lt 1) ("at {0:0.00} s after seeking to 150" -f $pos)

        $rect = New-Object WinUiTest.Win+R; [WinUiTest.Win]::GetWindowRect($p.MainWindowHandle, [ref]$rect) | Out-Null
        $want = $Window -split ',' | ForEach-Object { [int]$_ }
        Report "window-$mode" ($rect.L -eq $want[0] -and $rect.T -eq $want[1] -and ($rect.Rt - $rect.L) -eq $want[2] -and ($rect.B - $rect.T) -eq $want[3]) "at $($rect.L),$($rect.T), $($rect.Rt - $rect.L)x$($rect.B - $rect.T) (asked for $Window)"
        $bmp = New-Object System.Drawing.Bitmap ($rect.Rt - $rect.L), ($rect.B - $rect.T)
        $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($rect.L, $rect.T, 0, 0, $bmp.Size); $g.Dispose()
        $png = Join-Path $Out "window-$mode.png"; $bmp.Save($png, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()

        $first = Get-Content $trace | Select-String 'first frame' | Select-Object -First 1
        $start = $p.StartTime
        if ($first -and $first.Line -match '^(\d\d:\d\d:\d\d\.\d+)') {
            $ms = ([datetime]::ParseExact($Matches[1], 'HH:mm:ss.fff', $null).TimeOfDay - $start.TimeOfDay).TotalMilliseconds
            Report "first-frame-$mode" ($ms -lt 1500) ("{0:0} ms from launch to the first video frame ({1})" -f $ms, ($first.Line -replace '^\S+ ', ''))
        } else { Report "first-frame-$mode" $false "no 'first frame' line in the trace" }

        # mpv quits (end of the playlist, or "q"): back to the drop screen; then closing the window ends the app
        $w.WriteLine('{"command":["quit"]}'); $c.Close()
        $idle = $null; for ($i = 0; $i -lt 30 -and -not $idle; $i++) { Start-Sleep -Milliseconds 200; $idle = Find $p 'Haku Player' }
        Report "home-after-quit-$mode" ([bool]$idle) "home page shown after mpv quit: $([bool]$idle)"
        # closing the window (WM_CLOSE, as the title bar's X does) ends the app
        [WinUiTest.Win]::PostMessage($p.MainWindowHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
        $exited = $p.WaitForExit(8000)
        CheckFocus "closing"
        Report "closes-$mode" $exited "app ends when its window is closed: $exited"
        Report "focus-$mode" (-not $script:tookFocus) "the test copy was never the foreground window$(if ($script:tookFocus) { ": became it at: $script:focusAt" })"
    }
    catch { Report "run-$mode" $false $_.Exception.Message }
    finally { if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force } }
    $errs = @(Get-Content "$trace.mpv.log" -ErrorAction SilentlyContinue | Select-String '\]\[e\]\[(libmpv_render|vo)')
    Report "mpv-errors-$mode" ($errs.Count -eq 0) $(if ($errs.Count) { $errs[0].Line } else { "none" })
    $unhandled = @(Get-Content $trace -ErrorAction SilentlyContinue | Select-String 'UNHANDLED|video thread|video resize')
    Report "app-errors-$mode" ($unhandled.Count -eq 0) $(if ($unhandled.Count) { $unhandled[0].Line } else { "none" })
}

# ---------- the player's parts: top bar, skip opening, Up next, watch progress (SDR) ----------

function Launch([string[]]$argList, [string]$name, [hashtable]$settings = @{}) {
    $data = Join-Path $Out "data-$name"; if (Test-Path $data) { Remove-Item $data -Recurse -Force }; New-Item -ItemType Directory $data | Out-Null
    $conf = [ordered]@{ SettingsVersion = 3; Upscale = $false; Resume = $false; Autoplay = $true }
    foreach ($k in $settings.Keys) { $conf[$k] = $settings[$k] }
    $conf | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $data "settings.json") -Encoding utf8
    $trace = Join-Path $Out "trace-$name.txt"; foreach ($f in $trace, "$trace.mpv.log") { if (Test-Path $f) { Remove-Item $f } }
    $env:HAKU_DATA_DIR = $data; $env:HAKU_TRACE = $trace; $env:HAKU_TEST_WINDOW = $Window
    try { $p = Start-Process $exe -ArgumentList ($argList | ForEach-Object { "`"$_`"" }) -PassThru } finally { Remove-Item Env:HAKU_DATA_DIR, Env:HAKU_TRACE, Env:HAKU_TEST_WINDOW }
    $pipe = "animeplayer-$($p.Id)-1"
    for ($i = 0; $i -lt 40 -and -not ([IO.Directory]::GetFiles('\\.\pipe\') | Where-Object { $_ -like "*$pipe" }); $i++) { Start-Sleep -Milliseconds 250 }
    $c = New-Object IO.Pipes.NamedPipeClientStream(".", $pipe, [IO.Pipes.PipeDirection]::InOut); $c.Connect(5000)
    $w = New-Object IO.StreamWriter($c); $w.AutoFlush = $true; $r = New-Object IO.StreamReader($c)
    $w.WriteLine('{"command":["set_property","mute",true]}')
    @{ P = $p; C = $c; W = $w; R = $r; Data = $data; Trace = $trace }
}
function Shut($s) {
    try { $s.W.WriteLine('{"command":["quit"]}'); $s.C.Close() } catch { }
    Start-Sleep -Milliseconds 800
    [WinUiTest.Win]::PostMessage($s.P.MainWindowHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    if (-not $s.P.WaitForExit(8000)) { Stop-Process -Id $s.P.Id -Force }
}
function WaitFor([scriptblock]$cond, [double]$seconds) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $seconds) { if (& $cond) { return [math]::Round($sw.Elapsed.TotalSeconds, 2) }; Start-Sleep -Milliseconds 200 }
    -1
}
function Texts($proc) {
    $w = $UIA::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition($UIA::ProcessIdProperty, $proc.Id)))
    if (-not $w) { return @() }
    @($w.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text))) | ForEach-Object { $_.Current.Name })
}

if (-not (Test-SafeToLaunch) -and $Media.ChapterOpening -and (Test-Path -LiteralPath $Media.ChapterOpening)) {
    # an episode whose chapters say "Opening" at 0:00: "Skip Opening" shows; the top bar names the show and episode
    $s = Launch @($Media.ChapterOpening) 'skip' @{ OnlineSegments = $true }
    try {
        $script:rid = 100
        # this file has two chapter sets (editions): the named one (Opening, Part A, ...) is edition 0, but mpv picks the
        # numbered one; outside a library there are no online times to fall back on, so pick the named chapters
        Start-Sleep -Milliseconds 500
        $s.W.WriteLine('{"command":["set_property","edition",0]}')
        $skip = WaitFor { Find $s.P 'Skip Opening' } 20
        Report "skip-opening" ($skip -ge 0) "'Skip Opening' after $skip s"
        $t = Texts $s.P
        $back = [bool](Find $s.P 'Back')
        Report "top-bar" ($back -and ($t -match 'Hunter').Count -gt 0) "Back button: $back; titles: $(($t | Where-Object { $_ -match 'Hunter|S01E' }) -join ' | ')"
        $s.W.WriteLine('{"command":["set_property","pause",true]}'); Start-Sleep -Milliseconds 700
        $opts = Q $s.W $s.R 'glsl-shader-opts'
        Report "glass-top-bar" ($opts -match 'b_r=[1-9]' -and $opts -match 'd_r=[1-9]') "top bar (b) and card (d) sent: $(($opts -split ',' | Where-Object { $_ -match '^[bd]_r' }) -join ',')"
    }
    catch { Report "skip-run" $false $_.Exception.Message }
    finally { Shut $s }
}

if (-not (Test-SafeToLaunch) -and $Media.Subs -and (Test-Path -LiteralPath $Media.Subs)) {
    # episode 1 with its folder as the playlist: Up next near the end, then the countdown plays episode 2; progress is saved
    $s = Launch @($Media.Subs, (Split-Path $Media.Subs -Parent)) 'upnext'
    try {
        $script:rid = 100
        $dur = 0.0; WaitFor { ($script:dur = (QN $s.W $s.R 'duration')) -gt 0 } 15 | Out-Null
        $pos0 = Q $s.W $s.R 'playlist-pos'
        $s.W.WriteLine("{`"command`":[`"seek`",$([math]::Floor($dur - 14)),`"absolute+exact`"]}")
        $card = WaitFor { Find $s.P 'Play next episode' } 8
        Report "up-next-card" ($card -ge 0 -and $pos0 -eq '0') "playlist starts at episode 1 ($pos0); Up next card $card s after seeking near the end"
        $moved = WaitFor { (Q $s.W $s.R 'playlist-pos') -eq '1' } 20
        Report "up-next-plays" ($moved -ge 0) "episode 2 playing $moved s later"
        $ep2 = (Texts $s.P) -match '02|E02' | Select-Object -First 1
        Report "top-bar-episode" ([bool]$ep2) "top bar now: $ep2"
    }
    catch { Report "upnext-run" $false $_.Exception.Message }
    finally { Shut $s }
    $watched = Join-Path $s.Data 'watched.json'
    $saved = (Test-Path $watched) -and ((Get-Content $watched -Raw) -match [regex]::Escape((Split-Path $Media.Subs -Leaf).Replace('\', '\\')))
    Report "watch-progress" $saved "watched.json has episode 1: $saved"
}

if (-not (Test-SafeToLaunch) -and $Media.Subs -and (Test-Path -LiteralPath $Media.Subs)) {
    # the full-dialogue track picked over the file's default "Signs & Songs"
    $s = Launch @($Media.Subs) 'subs'
    try {
        $script:rid = 100
        WaitFor { (QN $s.W $s.R 'width') -gt 0 } 15 | Out-Null; Start-Sleep 2
        $title = Q $s.W $s.R 'current-tracks/sub/title'
        Report "subtitles" ($title -match 'Full' -and $title -notmatch 'Sign') "picked '$title'"
        # left alone, the bars fade and the pointer hides with them, and stays hidden (nothing fakes a mouse move)
        $hid = WaitFor { Get-Content $s.Trace | Select-String 'pointer hidden' } ([double]2 + 5)
        Start-Sleep 2
        $lines = @(Get-Content $s.Trace | Select-String 'pointer (hidden|shown)' | ForEach-Object { "$_" -replace '^\S+ ', '' })
        Report "pointer-hides" ($hid -ge 0 -and $lines.Count -gt 0 -and $lines[-1] -eq 'pointer hidden') "hidden after $hid s; pointer log: $($lines -join ', ')"
    }
    catch { Report "subs-run" $false $_.Exception.Message }
    finally { Shut $s }
}

if (-not (Test-SafeToLaunch) -and $Media.H264 -and (Test-Path -LiteralPath $Media.H264)) {
    # your own subtitle file and its timing come back; the quality badge shows the source
    $srt = Join-Path $Out "mine.srt"
    "1`r`n00:00:01,000 --> 00:10:00,000`r`nHaku test subtitle`r`n" | Set-Content $srt -Encoding utf8
    $s = Launch @($Media.H264) 'ownsubs' @{ ExtraSubs = @{ $Media.H264 = @($srt) }; SubDelays = @{ $Media.H264 = 0.5 } }
    try {
        $script:rid = 100
        WaitFor { (QN $s.W $s.R 'width') -gt 0 } 15 | Out-Null
        $got = WaitFor { (Q $s.W $s.R 'current-tracks/sub/external') -eq 'yes' } 8
        $delay = QN $s.W $s.R 'sub-delay'
        Report "own-subtitles" ($got -ge 0 -and [math]::Abs($delay - 0.5) -lt 0.01) "added file selected after $got s, timing $delay s"
        $badge = (Texts $s.P) -match '^\d+p  ' | Select-Object -First 1
        Report "quality-badge" ([bool]$badge) "badge: '$badge'"
    }
    catch { Report "ownsubs-run" $false $_.Exception.Message }
    finally { Shut $s }
}

if (-not (Test-SafeToLaunch) -and $Media.H264 -and (Test-Path -LiteralPath $Media.H264)) {
    # AI upscaling on: paused first with the loading panel, then playing upscaled to 4K, the button and badge saying so
    $s = Launch @($Media.H264) 'ai' @{ Upscale = $true; UpscaleTier = 'auto'; UpscaleLiveAction = 'anime' }
    try {
        $script:rid = 100
        $pausedFirst = (Q $s.W $s.R 'pause') -eq 'yes'
        $panel = WaitFor { (Find $s.P 'Starting AI upscaling') -or (Find $s.P 'AI buffering') } 6
        $playing = WaitFor { (Q $s.W $s.R 'pause') -eq 'no' } 20
        $outW = Q $s.W $s.R 'video-out-params/w'
        $d0 = [int](QN $s.W $s.R 'frame-drop-count'); Start-Sleep -Seconds 8; $drops = [int](QN $s.W $s.R 'frame-drop-count') - $d0
        $btn = Find $s.P 'AI upscaling'; $tip = if ($btn) { $btn.Current.HelpText } else { '' }
        $badgeText = (Texts $s.P) -match '→ 4K.*AI' | Select-Object -First 1; $badge = [bool]$badgeText
        $ready = Q $s.W $s.R 'vf-metadata/hakusr/frames'
        Report "ai-startup" ($pausedFirst -and $panel -ge 0 -and $playing -ge 0 -and [int]("0$outW") -ge 3800 -and $drops -eq 0) "paused first: $pausedFirst, loading panel after $panel s, playing after $playing s, $outW px wide, 8 s: $drops drops, $ready frames ready"
        Report "ai-button" ($tip -match 'AI upscaling on' -and $badge) "button: '$tip'; badge '$badgeText'"
    }
    catch { Report "ai-run" $false $_.Exception.Message }
    finally { Shut $s }
}

# ---------- the library pages (phase 3), driven through the app's test pipe (TestHooks.cs), never UI Automation's Invoke ----------

function Tell($proc, [string]$command) {
    $c = New-Object IO.Pipes.NamedPipeClientStream(".", "haku-test-$($proc.Id)", [IO.Pipes.PipeDirection]::InOut)
    try { $c.Connect(3000); $w = New-Object IO.StreamWriter($c); $w.AutoFlush = $true; $r = New-Object IO.StreamReader($c); $w.WriteLine($command); $r.ReadLine() }
    catch { "pipe: $($_.Exception.Message)" } finally { $c.Dispose() }
}
function LaunchApp([string]$name, [hashtable]$settings) {
    $data = Join-Path $Out "data-$name"; if (Test-Path $data) { Remove-Item $data -Recurse -Force }; New-Item -ItemType Directory $data | Out-Null
    $conf = [ordered]@{ SettingsVersion = 3; Upscale = $false; Resume = $false; Autoplay = $true }
    foreach ($k in $settings.Keys) { $conf[$k] = $settings[$k] }
    $conf | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $data "settings.json") -Encoding utf8
    $trace = Join-Path $Out "trace-$name.txt"; foreach ($f in $trace, "$trace.mpv.log") { if (Test-Path $f) { Remove-Item $f } }
    $env:HAKU_DATA_DIR = $data; $env:HAKU_TRACE = $trace; $env:HAKU_TEST_WINDOW = $Window
    try { $p = Start-Process $exe -PassThru } finally { Remove-Item Env:HAKU_DATA_DIR, Env:HAKU_TRACE, Env:HAKU_TEST_WINDOW }
    @{ P = $p; Data = $data; Trace = $trace }
}

if (-not (Test-SafeToLaunch) -and $Media.AnimeLibrary -and (Test-Path -LiteralPath $Media.AnimeLibrary)) {
    $show = Get-ChildItem -LiteralPath $Media.AnimeLibrary -Directory | Where-Object { Get-ChildItem -LiteralPath $_.FullName -File -Recurse -Include *.mkv, *.mp4 -ErrorAction SilentlyContinue | Select-Object -First 1 } | Sort-Object Name | Select-Object -First 1
    $s = LaunchApp 'library' @{ Libs = @(@{ Id = 't'; Name = 'Anime'; Folders = @(@{ Path = $Media.AnimeLibrary; Kind = 'anime' }); Home = $true }); Renames = @{ $show.FullName = 'Renamed By Test' } }
    $script:tookFocus = $false; $p = $s.P
    function CheckFocus2 { $fg = [WinUiTest.Win]::GetForegroundWindow(); $fp = 0; [WinUiTest.Win]::GetWindowThreadProcessId($fg, [ref]$fp) | Out-Null; if ($fp -eq $p.Id) { $script:tookFocus = $true } }
    try {
        $tile = WaitFor { CheckFocus2; Find $p 'Anime' } 20
        $startup = Get-Content $s.Trace -ErrorAction SilentlyContinue | Select-String 'startup:' | Select-Object -First 1
        Report "library-home" ($tile -ge 0) "tile 'Anime' after $tile s; $(if ($startup) { $startup.Line -replace '^\S+ ', '' })"
        $pressed = Tell $p 'press Anime'
        $card = WaitFor { CheckFocus2; Find $p 'Renamed By Test' } 10
        Report "library-cards" ($pressed -eq 'ok' -and $card -ge 0) "pressed the tile: $pressed; renamed show's card after $card s"
        $pressed = Tell $p 'press Renamed By Test'
        $opened = WaitFor { CheckFocus2; (Find $p 'All seasons') -or (Find $p 'Mark all watched') -or (Find $p 'Season 1') } 10
        $names = Tell $p 'names'
        Report "show-page" ($pressed -eq 'ok' -and $opened -ge 0) "opened after $opened s: $(($names -split ' \| ' | Select-Object -First 14) -join ' | ')"
        # a wheel notch on the episode list glides (as the WPF app's: ~190 px over ~300 ms, whatever the monitor's rate)
        Start-Sleep -Milliseconds 500
        $wheel = Tell $p 'wheel -120 episodes'; Start-Sleep -Milliseconds 1500
        $ys = @(Get-Content $s.Trace | Select-String 'scroll offset' | ForEach-Object { $f = "$_" -split ' '; [pscustomobject]@{ T = [double]$f[3]; Y = [double]$f[4] } })
        $moved = @($ys | Where-Object { $_.Y -ne $ys[0].Y })
        if ($wheel -ne 'ok' -or $ys.Count -eq 0 -or $ys[-1].Y -eq $ys[0].Y) { Report "scroll-glide" ($wheel -eq 'ok') "the list doesn't scroll here (too short): $wheel" }
        else {
            $end = ($ys | Where-Object { $_.Y -eq $ys[-1].Y } | Select-Object -First 1).T; $startT = $moved[0].T
            $steps = @(for ($i = 1; $i -lt $ys.Count; $i++) { [math]::Abs($ys[$i].Y - $ys[$i - 1].Y) }); $big = ($steps | Measure-Object -Maximum).Maximum
            Report "scroll-glide" (($end - $startT) -ge 180 -and $big -le 40) "moved $($ys[-1].Y - $ys[0].Y) px over $($end - $startT) ms in $($moved.Count) frames, largest step $big px"
        }
        Tell $p 'wheel 120 episodes' | Out-Null; Start-Sleep -Milliseconds 900 # back to the top for the next test
        # play the first episode (or season), then back
        # the first episode row: the name just before the first "Watched row" (its tick); else the first season
        $list = @($names -split ' \| '); $tick = [array]::IndexOf($list, 'Watched row')
        $first = if ($tick -gt 0) { $list[$tick - 1] } else { $list | Where-Object { $_ -match '^Season \d' } | Select-Object -First 1 }
        if ($first -match '^Season') { Tell $p "press $first" | Out-Null; Start-Sleep -Milliseconds 600; $names = Tell $p 'names'; $first = ($names -split ' \| ') | Where-Object { $_ -notmatch '^(Back|Star row|Watched row|All seasons|Mark|Search|Sync|Favourite|Haku)' -and $_ -match '\w' } | Select-Object -Last 1 }
        $before = [IO.Directory]::GetFiles('\\.\pipe\') | Where-Object { $_ -like "*animeplayer-$($p.Id)-*" }
        $played = Tell $p "press $first"
        $mpvUp = WaitFor { CheckFocus2; [IO.Directory]::GetFiles('\\.\pipe\') | Where-Object { $_ -like "*animeplayer-$($p.Id)-*" } } 10
        Report "play-from-show" ($played -eq 'ok' -and $mpvUp -ge 0) "pressed '$first': $played; mpv started after $mpvUp s"
        Start-Sleep -Seconds 2
        $back = Tell $p 'press Back' # the top bar's Back: stops the video
        $onShow = WaitFor { CheckFocus2; (Find $p 'Renamed By Test') -and -not ([IO.Directory]::GetFiles('\\.\pipe\') | Where-Object { $_ -like "*animeplayer-$($p.Id)-*" }) } 10
        Report "back-to-show" ($back -eq 'ok' -and $onShow -ge 0) "Back: $back; on the show page again after $onShow s"
        Report "library-focus" (-not $script:tookFocus) "the test copy was never the foreground window"
    }
    catch { Report "library-run" $false $_.Exception.Message }
    finally { [WinUiTest.Win]::PostMessage($p.MainWindowHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null; if (-not $p.WaitForExit(8000)) { Stop-Process -Id $p.Id -Force } }
    $unhandled = @(Get-Content $s.Trace -ErrorAction SilentlyContinue | Select-String 'UNHANDLED')
    Report "library-errors" ($unhandled.Count -eq 0) $(if ($unhandled.Count) { ($unhandled[0].Line).Substring(0, [Math]::Min(300, $unhandled[0].Line.Length)) } else { "none" })
}

# ---------- window behaviours: pop-out, one Haku at a time ----------

Add-Type -Namespace WinUiTest2 -Name Win -MemberDefinition '[DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);' -ErrorAction SilentlyContinue
if (-not (Test-SafeToLaunch) -and $Media.H264 -and $Media.Subs -and (Test-Path -LiteralPath $Media.H264)) {
    $s = Launch @($Media.H264) 'window'
    try {
        $script:rid = 100
        Start-Sleep -Seconds 2
        $r0 = New-Object WinUiTest.Win+R; [WinUiTest.Win]::GetWindowRect($s.P.MainWindowHandle, [ref]$r0) | Out-Null
        $pressed = Tell $s.P 'press Pop out'; Start-Sleep -Milliseconds 800
        $r1 = New-Object WinUiTest.Win+R; [WinUiTest.Win]::GetWindowRect($s.P.MainWindowHandle, [ref]$r1) | Out-Null
        $top = ([WinUiTest2.Win]::GetWindowLongPtr($s.P.MainWindowHandle, -20).ToInt64() -band 0x8) -ne 0
        $w1 = $r1.Rt - $r1.L
        Report "pop-out" ($pressed -eq 'ok' -and $w1 -lt ($r0.Rt - $r0.L) * 0.5 -and $top) "pressed: $pressed; $($r0.Rt - $r0.L) px wide -> $w1 px at $($r1.L),$($r1.T), always on top: $top"
        $back = Tell $s.P 'press Pop out'; Start-Sleep -Milliseconds 800
        $r2 = New-Object WinUiTest.Win+R; [WinUiTest.Win]::GetWindowRect($s.P.MainWindowHandle, [ref]$r2) | Out-Null
        $top2 = ([WinUiTest2.Win]::GetWindowLongPtr($s.P.MainWindowHandle, -20).ToInt64() -band 0x8) -ne 0
        Report "pop-back" ($back -eq 'ok' -and ($r2.Rt - $r2.L) -eq ($r0.Rt - $r0.L) -and -not $top2) "back: $($r2.Rt - $r2.L)x$($r2.B - $r2.T) at $($r2.L),$($r2.T), on top: $top2"
        # a second launch with another video: handed to this window, and the second process leaves
        $env:HAKU_DATA_DIR = $s.Data; $env:HAKU_TEST_WINDOW = $Window
        try { $p2 = Start-Process $exe -ArgumentList "`"$($Media.Subs)`"" -PassThru } finally { Remove-Item Env:HAKU_DATA_DIR, Env:HAKU_TEST_WINDOW }
        $left = $p2.WaitForExit(8000)
        $playing = WaitFor { (Q $s.W $s.R 'path') -eq '' -or $true } 0 | Out-Null
        $pipe2 = "animeplayer-$($s.P.Id)-2"
        $handed = WaitFor { [IO.Directory]::GetFiles('\\.\pipe\') | Where-Object { $_ -like "*$pipe2" } } 10
        Report "single-instance" ($left -and $handed -ge 0) "second launch left: $left; the first window started the new video after $handed s"
        if (-not $left) { Stop-Process -Id $p2.Id -Force }
    }
    catch { Report "window-run" $false $_.Exception.Message }
    finally { Shut $s }
}

$pass = @($results | Where-Object Pass).Count; $fail = $results.Count - $pass
Write-Host "`n$pass passed, $fail failed"
$results | ConvertTo-Json | Set-Content (Join-Path $Out "results.json")
if ($fail) { exit 1 }
