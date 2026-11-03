# Haku Player end-to-end tests: AI upscaling, its buffer, seeking, pausing, the AI button, subtitles, show details.
# Builds a test copy into $outW and runs it with its own data folder (HAKU_DATA_DIR), so your real settings and history are
# never touched. Each test drives the app through mpv's JSON pipe and Windows UI Automation (never the real mouse).
#
#   powershell -ExecutionPolicy Bypass -File tests\app-tests.ps1            (all tests; a few minutes, plays sound muted)
#   ... -Only startup,seek -NoBuild
#
# The test videos are files from the author's library: change $Media to files you have. A test whose file is missing is
# skipped. Results: printed, and in $outW\results.json.
param(
    [string]$Out = (Join-Path $env:TEMP "haku-tests"),
    [string[]]$Only = @(),
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path $PSScriptRoot -Parent
$AppDir = Join-Path $Out "app"
$DataDir = Join-Path $Out "data"
$TraceFile = Join-Path $Out "trace.txt"

$Media = @{
    H264   = 'D:\TV Shows\South Park Season 1 to 26 Mp4 1080p\Season 02\South Park S02E01.mp4'           # 1920x1080 H.264 23.976
    Hevc10 = 'A:\Anime\Naruto Complete\Season 01\[Anime Time] Naruto - 001 - Enter Naruto Uzumaki!.mkv'  # 1432x1080 HEVC 10-bit 29.97
    UHD    = 'A:\Anime Upscaled\Anime Openings\Angel Beats!\AngelBeats-OP1-Encoded.24.000fps.cugan-pro-no-denoise-up2x.fp16.082be5.mp4' # 3840x2160
    Subs   = 'A:\Anime\Neon Genesis Evangelion Complete Series\[Anime Time] Neon Genesis Evangelion - 01.mkv' # default track: Signs/Songs
    AnimeLibrary = 'A:\Anime'
}

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
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

function Start-Haku([string]$file, [hashtable]$settings = @{}) {
    if (Test-Path $DataDir) { Remove-Item $DataDir -Recurse -Force }
    New-Item -ItemType Directory $DataDir | Out-Null
    $conf = [ordered]@{ SettingsVersion = 3; Upscale = $true; UpscaleTier = 'auto'; Resume = $false; Autoplay = $true; AutoPopOut = $false }
    foreach ($k in $settings.Keys) { $conf[$k] = $settings[$k] }
    $conf | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $DataDir "settings.json") -Encoding utf8
    $env:HAKU_DATA_DIR = $DataDir; $env:HAKU_TRACE = $TraceFile
    try { $proc = if ($file) { Start-Process (Join-Path $AppDir "HakuPlayer.exe") -ArgumentList "`"$file`"" -PassThru } else { Start-Process (Join-Path $AppDir "HakuPlayer.exe") -PassThru } }
    finally { Remove-Item Env:HAKU_DATA_DIR, Env:HAKU_TRACE -ErrorAction SilentlyContinue }
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
        if (-not (Test-Path -LiteralPath $Media.H264)) { Skip 'startup' "missing $($Media.H264)" } else {
            Start-Haku $Media.H264 | Out-Null
            $w = 0; WaitFor { ($script:w = QN 'width') -gt 0 } 20 | Out-Null
            $pausedFirst = (Q 'pause') -eq 'yes'
            $sawLoading = $false; $maxReady = 0
            $resumed = WaitFor { if (Loading) { $script:sawLoading = $true }; $r = QN 'vf-metadata/hakusr/frames'; if ($r -gt $script:maxReady) { $script:maxReady = $r }; (Q 'pause') -eq 'no' } 20
            $outW = QN 'video-out-params/w'
            $watch = Watch 10
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
        if (-not (Test-Path -LiteralPath $Media.H264)) { Skip 'paused-enable' "missing file" } else {
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
        if (-not (Test-Path -LiteralPath $Media.Hevc10)) { Skip 'hevc10' "missing file" } else {
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
        if (-not (Test-Path -LiteralPath $Media.UHD)) { Skip 'uhd' "missing file" } else {
            Start-Haku $Media.UHD | Out-Null
            WaitFor { (QN 'width') -gt 0 } 20 | Out-Null; Start-Sleep 3
            $outW = QN 'video-out-params/w'; $tip = AiTip; $playing = (Q 'pause') -eq 'no'
            Report 'uhd' ($outW -eq 3840 -and $tip -match 'bigger than' -and $playing) "4K plays as it is ($outW px, playing: $playing): '$tip'"
            Stop-Haku
        }
    }

    if ((Want 'subtitles') -or (Want 'next-episode')) {
        if (-not (Test-Path -LiteralPath $Media.Subs)) { Skip 'subtitles' "missing file" } else {
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
        if (-not (Test-Path -LiteralPath $Media.H264)) { Skip 'own-subtitles' "missing file" } else {
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

    if (Want 'leave-while-starting') {
        if (-not (Test-Path -LiteralPath $Media.H264)) { Skip 'leave-while-starting' "missing file" } else {
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

    if (Want 'show-details') {
        if (-not (Test-Path -LiteralPath $Media.AnimeLibrary)) { Skip 'show-details' "missing library" } else {
            Start-Haku $null @{ Libs = @(@{ Id = 't'; Name = 'Anime'; Folders = @(@{ Path = $Media.AnimeLibrary; Kind = 'anime' }); Home = $true }) } | Out-Null
            $want = @{ 'Naruto_Complete' = 'Naruto'; 'Naruto_Shippuden_Complete' = 'Naruto: Shippuden'; 'Neon_Genesis_Evangelion_Complete_Series' = 'Neon Genesis Evangelion'; 'Monster_480p' = 'Monster'; 'Monster_2004_1080p' = 'Monster' }
            $info = Join-Path $DataDir "info"
            WaitFor { @($want.Keys | Where-Object { Test-Path (Join-Path $info "$_.json") }).Count -eq $want.Count } 120 | Out-Null
            $got = foreach ($k in $want.Keys) { $f = Join-Path $info "$k.json"; if (Test-Path $f) { "$k=$((Get-Content $f -Raw | ConvertFrom-Json).Title)" } else { "$k=(none)" } }
            $bad = @($want.Keys | Where-Object { $f = Join-Path $info "$_.json"; -not (Test-Path $f) -or (Get-Content $f -Raw | ConvertFrom-Json).Title -ne $want[$_] })
            Report 'show-details' ($bad.Count -eq 0) ($got -join '; ')
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
