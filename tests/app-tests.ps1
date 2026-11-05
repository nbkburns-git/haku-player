# Haku Player end-to-end tests: AI upscaling, its buffer, seeking, pausing, the AI button, subtitles, show details.
# Builds a test copy into $Out and runs it with its own data folder (HAKU_DATA_DIR), so your real settings and history are
# never touched. Each test drives the app through mpv's JSON pipe and Windows UI Automation (never the real mouse).
#
#   powershell -ExecutionPolicy Bypass -File tests\app-tests.ps1            (all tests; a few minutes, plays sound muted)
#   ... -Only startup,seek -NoBuild
#
# The test videos are set in tests\media.local.ps1 (see $Media below). Results: printed, and in $Out\results.json.
param(
    [string]$Out = (Join-Path $env:TEMP "haku-tests"),
    [string[]]$Only = @(),
    [switch]$NoBuild,
    # where the test window goes, in screen pixels (another monitor, say); it never takes focus: "x,y,w,h"
    [string]$Window = $env:HAKU_TEST_WINDOW
)
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path $PSScriptRoot -Parent
$AppDir = Join-Path $Out "app"
$DataDir = Join-Path $Out "data"
$TraceFile = Join-Path $Out "trace.txt"

# Your test videos (a missing one skips its tests). Put them in tests\media.local.ps1 (git-ignored), as $Media with the
# same keys, so your file names stay on your PC.
$Media = @{
    H264   = ''   # a 1920x1080 H.264 episode, 23.976 fps, at least 6 minutes long (the seek goes to 5:00)
    Hevc10 = ''   # an HEVC 10-bit video under 1920 wide, ideally not a multiple of 16 (e.g. 1432x1080) at 29.97 fps
    UHD    = ''   # a 3840x2160 video (should play as it is)
    Subs   = ''   # episode 01 of a show whose default subtitle track is "Signs & Songs", with episode 02 next to it
    AnimeLibrary = '' # the anime library folder those are in (for the subtitle, next-episode and show-detail tests)
    LiveAction = ''   # a 1080p live-action TV episode (RTX Video)
    TvLibrary  = ''   # the TV library folder it's in
    Cartoon    = ''   # a cartoon episode in that library (should get the anime model)
    CartoonKey = ''   # its saved-details name: 'tv_' + the show's name with non-word characters as _, e.g. 'tv_South_Park'
}
# badly named show folders in that library and the title each should get: @{ 'Some_Show_Complete' = 'Some Show' } (folder name with
# non-word characters as _)
$ShowDetails = @{}
$TestWindow = $null # or set it in media.local.ps1
$localMedia = Join-Path $PSScriptRoot "media.local.ps1"
if (Test-Path $localMedia) { . $localMedia }

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type -Namespace HakuTest -Name Win -MemberDefinition '[StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Rt, B; } [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r); [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);'
[HakuTest.Win]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null   # per-monitor pixels, as Haku and HAKU_TEST_WINDOW use
$UIA = [System.Windows.Automation.AutomationElement]
$Results = New-Object System.Collections.Generic.List[object]
$RunStart = Get-Date

$Only = @($Only | ForEach-Object { $_ -split ',' } | Where-Object { $_ })  # "-Only a,b" through powershell -File arrives as one string
function Want($name) { $Only.Count -eq 0 -or $Only -contains $name }
function Report($name, [bool]$pass, $detail) {
    $Results.Add([pscustomobject]@{ Test = $name; Result = $(if ($pass) { 'pass' } else { 'FAIL' }); Detail = $detail })
    Write-Host ("{0,-5} {1,-22} {2}" -f $(if ($pass) { 'PASS' } else { 'FAIL' }), $name, $detail) -ForegroundColor $(if ($pass) { 'Green' } else { 'Red' })
}
function Skip($name, $why) { $Results.Add([pscustomobject]@{ Test = $name; Result = 'skipped'; Detail = $why }); Write-Host ("SKIP  {0,-22} {1}" -f $name, $why) -ForegroundColor Yellow }

# ---------- the app ----------

function Start-Haku([string]$file, [hashtable]$settings = @{}, [hashtable]$details = @{}) {
    if (Test-Path $DataDir) { Remove-Item $DataDir -Recurse -Force }
    New-Item -ItemType Directory $DataDir | Out-Null
    # show details as Haku saves them (info\<key>.json), so a test doesn't wait for the network: @{ 'tv_South_Park' = @('Animation', 'Comedy') }
    if ($details.Count) { New-Item -ItemType Directory (Join-Path $DataDir 'info') | Out-Null }
    # (or a saved file as it is: a JSON string)
    foreach ($k in $details.Keys) { $(if ($details[$k] -is [string] -and $details[$k].StartsWith('{')) { $details[$k] } else { @{ Title = $k; Genres = $details[$k]; Source = 'test' } | ConvertTo-Json }) | Set-Content (Join-Path $DataDir "info\$k.json") -Encoding utf8 }
    # UpscaleLiveAction = anime: files opened from outside a library count as movies, and these tests are about the AI
    $conf = [ordered]@{ SettingsVersion = 3; Upscale = $true; UpscaleTier = 'auto'; UpscaleLiveAction = 'anime'; Resume = $false; Autoplay = $true; AutoPopOut = $false }
    foreach ($k in $settings.Keys) { $conf[$k] = $settings[$k] }
    $conf | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $DataDir "settings.json") -Encoding utf8
    $env:HAKU_DATA_DIR = $DataDir; $env:HAKU_TRACE = $TraceFile; if (-not $Window) { $Window = $TestWindow }; if ($Window) { $env:HAKU_TEST_WINDOW = $Window }
    try { $proc = if ($file) { Start-Process (Join-Path $AppDir "HakuPlayer.exe") -ArgumentList "`"$file`"" -PassThru } else { Start-Process (Join-Path $AppDir "HakuPlayer.exe") -PassThru } }
    finally { Remove-Item Env:HAKU_DATA_DIR, Env:HAKU_TRACE, Env:HAKU_TEST_WINDOW -ErrorAction SilentlyContinue }
    $script:Hk = @{ Proc = $proc; Id = 7000 }
    if ($file) { Connect-Pipe }
    $script:Hk
}

function Connect-Pipe {
    $name = "animeplayer-$($Hk.Proc.Id)-1"
    for ($i = 0; $i -lt 60 -and -not ([IO.Directory]::GetFiles('\\.\pipe\') | Where-Object { $_ -like "*$name" }); $i++) { Start-Sleep -Milliseconds 500 }
    $client = New-Object IO.Pipes.NamedPipeClientStream(".", $name, [IO.Pipes.PipeDirection]::InOut)
    $client.Connect(5000)
    $Hk.Client = $client
    $Hk.Writer = New-Object IO.StreamWriter($client); $Hk.Writer.AutoFlush = $true
    $Hk.Reader = New-Object IO.StreamReader($client)
    Send '{"command":["set_property","mute",true]}'
}

function Send([string]$json) { $Hk.Writer.WriteLine($json) }

# One property as a string ("" when unavailable).
function Q([string]$prop) {
    $Hk.Id++
    $Hk.Writer.WriteLine("{`"command`":[`"get_property_string`",`"$prop`"],`"request_id`":$($Hk.Id)}")
    for ($k = 0; $k -lt 500; $k++) {
        $line = $Hk.Reader.ReadLineAsync()
        if (-not $line.Wait(4000)) { return "" }
        if ($line.Result -match "`"request_id`":$($Hk.Id)\b") {
            if ($line.Result -notmatch '"error":"success"') { return "" }
            return ($line.Result -replace '.*"data":"?(.*?)"?,"request_id.*', '$1')
        }
    }
    ""
}
function QN([string]$prop) { $v = Q $prop; $d = 0.0; if ([double]::TryParse($v, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$d)) { $d } else { -1 } }

function Stop-Haku {
    if (-not $Hk) { return }
    try { $Hk.Client.Close() } catch { }
    Get-CimInstance Win32_Process -Filter "ParentProcessId=$($Hk.Proc.Id)" | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Stop-Process -Id $Hk.Proc.Id -Force -ErrorAction SilentlyContinue
    $script:Hk = $null
    Start-Sleep -Milliseconds 500
}

function Window { $UIA::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition($UIA::ProcessIdProperty, $Hk.Proc.Id))) }
function Find([string]$name) { $w = Window; if ($w) { $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($UIA::NameProperty, $name))) } }
function Press([string]$name) { $e = Find $name; if (-not $e) { throw "no '$name' button" }; $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function AiTip { $e = Find 'AI upscaling'; if ($e) { $e.Current.HelpText } else { "" } }
function Loading { [bool]((Find 'Starting AI upscaling') -or (Find 'AI buffering')) }

# Wait (polling) until the script block is true; returns the seconds it took, or -1 on time-out.
function WaitFor([scriptblock]$cond, [double]$seconds) {
    $t0 = Get-Date
    while (((Get-Date) - $t0).TotalSeconds -lt $seconds) { if (& $cond) { return [math]::Round(((Get-Date) - $t0).TotalSeconds, 2) }; Start-Sleep -Milliseconds 200 }
    -1
}

# Over $seconds of playback: the most dropped frames and the fewest AI frames ready.
function Watch([double]$seconds) {
    $t0 = Get-Date; $minReady = 999; $cap = 0
    while (((Get-Date) - $t0).TotalSeconds -lt $seconds) {
        $ready = QN 'vf-metadata/hakusr/frames'; $c = QN 'vf-metadata/hakusr/cap'
        if ($ready -ge 0 -and $ready -lt $minReady) { $minReady = $ready }
        if ($c -gt 0) { $cap = $c }
        Start-Sleep -Milliseconds 300
    }
    @{ Drops = (QN 'frame-drop-count'); MinReady = $minReady; Cap = $cap }
}

function Hangs { @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Id = 1002; StartTime = $RunStart } -ErrorAction SilentlyContinue | Where-Object { $_.Properties[5].Value -like "$AppDir*" }) }

# ---------- build ----------

New-Item -ItemType Directory -Force $Out | Out-Null
if (Test-Path $TraceFile) { Remove-Item $TraceFile -Force }
# a test copy left open by an earlier, interrupted run would share the GPU and skew every number (only copies in $AppDir)
Get-Process HakuPlayer -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$AppDir*" } | ForEach-Object { Stop-Process -Id $_.Id -Force }
if (-not $NoBuild) {
    Write-Host "building the test copy..."
    dotnet build (Join-Path $RepoRoot "AnimePlayer.csproj") -c Debug -o $AppDir -v q --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "build failed" }
}

# ---------- tests ----------

try {
    if ((Want 'startup') -or (Want 'seek') -or (Want 'pause') -or (Want 'button')) {
        if (-not (($Media.H264 -and (Test-Path -LiteralPath $Media.H264)))) { Skip 'startup' "missing $($Media.H264)" } else {
            Start-Haku $Media.H264 | Out-Null
            $w = 0; WaitFor { ($script:w = QN 'width') -gt 0 } 20 | Out-Null
            $pausedFirst = (Q 'pause') -eq 'yes'
            $sawLoading = $false; $maxReady = 0
            $resumed = WaitFor { if (Loading) { $script:sawLoading = $true }; $r = QN 'vf-metadata/hakusr/frames'; if ($r -gt $script:maxReady) { $script:maxReady = $r }; (Q 'pause') -eq 'no' } 20
            $outW = QN 'video-out-params/w'
            $watch = Watch 10
            if ($Window -or $TestWindow) {
                # playing reshapes the window to the video: it must stay on the monitor it was put on
                $want = [int]((($(if ($Window) { $Window } else { $TestWindow })) -split ',')[0])
                $rect = New-Object HakuTest.Win+R; [HakuTest.Win]::GetWindowRect($Hk.Proc.MainWindowHandle, [ref]$rect) | Out-Null
                $screen = [System.Windows.Forms.Screen]::FromPoint((New-Object System.Drawing.Point($want, 200)))
                $inside = $rect.L -ge $screen.Bounds.Left -and $rect.Rt -le $screen.Bounds.Right
                Report 'stays-on-monitor' $inside "window at $($rect.L),$($rect.T) to $($rect.Rt),$($rect.B), its monitor spans $($screen.Bounds.Left) to $($screen.Bounds.Right)"
            }
            if (Want 'startup') {
                Report 'startup' ($pausedFirst -and $resumed -ge 0 -and $outW -ge 2 * $w - 1 -and $watch.Drops -eq 0 -and $maxReady -ge 0.75 * $watch.Cap) `
                    "paused first: $pausedFirst, playing after $resumed s with $maxReady/$($watch.Cap) frames ready, $w -> $outW px, loading panel: $sawLoading, 10 s: $($watch.Drops) drops, buffer never below $($watch.MinReady)"
            }
            if (Want 'seek') {
                # as Haku's timeline does it: where the seek goes, then an exact seek; timed to mpv's playback-restart
                $listen = New-Object IO.Pipes.NamedPipeClientStream(".", "animeplayer-$($Hk.Proc.Id)-1", [IO.Pipes.PipeDirection]::InOut); $listen.Connect(3000)
                $lw = New-Object IO.StreamWriter($listen); $lw.AutoFlush = $true; $lr = New-Object IO.StreamReader($listen)
                $before = QN 'frame-drop-count'
                $lw.WriteLine('{"command":["vf-command","hakusr","skip_until","300"]}'); $lw.WriteLine('{"command":["seek",300,"absolute+exact"]}')
                $sw = [Diagnostics.Stopwatch]::StartNew(); $ms = -1
                while ($sw.Elapsed.TotalSeconds -lt 6) { $l = $lr.ReadLineAsync(); if (-not $l.Wait(6000)) { break }; if ($l.Result -match 'playback-restart') { $ms = $sw.ElapsedMilliseconds; break } }
                $listen.Close()
                $refill = WaitFor { $script:lastReady = QN 'vf-metadata/hakusr/frames'; $script:lastCap = QN 'vf-metadata/hakusr/cap'; $script:lastReady -ge 0.6 * $script:lastCap -and $script:lastCap -gt 0 } 10
                $after = Watch 4
                Report 'seek' ($ms -ge 0 -and $ms -lt 1000 -and $refill -ge 0 -and $after.Drops -le $before) "exact seek landed in $ms ms, buffer 60%+ full again after $refill s ($lastReady/$lastCap), drops after: $($after.Drops)"
            }
            if (Want 'pause') {
                Send '{"command":["set_property","pause",true]}'
                $full = WaitFor { (QN 'vf-metadata/hakusr/frames') -ge (QN 'vf-metadata/hakusr/cap') } 5
                $outPaused = QN 'video-out-params/w'
                Send '{"command":["set_property","pause",false]}'
                Report 'pause' ($full -ge 0 -and $outPaused -ge 2 * $w - 1) "buffer full $full s into the pause, still upscaled ($outPaused px)"
            }
            if (Want 'button') {
                Press 'AI upscaling'
                $off = WaitFor { (QN 'video-out-params/w') -le $w } 8
                $tipOff = AiTip
                Press 'AI upscaling'
                $on = WaitFor { (QN 'video-out-params/w') -ge 2 * $w - 1 } 12
                Report 'button' ($off -ge 0 -and $on -ge 0 -and $tipOff -match 'turned off') "off after $off s ('$tipOff'), back on after $on s"
            }
            Stop-Haku
        }
    }

    if (Want 'paused-enable') {
        if (-not (($Media.H264 -and (Test-Path -LiteralPath $Media.H264)))) { Skip 'paused-enable' "missing file" } else {
            Start-Haku $Media.H264 @{ Upscale = $false } | Out-Null
            $w = 0; WaitFor { ($script:w = QN 'width') -gt 0 } 20 | Out-Null
            Start-Sleep 3
            Send '{"command":["set_property","pause",true]}'; Start-Sleep 1
            Press 'AI upscaling'
            $sawLoading = $false
            $took = WaitFor { if (Loading) { $script:sawLoading = $true }; (QN 'video-out-params/w') -ge 2 * $w - 1 } 10
            $stillPaused = (Q 'pause') -eq 'yes'
            Start-Sleep 1
            $gone = -not (Loading)
            Report 'paused-enable' ($took -ge 0 -and $stillPaused) "AI picture $took s after the button, still paused: $stillPaused, loading panel shown: $sawLoading, gone after: $gone"
            Stop-Haku
        }
    }

    if (Want 'hevc10') {
        if (-not (($Media.Hevc10 -and (Test-Path -LiteralPath $Media.Hevc10)))) { Skip 'hevc10' "missing file" } else {
            Start-Haku $Media.Hevc10 | Out-Null
            $w = 0; WaitFor { ($script:w = QN 'width') -gt 0 } 20 | Out-Null
            $took = WaitFor { (QN 'video-out-params/w') -ge 2 * $w - 1 -and (Q 'pause') -eq 'no' } 25
            $watch = Watch 15
            $tip = AiTip
            # the right edge: the last column against one 12 px in (a green line had shown on 1432-wide video)
            Send '{"command":["set_property","pause",true]}'; Start-Sleep 1
            $png = Join-Path $Out "edge.png"; if (Test-Path $png) { Remove-Item $png -Force }
            Send ("{`"command`":[`"screenshot-to-file`",`"" + ($png -replace '\\', '/') + "`",`"video`"]}")
            WaitFor { Test-Path $png } 8 | Out-Null; Start-Sleep 1
            $edge = "no screenshot"; $green = $true
            if (Test-Path $png) {
                $bmp = [System.Drawing.Bitmap]::FromFile($png)
                function Greenness($x) { $sum = 0; for ($y = 20; $y -lt $bmp.Height - 20; $y += 8) { $c = $bmp.GetPixel($x, $y); $sum += $c.G - ($c.R + $c.B) / 2 }; $sum / [math]::Floor(($bmp.Height - 40) / 8) }
                $last = Greenness ($bmp.Width - 1); $ref = Greenness ($bmp.Width - 13)
                $green = ($last - $ref) -gt 25
                $edge = "$($bmp.Width) px wide, last column greenness $([math]::Round($last,1)) vs $([math]::Round($ref,1)) 12 px in"
                $bmp.Dispose()
            }
            $ok = $took -ge 0 -and -not $green -and ($watch.Drops -le 12 -or $tip -match 'Fast')
            Report 'hevc10' $ok "10-bit 1432x1080 at 29.97: upscaled after $took s, 15 s: $($watch.Drops) drops, buffer never below $($watch.MinReady)/$($watch.Cap); $edge; tier: '$(($tip -split "`n")[0])'"
            Stop-Haku
        }
    }

    if (Want 'uhd') {
        if (-not (($Media.UHD -and (Test-Path -LiteralPath $Media.UHD)))) { Skip 'uhd' "missing file" } else {
            Start-Haku $Media.UHD | Out-Null
            WaitFor { (QN 'width') -gt 0 } 20 | Out-Null; Start-Sleep 3
            $outW = QN 'video-out-params/w'; $tip = AiTip; $playing = (Q 'pause') -eq 'no'
            Report 'uhd' ($outW -eq 3840 -and $tip -match 'bigger than' -and $playing) "4K plays as it is ($outW px, playing: $playing): '$tip'"
            Stop-Haku
        }
    }

    if ((Want 'subtitles') -or (Want 'next-episode')) {
        if (-not (($Media.Subs -and (Test-Path -LiteralPath $Media.Subs)))) { Skip 'subtitles' "missing file" } else {
            # inside a library, as you'd play it: anime language defaults, and the whole season as the playlist
            Start-Haku $Media.Subs @{ Libs = @(@{ Id = 't'; Name = 'Anime'; Folders = @(@{ Path = $Media.AnimeLibrary; Kind = 'anime' }); Home = $true }) } | Out-Null
            WaitFor { (QN 'width') -gt 0 } 20 | Out-Null; Start-Sleep 3
            $title = Q 'current-tracks/sub/title'
            if (Want 'subtitles') { Report 'subtitles' ($title -match 'Full' -and $title -notmatch 'Sign') "picked '$title' (the file's default is Signs/Songs)" }
            if (Want 'next-episode') {
                $count = QN 'playlist-count'
                if ($count -lt 2) { Skip 'next-episode' "a one-file playlist" } else {
                    $w0 = QN 'width'
                    Send '{"command":["playlist-next"]}'
                    WaitFor { (Q 'filename') -match ' - 02' } 15 | Out-Null
                    $w = 0; WaitFor { ($script:w = QN 'width') -gt 0 } 15 | Out-Null
                    $on = WaitFor { (QN 'video-out-params/w') -ge 2 * $script:w - 1 } 20
                    $title2 = Q 'current-tracks/sub/title'
                    Report 'next-episode' ($on -ge 0 -and $title2 -match 'Full') "episode 2: upscaled again after $on s ($w px), subtitles '$title2'"
                }
            }
            Stop-Haku
        }
    }

    if (Want 'own-subtitles') {
        if (-not (($Media.H264 -and (Test-Path -LiteralPath $Media.H264)))) { Skip 'own-subtitles' "missing file" } else {
            # a subtitle file you added, and your timing fix, come back on the first video too (it once lost them)
            $srt = Join-Path $Out "mine.srt"
            "1`r`n00:00:01,000 --> 00:10:00,000`r`nHaku test subtitle`r`n" | Set-Content $srt -Encoding utf8
            Start-Haku $Media.H264 @{ ExtraSubs = @{ $Media.H264 = @($srt) }; SubDelays = @{ $Media.H264 = 0.5 } } | Out-Null
            WaitFor { (QN 'width') -gt 0 } 20 | Out-Null
            $got = WaitFor { (Q 'current-tracks/sub/external') -eq 'yes' } 8
            $delay = QN 'sub-delay'
            Report 'own-subtitles' ($got -ge 0 -and [math]::Abs($delay - 0.5) -lt 0.01) "added file selected after $got s ('$(Q 'current-tracks/sub/title')'), timing $delay s"
            Stop-Haku
        }
    }

    if (Want 'rtx') {
        if (-not ($Media.LiveAction -and (Test-Path -LiteralPath $Media.LiveAction) -and $Media.TvLibrary)) { Skip 'rtx' "no live-action file or TV library in media.local.ps1" } else {
            # a TV show from your TV library gets NVIDIA's RTX Video (sized to fill the screen's height), not the anime model
            Start-Haku $Media.LiveAction @{ UpscaleLiveAction = 'rtx'; Libs = @(@{ Id = 'tv'; Name = 'TV'; Folders = @(@{ Path = $Media.TvLibrary; Kind = 'tv' }); Home = $true }) } | Out-Null
            $w = 0; WaitFor { ($script:w = QN 'width') -gt 0 } 20 | Out-Null
            $on = WaitFor { (AiTip) -match 'RTX Video upscaling on' -and (QN 'video-out-params/w') -gt $script:w -and (Q 'pause') -eq 'no' } 20
            $vf = Q 'vf'; $tip = AiTip
            $watch = Watch 8
            $badge = [bool]((Window).FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name -match '\s+RTX$' })
            Report 'rtx' ($on -ge 0 -and $vf -match 'd3d11vpp' -and $vf -notmatch 'haku_sr' -and $tip -match 'RTX Video upscaling on' -and $badge -and $watch.Drops -eq 0) "on after $on s: '$(($tip -split "`n")[0])', badge says RTX: $badge, 8 s: $($watch.Drops) drops"
            Stop-Haku
        }
    }

    if (Want 'cartoon') {
        if (-not ($Media.Cartoon -and (Test-Path -LiteralPath $Media.Cartoon) -and $Media.TvLibrary)) { Skip 'cartoon' "no cartoon file or TV library in media.local.ps1" } else {
            # a cartoon in the TV library (IMDb genre Animation) gets the anime model, not RTX Video
            Start-Haku $Media.Cartoon @{ UpscaleLiveAction = 'rtx'; Libs = @(@{ Id = 'tv'; Name = 'TV'; Folders = @(@{ Path = $Media.TvLibrary; Kind = 'tv' }); Home = $true }) } @{ $Media.CartoonKey = @('Animation', 'Comedy') } | Out-Null
            $w = 0; WaitFor { ($script:w = QN 'width') -gt 0 } 20 | Out-Null
            $on = WaitFor { (AiTip) -match 'upscaling on' } 25
            $vf = Q 'vf'; $tip = AiTip
            Report 'cartoon' ($on -ge 0 -and $vf -match 'haku_sr' -and $vf -notmatch 'd3d11vpp') "on after $on s: '$(($tip -split "`n")[0])'"
            Stop-Haku
        }
    }

    if (Want 'leave-while-starting') {
        if (-not (($Media.H264 -and (Test-Path -LiteralPath $Media.H264)))) { Skip 'leave-while-starting' "missing file" } else {
            Start-Haku $Media.H264 | Out-Null
            Start-Sleep -Milliseconds 600
            Press 'Back'
            Start-Sleep 4
            $alive = -not $Hk.Proc.HasExited; $Hk.Proc.Refresh()
            $responds = $Hk.Proc.Responding -and [bool](Window)
            Report 'leave-while-starting' ($alive -and $responds) "left the player 0.6 s in: still running $alive, responding $responds"
            Stop-Haku
        }
    }

    if (Want 'segments-opening') {
        # an episode whose chapters say "Opening" at 0:00 gets "Skip opening" there, even with online times on
        # (Hunter x Hunter (2011) had been matched to the 1999 series, whose AniSkip times put an ending there)
        if (-not ($Media.ChapterOpening -and (Test-Path -LiteralPath $Media.ChapterOpening))) { Skip 'segments-opening' "missing file" } else {
            $lib = Split-Path (Split-Path (Split-Path $Media.ChapterOpening -Parent) -Parent) -Parent
            Start-Haku $Media.ChapterOpening @{ Upscale = $false; OnlineSegments = $true; Libs = @(@{ Id = 't'; Name = 'Anime'; Folders = @(@{ Path = $lib; Kind = 'anime' }); Home = $true }) } | Out-Null
            $op = WaitFor { Find 'Skip Opening' } 20
            $ed = [bool](Find 'Skip Ending')
            Report 'segments-opening' ($op -ge 0 -and -not $ed) "'Skip opening' after $op s, 'Skip ending' shown: $ed"
            Stop-Haku
        }
    }

    if (Want 'rematch') {
        # a show matched by an older search (Hunter x Hunter (2011) saved as the 1999 series) is looked up again once
        if (-not ($Media.ChapterOpening -and (Test-Path -LiteralPath $Media.ChapterOpening))) { Skip 'rematch' "missing file" } else {
            $showDir = Split-Path (Split-Path $Media.ChapterOpening -Parent) -Parent
            $key = [regex]::Replace((Split-Path $showDir -Leaf), '[^\w]+', '_')
            $old = @{ Title = 'Hunter x Hunter'; Genres = @('Action'); Year = 1999; AniListId = 136; Source = 'search' } | ConvertTo-Json
            Start-Haku $null @{ Libs = @(@{ Id = 't'; Name = 'Anime'; Folders = @(@{ Path = (Split-Path $showDir -Parent); Kind = 'anime' }); Home = $true }) } @{ $key = $old } | Out-Null
            $f = Join-Path $DataDir "info\$key.json"
            $fixed = WaitFor { (Get-Content $f -Raw | ConvertFrom-Json).AniListId -eq 11061 } 90
            $now = Get-Content $f -Raw | ConvertFrom-Json
            Report 'rematch' ($fixed -ge 0) "saved as '$($now.Title)' ($($now.Year), AniList $($now.AniListId)) after $fixed s"
            Stop-Haku
        }
    }

    if (Want 'show-details') {
        if (-not (($Media.AnimeLibrary -and (Test-Path -LiteralPath $Media.AnimeLibrary)))) { Skip 'show-details' "missing library" } else {
            Start-Haku $null @{ Libs = @(@{ Id = 't'; Name = 'Anime'; Folders = @(@{ Path = $Media.AnimeLibrary; Kind = 'anime' }); Home = $true }) } | Out-Null
            $want = $ShowDetails
            $info = Join-Path $DataDir "info"
            WaitFor { @($want.Keys | Where-Object { Test-Path (Join-Path $info "$_.json") }).Count -eq $want.Count } 120 | Out-Null
            $got = foreach ($k in $want.Keys) { $f = Join-Path $info "$k.json"; if (Test-Path $f) { "$k=$((Get-Content $f -Raw | ConvertFrom-Json).Title)" } else { "$k=(none)" } }
            $bad = @($want.Keys | Where-Object { $f = Join-Path $info "$_.json"; -not (Test-Path $f) -or (Get-Content $f -Raw | ConvertFrom-Json).Title -ne $want[$_] })
            Report 'show-details' ($bad.Count -eq 0) ($got -join '; ')
            Stop-Haku
        }
    }

    if (Want 'adore') {
        # the Adore model, picked by hand: downloaded and prepared the first time, then it has to keep up with 1080p
        if (-not (($Media.H264 -and (Test-Path -LiteralPath $Media.H264)))) { Skip 'adore' "missing file" } else {
            Start-Haku $Media.H264 @{ UpscaleModel = 'adore'; UpscaleTier = 'adore' } | Out-Null
            $w = 0; WaitFor { ($script:w = QN 'width') -gt 0 } 20 | Out-Null
            $took = WaitFor { (QN 'video-out-params/w') -ge 2 * $w - 1 -and (AiTip) -match 'Adore' } 240
            Start-Sleep 3
            $watch = Watch 20
            # it must work everywhere; keeping up is the GPU's business (a 4070 has ~8% to spare at 1080p film, less if
            # something else uses the GPU), so that part is reported, not judged
            $kept = (AiTip) -match 'on: Adore'
            Report 'adore' ($took -ge 0) "on after $took s (first time: download + engine); kept up for 20 s: $kept ($($watch.Drops) drops, buffer never below $($watch.MinReady)/$($watch.Cap)); '$(((AiTip) -split "`n")[0])'"
            Stop-Haku
        }
    }

    # ---------- the anime model choice (Settings.UpscaleModel): AnimeJaNai or Adore ----------
    function MenuPress([string]$name) { if (-not (Find $name)) { Press 'Settings'; Start-Sleep 0.6 }; Press $name }
    function TipFor([string]$pattern, [double]$seconds) { WaitFor { (AiTip) -match $pattern } $seconds }
    $hasH264 = $Media.H264 -and (Test-Path -LiteralPath $Media.H264)

    if (Want 'adore-first') {
        # picking Adore the first time: downloaded, checked, prepared while the video plays on, then used. It runs with
        # its own empty models folder (HAKU_MODELS_DIR), so your pack's models and engines are never touched.
        if (-not $hasH264) { Skip 'adore-first' "missing file" } else {
            $store = Join-Path $Out "models-first"
            if (Test-Path $store) { Remove-Item $store -Recurse -Force }
            New-Item -ItemType Directory $store | Out-Null
            $env:HAKU_MODELS_DIR = $store
            try { Start-Haku $Media.H264 @{ UpscaleModel = 'adore'; UpscaleTier = 'auto' } | Out-Null } finally { Remove-Item Env:HAKU_MODELS_DIR }
            $model = Join-Path $store "models\2x_Adore_renarchi_fp16.onnx"; $engine = Join-Path $store "engines\2x_Adore_renarchi_fp16.max2560x1728.engine"
            # what the AI button says, every 2 s: the steps it went through (shown when it fails)
            $seen = [System.Collections.Generic.List[string]]::new(); $t0 = Get-Date; $prep = -1; $on = -1; $playing = $false
            while (((Get-Date) - $t0).TotalSeconds -lt 300 -and $on -lt 0) {
                $tip = ((AiTip) -split "`n")[0]; $secs = [math]::Round(((Get-Date) - $t0).TotalSeconds, 1)
                if ($seen.Count -eq 0 -or $seen[$seen.Count - 1] -notlike "*$tip*") { $seen.Add("$secs s: $tip | pause=$(Q 'pause')") }
                if ($prep -lt 0 -and $tip -match 'downloading and preparing Adore') { $prep = $secs }
                if ($prep -ge 0 -and -not $playing) { $playing = (Q 'pause') -eq 'no' }
                if ($tip -match 'on: Adore') { $on = $secs }
                Start-Sleep 2
            }
            $hash = if (Test-Path -LiteralPath $model) { (Get-FileHash -LiteralPath $model -Algorithm SHA256).Hash } else { "(none)" }
            $ok = $prep -ge 0 -and $playing -and $on -ge 0 -and $hash -eq '79E007EDA5721CFA596945D5849320E2FAD67F8596F9AC31ED06CC8E3DDAF57D' -and (Test-Path -LiteralPath $engine)
            Report 'adore-first' $ok ("said 'downloading and preparing' after $prep s, video playing meanwhile: $playing, Adore on after $on s, model checksum ok: $($hash -like '79E007ED*'), engine built: $(Test-Path -LiteralPath $engine)" + $(if (-not $ok) { " | steps: " + ($seen -join ' / ') } else { "" }))
            Stop-Haku
            Remove-Item $store -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    if (Want 'adore-switch') {
        # changing model and tier in the player's menu while it plays: each change is used straight away and saved
        if (-not $hasH264) { Skip 'adore-switch' "missing file" } else {
            Start-Haku $Media.H264 @{ UpscaleModel = 'animejanai'; UpscaleTier = 'auto' } | Out-Null
            $w = 0; WaitFor { ($script:w = QN 'width') -gt 0 } 20 | Out-Null
            $steps = [ordered]@{}
            $steps['start (AnimeJaNai, Auto)'] = TipFor 'on: Balanced' 30
            MenuPress 'Model: Adore';      $steps['Model: Adore'] = TipFor 'on: Adore' 30
            MenuPress 'Tier: Balanced';    $steps['Tier: Balanced'] = TipFor 'on: Balanced' 30
            MenuPress 'Tier: Adore';       $steps['Tier: Adore'] = TipFor 'on: Adore' 30
            MenuPress 'Model: AnimeJaNai'; $steps['Model: AnimeJaNai (tier Adore left over: Auto)'] = TipFor 'on: Balanced' 30
            $upscaled = (QN 'video-out-params/w') -ge 2 * $w - 1
            $saved = Get-Content (Join-Path $DataDir "settings.json") -Raw | ConvertFrom-Json
            $ok = @($steps.Values | Where-Object { $_ -lt 0 }).Count -eq 0 -and $upscaled -and $saved.UpscaleModel -eq 'animejanai'
            Report 'adore-switch' $ok ((($steps.GetEnumerator() | ForEach-Object { "$($_.Key) $($_.Value) s" }) -join '; ') + "; still upscaled: $upscaled; saved model: $($saved.UpscaleModel)")
            Stop-Haku
        }
    }

    if (Want 'adore-stale') {
        # a tier saved for the other model (after switching back and forth) falls back to Auto instead of breaking
        if (-not $hasH264) { Skip 'adore-stale' "missing file" } else {
            Start-Haku $Media.H264 @{ UpscaleModel = 'animejanai'; UpscaleTier = 'adore' } | Out-Null
            $a = TipFor 'on: Balanced' 30; $tipA = ((AiTip) -split "`n")[0]; Stop-Haku
            Start-Haku $Media.H264 @{ UpscaleModel = 'adore'; UpscaleTier = 'quality' } | Out-Null
            $b = TipFor 'on: Adore' 60; $tipB = ((AiTip) -split "`n")[0]; Stop-Haku
            Report 'adore-stale' ($a -ge 0 -and $b -ge 0) "AnimeJaNai + tier 'adore': '$tipA'; Adore + tier 'quality': '$tipB'"
        }
    }

    if (Want 'adore-settings') {
        # the settings page follows the model: Adore's tiers and table row, AnimeJaNai's Quality and Look
        Start-Haku $null @{ UpscaleModel = 'adore'; UpscaleTier = 'auto' } | Out-Null
        WaitFor { Find 'App settings' } 20 | Out-Null
        Press 'App settings'; Start-Sleep 1; Press 'Settings: AI upscaling'; Start-Sleep 1.5
        $adoreChips = [bool](Find 'Speed and quality: Adore') -and -not (Find 'Speed and quality: Quality') -and [bool](Find 'Anime model: Adore')
        $adoreNote = [bool](Find 'Adore by renarchi, made for 1080p anime: a little slower than Balanced. When your card can''t keep up with a video, AnimeJaNai''s faster tiers take over. Downloaded (3 MB) from renarchi''s GitHub the first time.')
        Press 'Anime model: AnimeJaNai'; Start-Sleep 1.5
        $janaiChips = [bool](Find 'Speed and quality: Quality') -and -not (Find 'Speed and quality: Adore')
        $saved = (Get-Content (Join-Path $DataDir "settings.json") -Raw | ConvertFrom-Json).UpscaleModel
        Stop-Haku
        # with AI upscaling switched off the model can still be picked (it had been hidden)
        Start-Haku $null @{ Upscale = $false; UpscaleModel = 'animejanai' } | Out-Null
        WaitFor { Find 'App settings' } 20 | Out-Null
        Press 'App settings'; Start-Sleep 1; Press 'Settings: AI upscaling'; Start-Sleep 1.5
        $offShown = [bool](Find 'Anime model: Adore')
        if ($offShown) { Press 'Anime model: Adore'; Start-Sleep 1 }
        $offSaved = (Get-Content (Join-Path $DataDir "settings.json") -Raw | ConvertFrom-Json)
        Report 'adore-settings' ($adoreChips -and $adoreNote -and $janaiChips -and $saved -eq 'animejanai' -and $offShown -and $offSaved.UpscaleModel -eq 'adore' -and -not $offSaved.Upscale) "Adore: its tiers shown $adoreChips, its note $adoreNote; switched to AnimeJaNai: its tiers shown $janaiChips, saved '$saved'; AI off: model choice shown $offShown, picked Adore saved '$($offSaved.UpscaleModel)', still off: $(-not $offSaved.Upscale)"
        Stop-Haku
    }

    if (Want 'rename') {
        # a show renamed in the app: the library shows the new name, the folder on disk keeps its own
        $show = if ($Media.AnimeLibrary) { Get-ChildItem -LiteralPath $Media.AnimeLibrary -Directory -ErrorAction SilentlyContinue | Sort-Object Name | Select-Object -First 1 }
        if (-not $show) { Skip 'rename' "missing library" } else {
            Start-Haku $null @{ Libs = @(@{ Id = 't'; Name = 'Anime'; Folders = @(@{ Path = $Media.AnimeLibrary; Kind = 'anime' }); Home = $true }); Renames = @{ $show.FullName = 'Renamed By Test' } } | Out-Null
            WaitFor { Find 'Anime' } 20 | Out-Null
            Press 'Anime'
            $shown = WaitFor { Find 'Renamed By Test' } 10
            Report 'rename' ($shown -ge 0 -and (Test-Path -LiteralPath $show.FullName)) "'$($show.Name)' shows as 'Renamed By Test' after $shown s; folder still '$($show.Name)': $(Test-Path -LiteralPath $show.FullName)"
            Stop-Haku
        }
    }
}
finally { Stop-Haku }

# ---------- the whole run ----------

$stuck = if (Test-Path $TraceFile) { @(Get-Content $TraceFile) } else { @() }
Report 'no-ui-stalls' ($stuck.Count -eq 0) $(if ($stuck.Count) { $stuck -join ' | ' } else { "the UI thread never waited 3 s or more in a draw" })
$hangs = Hangs
Report 'no-hangs' ($hangs.Count -eq 0) "$($hangs.Count) 'not responding' events from the test copy"

$Results | ConvertTo-Json | Set-Content (Join-Path $Out "results.json") -Encoding utf8
$failed = @($Results | Where-Object Result -eq 'FAIL').Count
Write-Host ""; Write-Host "$(@($Results | Where-Object Result -eq 'pass').Count) passed, $failed failed, $(@($Results | Where-Object Result -eq 'skipped').Count) skipped"
exit $failed
