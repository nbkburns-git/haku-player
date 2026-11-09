using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.System;
using Windows.UI;

namespace AnimePlayer;

// The WinUI window (phase 2: the player). Plays the videos it's given (command line, or dropped files and folders) as
// one playlist; the library comes in phase 3. mpv draws into VideoView, the glass pieces sit on top (pill, top bar, skip
// card: MainWindow.Player.cs), keys go to mpv as in the WPF app.
// HDR: on a monitor with Windows HDR on, mpv outputs HDR10 (PQ / BT.2020) at the monitor's peak, and SDR videos are put at
// Windows' SDR brightness (glass.glsl gwhite).
public partial class MainWindow : Window
{
    readonly Grid root = new();
    readonly Border tint = new() { Background = new SolidColorBrush(Color.FromArgb(0xB8, 0x0A, 0x0B, 0x10)) }; // over the acrylic, behind the pages
    readonly CursorGrid videoHost = new(); // a Grid whose cursor can be set (the up/down arrow over the subtitles)
    readonly TextBlock idle = new()
    {
        Text = "Drop a video here", FontSize = 20, Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };
    readonly PlayerBar bar = new();
    readonly IntPtr hwnd;
    // HAKU_HDR=off: always SDR output. HAKU_HDR=force (tests): HDR10 output even on an SDR monitor (Windows maps it down)
    static readonly string HdrMode = (Environment.GetEnvironmentVariable("HAKU_HDR") ?? "auto").ToLowerInvariant();
    static bool WantHdr(DisplayInfo? d) => HdrMode == "force" || (HdrMode != "off" && d?.Hdr == true);

    MpvCore? core;
    bool libraryLoaded;
    (List<string> Files, int Index)? pendingPlay;
    VideoView? video;
    MpvIpc? ipc;
    int session;
    double duration, videoAspect, volume = LoadVolume();
    bool paused, fullscreen, autoPop;
    string gamma = "";
    DisplayInfo? display;

    public MainWindow()
    {
        Title = "Haku Player";
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        HookSizing(); // dragging an edge while playing keeps the video's shape (Window.WinUI.cs)
        ExtendsContentIntoTitleBar = true;
        // no title bar of Windows' own: the header (and the player's top bar) have the window buttons, as the WPF app
        if (AppWindow.Presenter is OverlappedPresenter op) op.SetBorderAndTitleBar(true, false);
        AppWindow.Changed += (_, e) => { if (e.DidPresenterChange || e.DidSizeChange) ShowMaximized(); };
        Activated += (_, _) => UpdateBlobs();
        try { AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "haku.ico")); } catch { }

        ThemeW.Apply(Settings.Current.Theme); // accent colours (Theme.WinUI.cs)
        // the library pages (Library.WinUI.cs) over Windows' acrylic, the player over them while playing
        SystemBackdrop = new ThinAcrylicBackdrop(); // Backdrop.WinUI.cs
        root.Children.Add(tint);
        SetupBackdrop(); // drifting colour blobs, the cover's glow, the bottom shade (Backdrop.WinUI.cs)
        SetupLibraryParts();
        root.Children.Add(pages);
        videoHost.Background = new SolidColorBrush(Colors.Black);
        videoHost.Visibility = Visibility.Collapsed;
        root.Children.Add(videoHost);
        idle.Visibility = Visibility.Collapsed;
        root.Children.Add(idle);
        root.Children.Add(bar.Pill);
        SetupPlayerParts();
        SetupUpscaleParts();
        SetupSettings();
        SetupPlayerExtras(); // PlayerExtras.WinUI.cs
        SetupFix();          // "Wrong show info?" (Popups.WinUI.cs)
        SetupSplash();       // the loading screen, over everything until the library is in (Splash.WinUI.cs)
        Content = root;
        SetTitleBar(headerDrag);
        bar.Pill.Visibility = Visibility.Collapsed;

        bar.PlayPause = () => Send("cycle", "pause");
        bar.ToggleMute = () => Send("cycle", "mute");
        bar.ToggleFullscreen = () => Send("cycle", "fullscreen");
        bar.Volume = v => { volume = v; Send("set_property", "volume", v); };
        bar.Seek = (f, final) =>
        {
            if (duration <= 0) return;
            SeekTo(f * duration, final ? "absolute+exact" : "absolute+keyframes");
            if (final) up?.TimelineSeekEnd();
        };
        bar.Pill.SizeChanged += (_, _) => SendGlass();
        videoHost.SizeChanged += (_, _) => SendGlass();

        // keys go to mpv (its own bindings), as in the WPF app
        root.KeyDown += OnKey;
        root.CharacterReceived += OnChar;
        // only real movement: Windows re-sends a move when things change under a still pointer (as the WPF app filters it)
        root.PointerMoved += (_, _) => { if (PointerReallyMoved() && core != null) { if (cursorHidden) MpvCore.Note("pointer moved: showing the bars"); Activity(); } };
        // keyboard focus for the keys above, only once the window is really activated: focusing content in a window shown
        // without activation (tests on the second monitor) brought it to the front
        Activated += (_, e) => { if (e.WindowActivationState != WindowActivationState.Deactivated) root.Focus(FocusState.Programmatic); };
        root.IsTabStop = true;

        // drop a video to play it
        root.AllowDrop = true;
        root.DragOver += (_, e) => { if (e.DataView.Contains(StandardDataFormats.StorageItems)) e.AcceptedOperation = DataPackageOperation.Copy; };
        root.DragEnter += (_, e) => { if (e.DataView.Contains(StandardDataFormats.StorageItems)) Toast("Drop to play"); };
        root.Drop += async (_, e) =>
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            var files = VideosOf(items.Select(i => i.Path));
            if (files.Count == 0) { Toast("No videos there that Haku Player can play"); return; }
            if (TestFocus.Rect == null) Activate();
            Play(files, 0);
        };

        // the monitor may change (moved, or HDR switched in Windows): check when the window moves or resizes
        AppWindow.Changed += (_, e) => { if (e.DidPositionChange || e.DidSizeChange) CheckDisplay(); };
        Closed += async (_, e) =>
        {
            if (core == null) return;
            e.Handled = true; // let mpv save its resume point first
            SaveVolume();
            await StopVideo(closing: true);
            Close();
        };
        // the library scan starts now, while the window is still being made (as the WPF app)
        var scan = Task.Run(ScanAll);
        root.Loaded += async (_, _) => await LoadLibrary(scan);
        root.Loaded += (_, _) => ListenForOpens(); // videos opened while Haku runs
        StartTestHooks(); // test runs only (TestHooks.cs)
        Watch.Changed += () => DispatcherQueue.TryEnqueue(RefreshWatchViews);
        hideTimer = DispatcherQueue.CreateTimer();
        hideTimer.Interval = TimeSpan.FromSeconds(Settings.Current.HideAfter);
        hideTimer.Tick += (_, _) => { hideTimer.Stop(); if (!paused && core != null && openMenu == null && !overBars && subDragFrom == null) ShowBar(false); };
    }

    // Tests and measuring: HAKU_TEST_WINDOW="x,y,w,h" (screen pixels) puts the window there and shows it without
    // taking focus, so it never comes up over a game.
    public void ShowQuietly()
    {
        // test runs: TestFocus has already made the window right where HAKU_TEST_WINDOW says (in the test monitor's
        // pixels, so there's no move between monitors with different scaling); show it without activating it
        if (TestFocus.Rect != null) AppWindow.Show(false);
        else Activate();
        display = DisplayInfo.For(hwnd);
        MpvCore.Note($"window shown on {display?.Name}: {display}");
    }

    // ---------- playing ----------

    public void Play(string file) => Play(VideosOf(new[] { file }), 0);

    // Files from outside (command line, dropped): one that's in a library plays as that show's episode (names, next
    // episode, progress); anything else as its own little playlist.
    public void Play(List<string> files, int index)
    {
        if (files.Count == 0) return;
        if (!libraryLoaded && files.Count == 1) { pendingPlay = (files, index); return; } // LoadLibrary plays it
        if (view != videoHost && view != null) returnTo = view;
        var same = StringComparer.OrdinalIgnoreCase;
        if (files.Count == 1 && Everything(lib).FirstOrDefault(x => !x.IsRandom && x.Videos.Any(v => same.Equals(v.Path, files[0]))) is { } known)
        {
            PlayEntry(known, known.Videos.FindIndex(v => same.Equals(v.Path, files[0])));
            return;
        }
        PlayEntry(EntryFor(files), index);
    }

    async void PlayEntry(Entry what, int index)
    {
        if (what.Videos.Count == 0) return;
        await StopVideo(leaving: false);
        await lastStop; // a video left a moment ago may still be shutting down
        int my = ++session;
        entry = what;
        var files = what.Videos.Select(v => v.Path).ToList();
        Directory.CreateDirectory(Settings.DataDir);
        var m3u = Path.Combine(Settings.DataDir, "playlist-winui.m3u8");
        File.WriteAllLines(m3u, files.Prepend("#EXTM3U"), new UTF8Encoding(false));
        var file = files[index];
        var pipe = $"animeplayer-{Environment.ProcessId}-{my}";
        display = DisplayInfo.For(hwnd);
        bool hdr = WantHdr(display);
        // AI buffer: start paused, and the upscaling resumes once the AI is running (or straight away if it won't upscale)
        bool aiStart = Settings.Current.Upscale && Upscaler.Installed;
        autoPop = Settings.Current.AutoPopOut;
        var args = PlayerArgs.Build(pipe, m3u, index, entry.Unit, entry.Kind, volume, aiStart).Concat(TargetArgs(hdr)).ToArray();
        gamma = ""; duration = 0; videoAspect = 0;
        try
        {
            var c = new MpvCore(args, deferLoad: true);
            core = c;
            video = new VideoView(c, hdr); // its swap chain first, then the file (MpvCore.Load)
            videoHost.Children.Add(video.Panel);
            c.Shutdown += () => DispatcherQueue.TryEnqueue(() => { if (core == c) _ = StopVideo(); });
            c.Load();
            ipc = new MpvIpc();
            ipc.Property += OnProperty;
            ipc.Event += ev => { if (ev == "playback-restart") up?.PlaybackRestart(); else if (ev == "file-loaded") FileLoaded(); };
            NewUpscaleSession();
            qualityHeight = 0; qualityCodec = ""; upscaleHeight = 0; topBar.SetBadge("");
            bar.SetAutoplay(Settings.Current.Autoplay);
            bar.SetAiButton(Settings.Current.Upscale, false, Settings.Current.Upscale ? "AI upscaling: waiting for the video (Ctrl+U)" : "AI upscaling: off (Ctrl+U)");
            if (aiStart) up!.Hold(); // mpv is already paused; this arms the hold and its time limit
            _ = ipc.Run(pipe, new[] { "pause", "time-pos", "duration", "volume", "mute", "video-params/aspect", "video-params/gamma", "fullscreen",
                "demuxer-cache-time", "playlist-pos", "chapter-list", "track-list", "speed", "width", "height", "container-fps", "frame-drop-count",
                "sub-delay", "sub-text", "video-format" });
            Show(videoHost);
            videoHost.Visibility = Visibility.Visible;
            bar.Pill.Visibility = Visibility.Visible;
            topBar.Panel.Visibility = Visibility.Visible;
            bar.SetVolume(volume);
            ShowBar(true);
            SendGlass();
            ApplyDebug();
            MpvCore.Note($"playing ({(hdr ? $"HDR10, peak {display!.PeakNits:0} nits, SDR white {display.SdrWhiteNits:0}" : "SDR")}): {file}");
        }
        catch (Exception ex)
        {
            MpvCore.Note("couldn't play: " + ex);
            idle.Text = $"Couldn't play that video: {ex.Message}";
            await StopVideo();
        }
    }

    // mpv's output: HDR10 at the monitor's peak, or its defaults (SDR, as the WPF app)
    IEnumerable<string> TargetArgs(bool hdr) => hdr
        ? new[] { "--target-trc=pq", "--target-prim=bt.2020", $"--target-peak={Math.Max(203, display?.PeakNits ?? 1000).ToString("0", CultureInfo.InvariantCulture)}" }
        : Array.Empty<string>();

    // Leaves the player right away (as the WPF app): the page comes back at once, and mpv saves the resume point and shuts
    // down (~1 s) in the background. The task ends once mpv has gone. closing: the window is closing, keep the video up.
    async Task StopVideo(bool leaving = true, bool closing = false)
    {
        var c = core; var v = video; var i = ipc;
        if (c == null) return;
        core = null; video = null; ipc = null;
        var done = new TaskCompletionSource();
        lastStop = done.Task; // the next video waits for this one to have saved its resume point
        thumbs?.Dispose(); thumbs = null;
        if (!closing)
        {
            entry = null; playingIndex = -1; durationFor = -1; duration = 0; tracks = null; up = null;
            aiLoading.Show(null, 0, 0, null);
            ApplyDebug(); currentFile = ""; loadedFor = null; trackList = new();
            HideCursor(false); videoHost.SetCursor(null); lastFill = null;
            CloseMenu(); ResetPrompt();
            videoHost.Visibility = Visibility.Collapsed;
            bar.Pill.Visibility = Visibility.Collapsed;
            bar.Preview.Visibility = Visibility.Collapsed;
            topBar.Panel.Visibility = Visibility.Collapsed;
            Title = "Haku Player";
            if (fullscreen) SetFullscreen(false);
            if (leaving) { UnpopNow(); RestoreBrowsingSize(); Discord.Clear(); Show(returnTo); } // back to the page you played it from
        }
        try
        {
            if (!c.Dead)
            {
                i?.Send(new object?[] { "quit" });
                // mpv saves the resume point and shuts down; then its render context can go (Stopped only comes after Free)
                if (await Task.WhenAny(c.ShutDown, Task.Delay(3000)) != c.ShutDown && !c.Dead) { c.Kill(); await Task.WhenAny(c.ShutDown, Task.Delay(1000)); }
            }
            v?.Dispose();
            if (v != null) videoHost.Children.Remove(v.Panel);
            c.Free();
            Watch.Save();
            await Task.WhenAny(c.Stopped, Task.Delay(3000));
        }
        finally { done.TrySetResult(); } // never leaves the next video waiting
    }
    Task lastStop = Task.CompletedTask;

    void Send(params object?[] command) => ipc?.Send(command);

    void OnProperty(string name, JsonElement d)
    {
        switch (name)
        {
            case "pause": paused = d.ValueKind == JsonValueKind.True; bar.SetPause(paused); up?.PauseChanged(paused); DiscordUpdate(); if (paused) ShowBar(true); else Activity(); break;
            case "width" when d.ValueKind == JsonValueKind.Number: if (up != null) { up.Width = d.GetInt32(); up.Update(session); } break;
            case "height" when d.ValueKind == JsonValueKind.Number: qualityHeight = d.GetInt32(); UpdateQuality(); if (up != null) { up.Height = qualityHeight; up.Update(session); } break;
            case "container-fps" when d.ValueKind == JsonValueKind.Number: if (up != null) up.Fps = d.GetDouble(); break;
            case "frame-drop-count" when d.ValueKind == JsonValueKind.Number: up?.CheckDrops(d.GetInt64(), session); break;
            case "time-pos" when d.ValueKind == JsonValueKind.Number: PositionChanged(d.GetDouble()); break;
            case "duration" when d.ValueKind == JsonValueKind.Number: duration = d.GetDouble(); durationFor = playingIndex; bar.SetDuration(duration); DiscordUpdate(); _ = FindSegments(duration, session); break;
            case "playlist-pos" when d.ValueKind == JsonValueKind.Number: FileChanged(d.GetInt32()); break;
            case "chapter-list" when d.ValueKind == JsonValueKind.Array:
                chapters = d.EnumerateArray()
                    .Select(c => new Chapter(c.TryGetProperty("time", out var tm) && tm.ValueKind == JsonValueKind.Number ? tm.GetDouble() : 0, Str(c, "title")))
                    .OrderBy(c => c.Time).ToList();
                bar.SetMarks(segments, chapters);
                // chapters can come after the openings were looked for: they're the best source, so look again
                if (chapters.Count > 0 && segmentsFor != null && duration > 0) { segmentsFor = null; _ = FindSegments(duration, session); }
                break;
            case "track-list" when d.ValueKind == JsonValueKind.Array: tracks = d.Clone(); SetTracks(d); break;
            case "sub-delay" when d.ValueKind == JsonValueKind.Number: SetSubDelay(d.GetDouble()); break;
            case "sub-text": subShowing = d.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(d.GetString()); break;
            case "video-format" when d.ValueKind == JsonValueKind.String: qualityCodec = d.GetString() ?? ""; UpdateQuality(); break;
            case "speed" when d.ValueKind == JsonValueKind.Number: speed = d.GetDouble(); bar.SetSpeed(speed); if (openMenu == "speed") BuildMenu(); break;
            case "demuxer-cache-time" when d.ValueKind == JsonValueKind.Number: bar.SetCache(d.GetDouble()); break;
            case "volume" when d.ValueKind == JsonValueKind.Number: volume = d.GetDouble(); bar.SetVolume(volume); break;
            case "mute": bar.SetMute(d.ValueKind == JsonValueKind.True); break;
            case "fullscreen": SetFullscreen(d.ValueKind == JsonValueKind.True); break;
            case "video-params/aspect" when d.ValueKind == JsonValueKind.Number:
                videoAspect = d.GetDouble();
                if (autoPop) { autoPop = false; if (!popped && !fullscreen) { TogglePopOut(); SendGlass(); break; } }
                FitWindowToVideo();
                lastFill = null; UpdateFill();
                SendGlass();
                break;
            case "video-params/gamma" when d.ValueKind == JsonValueKind.String:
                gamma = d.GetString() ?? "";
                MpvCore.Note($"video gamma: {gamma}");
                SendGlass(); // HDR video keeps its own brightness; SDR video goes to Windows' SDR white
                break;
        }
    }

    // ---------- HDR: follow the monitor ----------

    void CheckDisplay()
    {
        var d = DisplayInfo.For(hwnd);
        if (d == null) return;
        bool changed = display == null || d.Name != display.Name || d.Hdr != display.Hdr || d.SdrWhiteNits != display.SdrWhiteNits || d.PeakNits != display.PeakNits;
        display = d;
        if (!changed || video == null) return;
        bool hdr = WantHdr(d);
        MpvCore.Note($"display now {d.Name}: {d} -> {(hdr ? "HDR10" : "SDR")} output");
        if (hdr != video.Hdr) video.SetHdr(hdr);
        if (hdr)
        {
            Send("set_property", "target-trc", "pq");
            Send("set_property", "target-prim", "bt.2020");
            Send("set_property", "target-peak", Math.Max(203, d.PeakNits).ToString("0", CultureInfo.InvariantCulture));
        }
        else
        {
            Send("set_property", "target-trc", "auto");
            Send("set_property", "target-prim", "auto");
            Send("set_property", "target-peak", "auto");
        }
        SendGlass();
    }

    // ---------- glass under the pill (glass.glsl) ----------

    readonly StringBuilder glassText = new(256);
    string lastGlass = "";
    bool glassBusy;

    void SendGlass()
    {
        if (ipc == null || video == null || videoHost.ActualWidth <= 0) return;
        double W = videoHost.ActualWidth, H = videoHost.ActualHeight;
        // positions are relative to the picture itself (a 4:3 video has bars at the sides)
        double pw = W, ph = H, px = 0, py = 0;
        // (zoomed to fill, UpdateFill: the picture is the whole area, as the WPF app's SetFill)
        if (videoAspect > 0 && lastFill != true) { pw = Math.Min(W, H * videoAspect); ph = pw / videoAspect; px = (W - pw) / 2; py = (H - ph) / 2; }
        var sb = glassText.Clear();
        void Add(char n, Border el)
        {
            double alpha = el.Visibility == Visibility.Visible ? el.Opacity : 0;
            if (alpha <= 0 || el.ActualWidth <= 0) { GlassParams.Append(sb, n, 0, 0, 0, 0, 0, 0); return; }
            var at = el.TransformToVisual(videoHost).TransformPoint(new Windows.Foundation.Point(0, 0));
            GlassParams.Append(sb, n, (at.X - px) / pw, (at.Y - py) / ph, el.ActualWidth / pw, el.ActualHeight / ph, el.CornerRadius.TopLeft / ph, alpha);
        }
        Add('a', bar.Pill);
        Add('b', topBar.Panel);
        Add('c', menuPanel);
        Add('d', prompt.Panel);
        bool hdr = video.Hdr;
        bool sdrVideo = gamma is not ("pq" or "hlg");
        double white = hdr && sdrVideo && display is { SdrWhiteNits: > 0 } dd ? dd.SdrWhiteNits / 203.0 : 1.0;
        sb.Append(CultureInfo.InvariantCulture, $"gblur={(Settings.Current.Glass switch { 0 => 9.0, 2 => 30.0, _ => 16.0 })},gpq={(hdr ? 1 : 0)},gwhite={white:0.####}");
        if (sb.Equals(lastGlass.AsSpan())) return;
        lastGlass = sb.ToString();
        _ = SendGlassNow();
    }

    // one update in flight at a time, the newest next (as the WPF app: queued updates made the blur lag behind)
    async Task SendGlassNow()
    {
        if (glassBusy) return;
        glassBusy = true;
        try
        {
            string sent;
            do { sent = lastGlass; if (ipc == null) break; await ipc.Ask(new object?[] { "set_property", "glsl-shader-opts", sent }); }
            while (sent != lastGlass);
        }
        finally { glassBusy = false; }
    }

    // ---------- the bar shows on activity, hides after 2 s of playing ----------

    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer hideTimer;
    double barTarget = 1;
    bool fading;

    void Activity()
    {
        if (core == null) return;
        ShowBar(true);
        hideTimer.Stop(); hideTimer.Start();
    }

    void ShowBar(bool on)
    {
        barTarget = on ? 1 : 0;
        HideCursor(!on && core != null);
        bar.Pill.IsHitTestVisible = on;
        topBar.Panel.IsHitTestVisible = on;
        if (!on) { bar.Preview.Visibility = Visibility.Collapsed; CloseMenu(); }
        if (fading) return;
        fading = true;
        CompositionTarget.Rendering += Fade;
    }

    // fades step by step, so the blur (mpv's) fades with the tint (ours)
    void Fade(object? s, object e)
    {
        double o = bar.Pill.Opacity, step = 1.0 / 9; // ~150 ms at 60 Hz
        o = barTarget > o ? Math.Min(barTarget, o + step) : Math.Max(barTarget, o - step);
        bar.Pill.Opacity = o;
        topBar.Panel.Opacity = o;
        SendGlass();
        if (o == barTarget) { CompositionTarget.Rendering -= Fade; fading = false; }
    }

    // ---------- fullscreen: mirrors mpv's flag (its "f" key, double-click, the button) ----------

    void SetFullscreen(bool on)
    {
        if (on == fullscreen) return;
        if (on && popped) UnpopNow();
        fullscreen = on;
        AppWindow.SetPresenter(on ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);
        topBar.SetFullscreen(on);
        bar.SetFullscreen(on);
    }

    // ---------- keys ----------

    // Keys as the WPF app (MainWindow.OnKey): Esc closes what's open; Esc / Backspace go back (in the player too, unless
    // fullscreen, where Esc is mpv's); in the player everything else goes to mpv, with Ctrl / Alt / Shift as mpv writes them.
    void OnKey(object sender, KeyRoutedEventArgs e)
    {
        var key = e.Key;
        if (FixOpen) { if (key == VirtualKey.Escape) { CloseFix(); e.Handled = true; } return; }
        if (Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root.XamlRoot) is TextBox box)
        {
            if (key == VirtualKey.Escape) { if (box == searchBox) searchBox.Text = ""; root.Focus(FocusState.Programmatic); e.Handled = true; }
            return;
        }
        if (SettingsOpen) { if (key == VirtualKey.Escape) { CloseSettings(); e.Handled = true; } return; }
        if (core == null || (!fullscreen && key is VirtualKey.Escape or VirtualKey.Back))
        {
            if (key is VirtualKey.Escape or VirtualKey.Back) { if (core != null) _ = StopVideo(); else GoBack(); e.Handled = true; }
            return;
        }
        bool Down(VirtualKey k) => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        bool ctrl = Down(VirtualKey.Control), alt = Down(VirtualKey.Menu), shift = Down(VirtualKey.Shift);
        if (ctrl && !alt && key == VirtualKey.U) { ToggleAi(); Activity(); e.Handled = true; return; } // AI upscaling on/off
        var name = MpvKey(key);
        if (name == null && (ctrl || alt) && key is >= VirtualKey.A and <= VirtualKey.Z) name = key.ToString().ToLowerInvariant();
        if (name == null) return; // printable: arrives in OnChar
        if (shift && MpvKey(key) != null) name = "Shift+" + name;
        if (alt) name = "Alt+" + name;
        if (ctrl) name = "Ctrl+" + name;
        Send("keypress", name);
        Activity();
        e.Handled = true;
    }

    void OnChar(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        if (core == null || e.Character < ' ' || e.Character == ' ') return; // control keys and space come through OnKey
        if (Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root.XamlRoot) is TextBox) return;
        bool Down(VirtualKey k) => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (Down(VirtualKey.Control) || Down(VirtualKey.Menu)) return; // sent with its modifiers by OnKey
        Send("keypress", e.Character == '#' ? "SHARP" : e.Character.ToString());
        Activity();
        e.Handled = true;
    }

    static string? MpvKey(VirtualKey k) => k switch
    {
        VirtualKey.Space => "SPACE", VirtualKey.Enter => "ENTER", VirtualKey.Escape => "ESC", VirtualKey.Tab => "TAB",
        VirtualKey.Back => "BS", VirtualKey.Delete => "DEL", VirtualKey.Insert => "INS",
        VirtualKey.Left => "LEFT", VirtualKey.Right => "RIGHT", VirtualKey.Up => "UP", VirtualKey.Down => "DOWN",
        VirtualKey.PageUp => "PGUP", VirtualKey.PageDown => "PGDWN", VirtualKey.Home => "HOME", VirtualKey.End => "END",
        >= VirtualKey.F1 and <= VirtualKey.F12 => k.ToString(),
        (VirtualKey)179 => "PLAYPAUSE", (VirtualKey)176 => "NEXT", (VirtualKey)177 => "PREV", (VirtualKey)178 => "STOP", // media keys
        _ => null,
    };

    // ---------- volume (app-wide, as the WPF app keeps it) ----------

    static readonly string VolumeFile = Path.Combine(Settings.DataDir, "volume.txt");
    static double LoadVolume()
    {
        try { return Math.Clamp(double.Parse(File.ReadAllText(VolumeFile), CultureInfo.InvariantCulture), 0, 100); }
        catch { return 100; }
    }
    void SaveVolume()
    {
        try { Directory.CreateDirectory(Settings.DataDir); File.WriteAllText(VolumeFile, volume.ToString(CultureInfo.InvariantCulture)); } catch { }
    }
}
