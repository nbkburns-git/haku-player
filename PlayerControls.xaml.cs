using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace AnimePlayer;

// Floating glass control bars over the video, in the player's own window (mpv draws into that window too, MpvVideo.cs).
// Talks to mpv through Send.
public partial class PlayerControls : UserControl
{
    public Window? Owner;
    public event EventHandler? Closed;
    // done with this video: off the screen, timers stopped
    public void Close()
    {
        hideTimer.Stop(); // else it fires after we're gone and hides the cursor over the next video
        (Parent as Panel)?.Children.Remove(this);
        Closed?.Invoke(this, EventArgs.Empty);
    }

    public Action<object[]> Send = _ => { };
    public Action<string> Notify = _ => { }; // the app's glass toast
    public bool ResumeAllowed = true; // false for openings/endings: they always start from the top
    public event Action<bool>? ShownChanged; // main window hides the cursor with the bar
    public Action Back = () => { }, ActivateOwner = () => { }, Minimize = () => { }, ToggleMaximize = () => { }, CloseApp = () => { }, DragWindow = () => { };

    double duration, time, hostHeight;
    double volume = 100, aspect = 16.0 / 9, speed = 1;
    bool fill; // video zoomed to fill the host (mpv panscan), so it overhangs instead of letterboxing
    string? openMenu; // "tracks" / "speed" / null
    List<(long Id, string Type, string Lang, string Label, bool Selected, string Title, bool Forced, bool Default)> tracks = new();
    static Settings S => Settings.Current;
    bool paused, dragging, volDragging, muted, overTimeline, shown = true;
    int frostSecond = -1, previewSeq;
    Thumbs? thumbs;
    readonly GlassSurface pillGlass, topGlass, menuGlass, previewGlass;
    GlassFrame? lastFrame;
    readonly DispatcherTimer hideTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

    public PlayerControls()
    {
        InitializeComponent();
        ShowAutoplay();
        debugTimer.Tick += (_, _) => _ = RefreshDebug();
        Closed += (_, _) => debugTimer.Stop();
        Loaded += (_, _) => ApplyDebug();
        hideTimer.Interval = TimeSpan.FromSeconds(S.HideAfter);
        hideTimer.Tick += (_, _) => { if (!paused && !dragging && !volDragging && openMenu == null && !Pill.IsMouseOver && !TopBar.IsMouseOver) SetShown(false); };
        pillGlass = new GlassSurface(Pill, 34);
        topGlass = new GlassSurface(TopBar, 28);
        menuGlass = new GlassSurface(Menu, 18);
        previewGlass = new GlassSurface(Preview, 16);
        previewGlass.Live = LiveGlass;
        if (LiveGlass) { pillGlass.Live = topGlass.Live = menuGlass.Live = true; }
        // tell mpv's glass shader where the bars are, every frame while anything could be moving (see WatchGlass)
        Closed += (_, _) => { if (watchingGlass) System.Windows.Media.CompositionTarget.Rendering -= OnGlassFrame; watchingGlass = false; };
        SizeChanged += (_, _) => { WatchGlass(); CloseMenu(); }; // e.g. popping out: the menu would no longer fit where it was
        Pill.SizeChanged += (_, _) => WatchGlass();
        TopBar.SizeChanged += (_, _) => WatchGlass();
        Menu.SizeChanged += (_, _) => WatchGlass();
        WatchGlass();
        mouseWatch.Tick += (_, _) =>
        {
            var pos = System.Windows.Forms.Cursor.Position;
            if (pos == lastPos) return;
            lastPos = pos;
            if (!IsVisible || PresentationSource.FromVisual(this) == null) return;
            var a = PointToScreen(new Point(0, 0)); var b = PointToScreen(new Point(ActualWidth, ActualHeight));
            if (pos.X >= a.X && pos.X < b.X && pos.Y >= a.Y && pos.Y < b.Y) Activity();
        };
        mouseWatch.Start();
        // If Windows takes the mouse away mid-drag (e.g. the bar hides or another window grabs it), don't stay "dragging".
        Timeline.LostMouseCapture += (_, _) => { if (dragging) { dragging = false; if (!overTimeline) HidePreview(); } };
        VolTrack.LostMouseCapture += (_, _) => volDragging = false;
        Closed += (_, _) => mouseWatch.Stop();
        Pill.SizeChanged += (_, _) =>
        {
            // compact (pop-out) size: drop the volume slider, keep the speaker button
            var v = Pill.ActualWidth < 880 ? Visibility.Collapsed : Visibility.Visible;
            VolTrack.Visibility = v; VolText.Visibility = v;
        };
        Timeline.SizeChanged += (_, _) => { Redraw(); DrawMarks(); };
        Pill.SizeChanged += (_, _) => UpdateFrostViewbox();
        TopBar.SizeChanged += (_, _) => UpdateFrostViewbox();
        SizeChanged += (_, _) => UpdateFrostViewbox();
        VolTrack.SizeChanged += (_, _) => RedrawVolume();
        Closed += (_, _) => thumbs?.Dispose();
        Activity();
    }

    public static bool LiveGlass => System.IO.File.Exists(MainWindow.GlassShader);
    string lastGlass = "";

    // Following the bars every frame kept WPF ticking at the monitor's refresh rate for the whole video.
    // Now it only runs for a moment after something that can move them (fade, menu, resize, mouse activity).
    bool watchingGlass;
    DateTime watchGlassUntil;
    void WatchGlass(int ms = 600)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        if (until > watchGlassUntil) watchGlassUntil = until;
        if (watchingGlass) return;
        watchingGlass = true;
        System.Windows.Media.CompositionTarget.Rendering += OnGlassFrame;
    }

    void OnGlassFrame(object? s, EventArgs e)
    {
        if (DateTime.UtcNow > watchGlassUntil) { System.Windows.Media.CompositionTarget.Rendering -= OnGlassFrame; watchingGlass = false; }
        PushGlass(null, EventArgs.Empty);
    }

    // One more push a moment after a fade or menu change, in case mpv was mid-frame when the last one arrived.
    async void SettleGlass()
    {
        await Task.Delay(120);
        lastGlass = ""; // force a resend even if nothing changed
        PushGlass(null, EventArgs.Empty);
    }

    void PushGlass(object? s, EventArgs e)
    {
        if (!LiveGlass || Root.ActualWidth <= 0) return;
        double W = Root.ActualWidth, H = Root.ActualHeight, k = glassOff ? 0 : Root.Opacity;
        // mpv's shader only covers the picture itself, not the side/top bars around a 4:3 video,
        // so positions are given relative to the picture (else the glass is squashed into it).
        double vw = W, vh = H, vx = 0, vy = 0;
        if (!fill && aspect > 0) { vw = Math.Min(W, H * aspect); vh = vw / aspect; vx = (W - vw) / 2; vy = (H - vh) / 2; }
        var sb = new System.Text.StringBuilder();
        void Add(string n, FrameworkElement el, double radius, double alpha)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            if (el.Visibility != Visibility.Visible || el.ActualWidth <= 0) alpha = 0;
            var p = alpha > 0 ? el.TranslatePoint(new Point(0, 0), Root) : new Point();
            sb.Append(ci, $"{n}x={(p.X - vx) / vw:0.#####},{n}y={(p.Y - vy) / vh:0.#####},{n}w={el.ActualWidth / vw:0.#####},{n}h={el.ActualHeight / vh:0.#####},{n}r={radius / vh:0.#####},{n}a={alpha:0.###},");
        }
        // The blur follows the bars' own fade (SendGlass keeps mpv at most one update behind).
        Add("a", Pill, 34, LiveAlpha(Pill) * k);
        Add("b", TopBar, 28, LiveAlpha(TopBar) * k);
        Add("c", Menu, 18, LiveAlpha(Menu) * k);
        // d: the Skip / Up next card (the hover preview has no live blur: it moved too fast for mpv to follow, and is nearly all picture)
        Add("d", Prompt, 20, LiveAlpha(Prompt) * k);
        // blur strength follows the Glass setting: Clear / Balanced / Frosted
        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"gblur={(S.Glass switch { 0 => 9.0, 2 => 30.0, _ => 16.0 })}");
        var opts = sb.ToString();
        if (opts == lastGlass) return;
        lastGlass = opts;
        SendGlass();
    }

    // mpv applies these about once per video frame. Sent every screen refresh they queued up and the blur ran
    // ~0.4 s behind the bars (the "ghost bar"), so only one is in flight at a time and the newest goes next.
    public Func<object[], Task<string?>> Ask = _ => Task.FromResult<string?>(null);
    bool glassBusy;
    async void SendGlass()
    {
        if (glassBusy) return;
        glassBusy = true;
        try
        {
            string sent;
            do { sent = lastGlass; await Ask(new object[] { "set_property", "glsl-shader-opts", sent }); }
            while (sent != lastGlass);
        }
        finally { glassBusy = false; }
    }

    // Blur off in mpv while the window zooms (WindowZoom.cs): a picture of the video is taken without it.
    bool glassOff;
    public void SuspendGlass(bool off)
    {
        if (glassOff == off) return;
        glassOff = off;
        WatchGlass();
        PushGlass(null, EventArgs.Empty);
    }

    // Window is about to change size: fade the bars out, and back in once it settles.
    public void BeginTransition() { WatchGlass(); Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(90))); }

    public void EndTransition()
    {
        WatchGlass();
        UpdateFrostViewbox();
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)) { BeginTime = TimeSpan.FromMilliseconds(60), EasingFunction = Ease });
        // no Activity(): the bars come back as they were (hidden stays hidden), and shown ones keep their hide timer
    }

    readonly DispatcherTimer mouseWatch = new() { Interval = TimeSpan.FromMilliseconds(80) };
    System.Drawing.Point lastPos;
    public Func<bool> Resizable = () => false;
    public string Kind = "anime"; // what's playing: picks the anime or the TV & movies language defaults

    public Action PopOut = () => { };
    void OnPopOut(object s, RoutedEventArgs e) => PopOut();

    public void SetPopped(bool on)
    {
        PopBtn.Content = on ? "\uE73F" : "\uE8A7";
        PopBtn.ToolTip = on ? "Back to the normal window" : "Pop out: small player that stays on top";
    }


    // ---------- state from mpv / main window ----------

    public void SetPause(bool p)
    {
        paused = p;
        PlayPause.Content = p ? "\uE768" : "\uE769";
        Activity();
    }

    public void SetDuration(double d) { duration = d; Redraw(); DrawMarks(); }

    public void SetTime(double t)
    {
        if (dragging) return;
        time = t;
        UpdateFrost();
        UpdatePrompt();
        // mpv reports this every video frame; redrawing a hidden bar still made Windows recomposite the whole overlay
        if (shown) Redraw();
    }

    public void SetFile(string path)
    {
        currentFile = path;
        thumbs?.Dispose();
        thumbs = new Thumbs(path);
        ResetSegments();
        previewSecond = -1;
        frostSecond = -1;
        Activity();
    }

    public void SetFullscreen(bool on)
    {
        FullBtn.Content = on ? "\uE73F" : "\uE740";
        WinButtons.Visibility = on ? Visibility.Collapsed : Visibility.Visible; // no window to minimize in fullscreen
    }

    public void SetMaximized(bool on)
    {
        MaxBtn.Content = on ? "\uE923" : "\uE922";
        MaxBtn.ToolTip = on ? "Restore" : "Maximize";
    }

    // Video shape (4:3, 16:9...): lines the frosted glass up with the picture and sizes the preview to match.
    public void SetAspect(double a)
    {
        if (a <= 0 || Math.Abs(a - aspect) < 0.001) return;
        aspect = a;
        WatchGlass();
        PreviewFrame.Width = 180 * a;
        Preview.Width = PreviewFrame.Width + 16;
        UpdateFrostViewbox();
    }

    public void SetSpeed(double v)
    {
        speed = v;
        SpeedBtn.Content = $"{v:0.##}×";
        if (openMenu == "speed") BuildMenu();
    }

    int qualityHeight;
    string qualityCodec = "";
    string? upscaleLabel;

    public void SetQuality(int height, string codec)
    {
        qualityHeight = height;
        qualityCodec = codec;
        string res = height >= 2000 ? "4K" : height >= 1400 ? "1440p" : height > 0 ? $"{height}p" : "";
        QualityText.Text = string.Join("  ", new[] { res, codec.ToUpperInvariant(), upscaleLabel ?? "" }.Where(x => x != ""));
        Quality.Visibility = QualityText.Text == "" ? Visibility.Collapsed : Visibility.Visible;
    }

    // "AI Balanced" etc. while AI upscaling runs, shown in the quality badge
    public void SetUpscale(string? label)
    {
        upscaleLabel = label;
        SetQuality(qualityHeight, qualityCodec);
    }

    public Action ToggleUpscale = () => { };   // MainWindow: on/off for real (saved), applied to this video
    public Action ApplyUpscale = () => { };    // MainWindow: tier or look changed in the menu

    // The AI button: accent while upscaling, bright when on but not upscaling this video, dimmed when off.
    public void SetUpscaleButton(bool on, bool active, string tip)
    {
        AiBtn.Opacity = on ? 1 : 0.4;
        AiBtn.Foreground = active ? (Brush)FindResource("Accent") : (Brush)FindResource("Text");
        AiBtn.ToolTip = tip;
    }

    void OnAiUpscale(object s, RoutedEventArgs e)
    {
        ToggleUpscale();
        if (openMenu == "settings") BuildMenu();
    }

    // mpv's track-list: audio and subtitle tracks with names for the menu.
    public void SetTracks(JsonElement list)
    {
        tracks = new();
        if (list.ValueKind != JsonValueKind.Array) return;
        foreach (var t in list.EnumerateArray())
        {
            var type = t.TryGetProperty("type", out var ty) ? ty.GetString() ?? "" : "";
            if (type is not ("audio" or "sub")) continue;
            string Get(string k) => t.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var lang = Get("lang");
            var parts = new List<string>();
            if (Get("title") is { Length: > 0 } title) parts.Add(title);
            if (lang != "") parts.Add(Language(lang));
            if (type == "audio" && t.TryGetProperty("demux-channel-count", out var ch) && ch.ValueKind == JsonValueKind.Number) parts.Add($"{ch.GetInt32()}ch");
            if (Get("codec") is { Length: > 0 } codec) parts.Add(codec.ToUpperInvariant());
            long id = t.GetProperty("id").GetInt64();
            var label = parts.Count > 0 ? string.Join(" · ", parts.Distinct()) : $"Track {id}";
            bool Flag(string k) => t.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
            tracks.Add((id, type, lang.ToLowerInvariant(), label, Flag("selected"), Get("title"), Flag("forced"), Flag("default")));
        }
        if (openMenu == "tracks") BuildMenu();
    }

    static string Language(string code) => code.ToLowerInvariant() switch
    {
        "jpn" or "ja" => "Japanese", "eng" or "en" => "English", "spa" or "es" => "Spanish", "fre" or "fra" or "fr" => "French",
        "ger" or "deu" or "de" => "German", "ita" or "it" => "Italian", "por" or "pt" => "Portuguese", "chi" or "zho" or "zh" => "Chinese",
        "kor" or "ko" => "Korean", "rus" or "ru" => "Russian", "ara" or "ar" => "Arabic", _ => code,
    };

    public void SetFill(bool f) { fill = f; UpdateFrostViewbox(); WatchGlass(); }

    public void SetTitle(string t) => TitleText.Text = t;

    // ---------- debug info ----------

    public Func<string, Task<string?>> Expand = _ => Task.FromResult<string?>(null); // mpv expands ${property} text
    readonly DispatcherTimer debugTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    // One request per refresh: mpv fills in every ${...} (": –" = shown when not available).
    const string DebugTemplate =
        "File           ${filename:–}\n" +
        "Position       ${time-pos:–} / ${duration:–}  (${percent-pos:–}%)\n" +
        "Playlist       ${playlist-pos-1:–} of ${playlist-count:–}\n" +
        "Video          ${video-params/w:–}×${video-params/h:–}  ${video-codec:–}\n" +
        "Colour         ${video-params/hw-pixelformat:${video-params/pixelformat:–}}  ${video-params/colormatrix:–} / ${video-params/primaries:–} / ${video-params/gamma:–}\n" +
        "Decoder        ${hwdec-current:–}\n" +
        "Frame rate     ${container-fps:–} file · ${estimated-vf-fps:–} actual · ${display-fps:–} display\n" +
        "Dropped        ${frame-drop-count:0} output · ${decoder-frame-drop-count:0} decoder\n" +
        "A/V sync       ${avsync:–}\n" +
        "Bitrate        video ${video-bitrate:–} · audio ${audio-bitrate:–}\n" +
        "Audio          ${audio-codec-name:–}  ${audio-params/channel-count:–} ch  ${audio-params/samplerate:–} Hz\n" +
        "Subtitles      ${current-tracks/sub/title:${current-tracks/sub/lang:off}}  delay ${sub-delay:0}\n" +
        "Speed          ${speed:–}×   volume ${volume:–}\n" +
        "Cache          ${demuxer-cache-duration:–} s ahead\n" +
        "Renderer       ${current-vo:–} · ${current-gpu-context:–}";

    void ApplyDebug()
    {
        DebugPanel.Visibility = S.Debug ? Visibility.Visible : Visibility.Collapsed;
        if (S.Debug) { debugTimer.Start(); _ = RefreshDebug(); } else debugTimer.Stop();
    }

    async Task RefreshDebug()
    {
        var text = await Expand(DebugTemplate);
        if (!S.Debug || text == null) return;
        DebugText.Text = $"Show           {TitleText.Text}\n{text}\nPreviews       {thumbs?.Progress ?? "–"}";
    }

    public void SetVolume(double v) { if (!volDragging) { volume = v; RedrawVolume(); } }
    public void SetMute(bool m) { muted = m; RedrawVolume(); }

    // Height of the video area, so the frosted backdrop lines up with the frame under the pills.
    public void SetHostHeight(double h) { hostHeight = h; UpdateFrostViewbox(); }

    // Mouse moved over the video or the bar, or a key was pressed.
    public void Activity()
    {
        WatchGlass();
        SetShown(true);
        hideTimer.Stop();
        hideTimer.Start();
    }

    System.Drawing.Point lastMove;
    void OnActivity(object sender, MouseEventArgs e)
    {
        var p = System.Windows.Forms.Cursor.Position;
        if (p == lastMove) return; // Windows re-sends moves when this window grows or moves under a still cursor (maximize)
        lastMove = p;
        Activity();
    }

    // Bar fade (WPF tint, rims, icons; mpv's blur follows it). Ease-in-out: no long faint tail where the
    // blur, updated only at the video's frame rate, could visibly drift from the rest.
    static readonly IEasingFunction FadeEase = new SineEase { EasingMode = EasingMode.EaseInOut };

    void SetShown(bool on)
    {
        if (on == shown) return;
        shown = on;
        if (on) { Redraw(); UpdateFrost(); } // catch the timeline and the frosted backdrop up (neither is redrawn while hidden)
        WatchGlass();
        var d = TimeSpan.FromMilliseconds(on ? 220 : 300);
        // Opacity 0 = fully transparent pixels, which click through to the video. WPF draws the whole bar while it fades (GlassHandoff.cs).
        Pill.IsHitTestVisible = TopBar.IsHitTestVisible = on; // faded out: the mouse goes to the video
        GlassFade(Pill, pillGlass, null, on ? 1 : 0, d, FadeEase);
        PillShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(on ? 0 : 18, d) { EasingFunction = Ease });
        GlassFade(TopBar, topGlass, null, on ? 1 : 0, d, FadeEase);
        TopShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(on ? 0 : -18, d) { EasingFunction = Ease });
        if (!on) { HidePreview(); CloseMenu(); }
        // Skip / Up next card: sits above the bar while it's shown, drops into its place when it hides
        PromptShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(on ? 0 : 84, d) { EasingFunction = FadeEase });
        UpdatePrompt();
        ShownChanged?.Invoke(on);
    }

    void Redraw()
    {
        double w = Timeline.ActualWidth, f = duration > 0 ? Math.Clamp(time / duration, 0, 1) : 0;
        // knob travels within the track so it's never clipped at 0% or 100%; whole pixels, so sub-pixel creep doesn't redraw
        double x = Math.Round(f * Math.Max(0, w - Knob.Width));
        Fill.Width = x + Knob.Width / 2;
        Knob.Margin = new Thickness(x, 0, 0, 0);
        Current.Text = Fmt(time);
        Duration.Text = Fmt(duration);
    }

    public static string Fmt(double s)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, s));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    // ---------- frosted glass ----------

    void UpdateFrost()
    {
        int s = (int)Math.Round(time);
        if (s == frostSecond || thumbs == null) return;
        // nothing on screen uses the frost (bars, menu and Skip / Up next card all hidden): build nothing, just keep the
        // decoder near playback so a frame is ready when the bars come back (SetShown(true) catches it up)
        bool seen = shown || openMenu != null || promptKey != "";
        var img = seen ? thumbs.Get(s) : null;
        if (img == null)
        {
            // not decoded yet (e.g. resuming mid-episode): nudge the decoder toward playback, unless someone's hovering
            if (!thumbs.Has(s) && (DateTime.Now - lastFrostGrab).TotalSeconds >= 3) { lastFrostGrab = DateTime.Now; thumbs.Request(s, hover: false); }
            return;
        }
        ApplyFrost(img, s);
    }

    DateTime lastFrostGrab;

    void ApplyFrost(System.Windows.Media.Imaging.BitmapSource img, int s)
    {
        frostSecond = s;
        var frame = new GlassFrame(img);
        pillGlass.SetFrame(frame);
        topGlass.SetFrame(frame);
        lastFrame = frame;
        if (openMenu != null) menuGlass.SetFrame(frame);
    }

    // Map each (blur-inflated) pill rectangle onto the matching part of the video frame.
    void UpdateFrostViewbox()
    {
        double hw = ActualWidth, hh = hostHeight > 0 ? hostHeight : ActualHeight;
        if (hw <= 0 || hh <= 0) return;
        double vw = fill ? Math.Max(hw, hh * aspect) : Math.Min(hw, hh * aspect), vh = vw / aspect; // filled, or letter/pillarboxed
        double vx = (hw - vw) / 2, vy = (hh - vh) / 2;
        var surfaces = new List<(FrameworkElement, GlassSurface)> { (Pill, pillGlass), (TopBar, topGlass), (Menu, menuGlass) };
        if (promptGlass != null) surfaces.Add((Prompt, promptGlass));
        foreach (var (el, glass) in surfaces)
        {
            if (el.ActualWidth <= 0) continue;
            var p = el.TranslatePoint(new Point(0, 0), Root);
            double top = hh - ActualHeight + p.Y; // window sits at the bottom of the host
            glass.SetRegion(new Rect((p.X - vx) / vw, (top - vy) / vh, el.ActualWidth / vw, el.ActualHeight / vh), el.RenderSize);
        }
    }

    // ---------- menus ----------

    void OnAnyMouseDown(object s, MouseButtonEventArgs e)
    {
        ActivateOwner(); // clicking the overlay brings the whole app to the front
        if (openMenu != null && !Menu.IsMouseOver && !TracksBtn.IsMouseOver && !SpeedBtn.IsMouseOver && !SettingsBtn.IsMouseOver) CloseMenu();
    }

    void OnTracksMenu(object s, RoutedEventArgs e) => ToggleMenu("tracks", TracksBtn);
    void OnSpeedMenu(object s, RoutedEventArgs e) => ToggleMenu("speed", SpeedBtn);
    void OnSettingsMenu(object s, RoutedEventArgs e) => ToggleMenu("settings", SettingsBtn);

    void ToggleMenu(string which, FrameworkElement anchor)
    {
        if (openMenu == which) { CloseMenu(); return; }
        WatchGlass();
        openMenu = which;
        // tracks: room for the subtitle timing buttons; never wider than the window (the pop-out is small)
        Menu.Width = Math.Min(which == "settings" ? 360 : which == "tracks" ? 380 : 300, Math.Max(200, Root.ActualWidth - 16));
        // fits between the top bar and the bottom bar; the rest scrolls
        double pillTop = Pill.TranslatePoint(new Point(0, 0), Root).Y;
        double top = TopBar.Visibility == Visibility.Visible ? TopBar.TranslatePoint(new Point(0, TopBar.ActualHeight), Root).Y + 10 : 10;
        MenuScroll.MaxHeight = Math.Clamp(pillTop - 14 - top - 16, 80, 520);
        BuildMenu();
        Menu.Visibility = Visibility.Visible;
        Menu.UpdateLayout();
        var a = anchor.TranslatePoint(new Point(anchor.ActualWidth / 2, 0), Root);
        Canvas.SetLeft(Menu, Math.Clamp(a.X - Menu.Width / 2, 8, Math.Max(8, Root.ActualWidth - Menu.Width - 8)));
        Canvas.SetBottom(Menu, Root.ActualHeight - Pill.TranslatePoint(new Point(0, 0), Root).Y + 14); // sits just above the pill
        Menu.UpdateLayout();
        UpdateFrostViewbox(); // frost shows the part of the frame behind the menu
        if (lastFrame != null) menuGlass.SetFrame(lastFrame);
        GlassFade(Menu, menuGlass, 0, 1, TimeSpan.FromMilliseconds(140), null);
        Activity();
    }

    // Returns true if a menu was open (so a click on the video only closes it).
    public bool CloseMenu()
    {
        if (openMenu == null) return false;
        WatchGlass();
        openMenu = null;
        // a quick fade drawn by WPF alone: snapping it away left mpv's blur of it behind for a moment
        GlassFade(Menu, menuGlass, null, 0, TimeSpan.FromMilliseconds(120), null, () => { if (openMenu == null) Menu.Visibility = Visibility.Collapsed; });
        Activity();
        return true;
    }

    void BuildMenu()
    {
        MenuItems.Children.Clear();
        if (openMenu == "settings") { BuildSettings(); return; }
        if (openMenu == "speed")
        {
            Header("Playback speed");
            foreach (var v in new[] { 0.5, 0.75, 1, 1.25, 1.5, 1.75, 2 })
                Item(v == 1 ? "Normal" : $"{v:0.##}×", Math.Abs(speed - v) < 0.01, () => Send(new object[] { "set_property", "speed", v }));
        }
        else
        {
            Header("Audio");
            var audio = tracks.Where(t => t.Type == "audio").ToList();
            if (audio.Count == 0) Note("No audio tracks");
            foreach (var t in audio) Item(t.Label, t.Selected, () => Send(new object[] { "set_property", "aid", t.Id }));
            Header("Subtitles");
            var subs = tracks.Where(t => t.Type == "sub").ToList();
            Item("Off", !subs.Any(t => t.Selected), () => Send(new object[] { "set_property", "sid", "no" }));
            foreach (var t in subs) Item(t.Label, t.Selected, () => Send(new object[] { "set_property", "sid", t.Id }));
            Item("Add subtitle file…", false, AddSubtitleFile);
            if (subs.Any(t => t.Selected))
            {
                Header($"Subtitle timing  ·  {(subDelay == 0 ? "in sync" : $"{subDelay:+0.0;-0.0} s")}");
                Chips("Shift",new[] { (-0.5, "−0.5"), (-0.1, "−0.1"), (0.1, "+0.1"), (0.5, "+0.5"), (0.0, "Reset") }, double.NaN,
                    d => Send(new object[] { "set_property", "sub-delay", d == 0 ? 0 : Math.Round(subDelay + d, 2) }));
            }
        }
    }

    // Subtitle choice: your language first, and (if set) full dialogue over "Signs & Songs" / forced tracks,
    // which anime releases often mark as the default. With "File default" chosen, mpv's own pick stands.
    void PickSubs()
    {
        var pref = S.LangFor(Kind);
        var langs = Settings.Langs(pref.Subs).Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (!pref.On || langs.Length == 0) return;
        static bool Has(string s, string pattern) => System.Text.RegularExpressions.Regex.IsMatch(s, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var best = tracks.Where(t => t.Type == "sub")
            .Select(t => (t.Id, t.Selected, Score:
                (langs.Contains(t.Lang) || (t.Lang == "" && langs.Any(l => l.Length > 3 && t.Title.Contains(l, StringComparison.OrdinalIgnoreCase))) ? 100 : 0)
                + (S.FullSubs && (t.Forced || Has(t.Title, "sign|song|forced|karaoke")) ? -50 : 0)
                + (Has(t.Title, "full|dialog") ? 10 : 0)
                + (t.Default ? 2 : 0)))
            .OrderByDescending(x => x.Score).FirstOrDefault();
        if (best.Id > 0 && best.Score >= 50 && !best.Selected) Send(new object[] { "set_property", "sid", best.Id }); // only when it's in your language
    }

    // ---------- your own subtitles ----------

    string currentFile = "";
    double subDelay;
    public string CurrentFile => currentFile;     // for "Continue watching"
    public double Position => time;
    public double Length => duration;
    public bool Paused => paused;
    public double Speed => speed;
    static readonly string SubsDir = System.IO.Path.Combine(Settings.DataDir, "subs");

    // Pick a subtitle file for this video; a copy is kept so it comes back every time the video plays.
    void AddSubtitleFile()
    {
        if (currentFile == "") return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Add a subtitle file",
            Filter = "Subtitle files|*.srt;*.ass;*.ssa;*.vtt;*.sub;*.sup|All files|*.*",
        };
        if (dlg.ShowDialog(Owner) != true) return;
        try
        {
            var folder = System.IO.Path.Combine(SubsDir, Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(currentFile.ToLowerInvariant())))[..16]);
            System.IO.Directory.CreateDirectory(folder);
            var copy = System.IO.Path.Combine(folder, System.IO.Path.GetFileName(dlg.FileName));
            System.IO.File.Copy(dlg.FileName, copy, true);
            if (!S.ExtraSubs.TryGetValue(currentFile, out var list)) S.ExtraSubs[currentFile] = list = new();
            list.RemoveAll(p => string.Equals(p, copy, StringComparison.OrdinalIgnoreCase));
            list.Add(copy);
            S.Save();
            Send(new object[] { "sub-add", copy, "select", System.IO.Path.GetFileNameWithoutExtension(copy) });
        }
        catch (Exception ex) { Notify($"Couldn't add that subtitle file: {ex.Message}"); }
        Activity();
    }

    // A video finished loading: bring back its added subtitles (newest one selected) and its timing.
    public async void FileLoaded()
    {
        var file = currentFile;
        if (S.ExtraSubs.TryGetValue(currentFile, out var list) && list.Any(System.IO.File.Exists))
        {
            var present = list.Where(System.IO.File.Exists).ToList();
            for (int i = 0; i < present.Count; i++)
                Send(new object[] { "sub-add", present[i], i == present.Count - 1 ? "select" : "auto", System.IO.Path.GetFileNameWithoutExtension(present[i]) });
        }
        else
        {
            // your own subtitles win; otherwise pick the best track once this video's track list has arrived
            await Task.Delay(300);
            if (file == currentFile) PickSubs();
        }
        Send(new object[] { "set_property", "sub-delay", S.SubDelays.GetValueOrDefault(currentFile) });
    }

    // mpv's subtitle delay changed (our buttons, or its z / x keys): remember it for this video.
    public void SetSubDelay(double v)
    {
        v = Math.Round(v, 2);
        if (v == subDelay) return;
        subDelay = v;
        if (currentFile != "")
        {
            if (v == 0) S.SubDelays.Remove(currentFile); else S.SubDelays[currentFile] = v;
            S.Save();
        }
        if (openMenu == "tracks") BuildMenu();
    }

    void ToggleAutoplayNow()
    {
        S.Autoplay = !S.Autoplay;
        Send(new object[] { "set_property", "keep-open", S.Autoplay ? "no" : "yes" });
        ShowAutoplay();
    }

    void ShowAutoplay()
    {
        AutoBtn.Opacity = S.Autoplay ? 1 : 0.4;
        AutoBtn.ToolTip = S.Autoplay ? "Autoplay: on" : "Autoplay: off";
    }

    void OnAutoplay(object s, RoutedEventArgs e)
    {
        ToggleAutoplayNow();
        S.Save();
        if (openMenu == "settings") BuildMenu(); // keep the settings menu's tick in step
    }

    // Each change applies right away (where mpv allows) and is saved for next time.
    void BuildSettings()
    {
        void Change(Action apply) { apply(); S.Save(); BuildMenu(); }

        Header("Playback");
        Item("Autoplay next video", S.Autoplay, () => Change(ToggleAutoplayNow));
        Item("Auto pop-out", S.AutoPopOut, () => Change(() => S.AutoPopOut = !S.AutoPopOut)); // applies from the next video you open
        Item("Skip opening / ending buttons", S.SkipButtons, () => Change(() => S.SkipButtons = !S.SkipButtons));
        Item("Skip openings automatically", S.AutoSkip, () => Change(() => S.AutoSkip = !S.AutoSkip));
        Item("Up next countdown", S.UpNext, () => Change(() => S.UpNext = !S.UpNext));
        Item("Resume where I left off", S.Resume, () => Change(() =>
        {
            S.Resume = !S.Resume;
            if (ResumeAllowed) Send(new object[] { "set_property", "save-position-on-quit", S.Resume });
        }));

        Header("AI upscaling");
        Item("AI upscaling  (Ctrl+U)", S.Upscale, () => { ToggleUpscale(); BuildMenu(); });
        if (S.Upscale)
        {
            Chips("Tier", new[] { ("auto", "Auto"), ("fast", "Fast"), ("balanced", "Balanced"), ("quality", "Quality") }, S.UpscaleTier,
                t => Change(() => { S.UpscaleTier = t; ApplyUpscale(); }));
            Chips("Look", new[] { ("sharp", "Sharp"), ("soft", "Soft") }, S.UpscaleLook, l => Change(() => { S.UpscaleLook = l; ApplyUpscale(); }));
        }

        // these edit the defaults for this kind of video (anime, or TV shows & movies)
        var pref = S.LangFor(Kind);
        Header(Kind == "anime" ? "Audio & subtitles · anime" : "Audio & subtitles · TV & movies");
        Chips("Audio", new[] { ("jpn", "Japanese"), ("eng", "English"), ("", "File") }, pref.Audio, code => Change(() =>
        {
            S.SetLang(Kind, audio: code);
            var alang = Settings.Langs(code);
            Send(new object[] { "set_property", "alang", alang });
            var langs = alang.Split(','); // switch now if this video has that language
            var match = tracks.FirstOrDefault(t => t.Type == "audio" && langs.Contains(t.Lang));
            if (code != "" && match.Id > 0) Send(new object[] { "set_property", "aid", match.Id });
        }));
        Chips("Subtitles", new[] { ("eng", "English"), ("jpn", "Japanese"), ("", "File") }, pref.Subs, code => Change(() =>
        {
            S.SetLang(Kind, subs: code);
            Send(new object[] { "set_property", "slang", Settings.Langs(code) });
            PickSubs(); // switch now if this video has that language
        }));
        Item("Show subtitles", pref.On, () => Change(() =>
        {
            S.SetLang(Kind, on: !pref.On);
            Send(new object[] { "set_property", "sid", !pref.On ? "auto" : "no" });
            if (!pref.On) PickSubs();
        }));
        Item("Prefer full subtitles over signs only", S.FullSubs, () => Change(() => { S.FullSubs = !S.FullSubs; PickSubs(); }));
        Chips("Text size", new[] { (0.8, "S"), (1.0, "M"), (1.3, "L") }, S.SubScale, scale => Change(() =>
        {
            S.SubScale = scale;
            Send(new object[] { "set_property", "sub-scale", scale });
        }));

        Header("Controls");
        Chips("Hide after", new[] { (2.0, "2s"), (3.0, "3s"), (5.0, "5s") }, S.HideAfter, secs => Change(() =>
        {
            S.HideAfter = secs;
            hideTimer.Interval = TimeSpan.FromSeconds(secs);
        }));
        Item("Timeline previews", S.Previews, () => Change(() => S.Previews = !S.Previews));

        Header("Advanced");
        Item("Debug info", S.Debug, () => Change(() => { S.Debug = !S.Debug; ApplyDebug(); }));
    }

    // A labelled row of pill choices; the current value is highlighted.
    void Chips<T>(string label, (T Value, string Name)[] options, T current, Action<T> pick)
    {
        var row = new DockPanel { Margin = new Thickness(12, 6, 8, 6) };
        var chips = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(chips, Dock.Right);
        foreach (var (value, name) in options)
        {
            var on = Equals(value, current);
            var b = new Button { Style = (Style)Application.Current.Resources["Chip"], Content = name, Tag = on ? "On" : null,
                Foreground = (Brush)Application.Current.Resources["Text"] };
            System.Windows.Automation.AutomationProperties.SetName(b, $"{label}: {name}");
            b.Click += (_, _) => { pick(value); Activity(); };
            chips.Children.Add(b);
        }
        row.Children.Add(chips);
        row.Children.Add(new TextBlock { Text = label, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 0, 0, 0) });
        MenuItems.Children.Add(row);
    }

    void Header(string text) => MenuItems.Children.Add(new TextBlock
    {
        Text = text.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.Bold, Opacity = 0.55,
        Margin = new Thickness(12, MenuItems.Children.Count == 0 ? 6 : 14, 12, 4),
    });

    void Note(string text) => MenuItems.Children.Add(new TextBlock { Text = text, FontSize = 13, Opacity = 0.6, Margin = new Thickness(12, 4, 12, 4) });

    void Item(string label, bool selected, Action pick)
    {
        var dock = new DockPanel();
        var check = new TextBlock
        {
            Text = selected ? "\uE73E" : "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 13, Width = 24,
            Foreground = (Brush)Application.Current.Resources["Accent"], VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(check, Dock.Left);
        dock.Children.Add(check);
        dock.Children.Add(new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Style = (Style)Application.Current.Resources["MenuItem"], Content = dock, Foreground = (Brush)Application.Current.Resources["Text"] };
        System.Windows.Automation.AutomationProperties.SetName(b, label);
        b.Click += (_, _) => { pick(); Activity(); };
        MenuItems.Children.Add(b);
    }

    // ---------- top bar ----------

    void OnBackClick(object s, RoutedEventArgs e) => Back();
    void OnMin(object s, RoutedEventArgs e) => Minimize();
    void OnMax(object s, RoutedEventArgs e) => ToggleMaximize();
    void OnCloseApp(object s, RoutedEventArgs e) => CloseApp();

    // Empty part of the top pill behaves like a title bar: drag to move, double-click to maximize.
    void OnTopBarDown(object s, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindButton(d)) return;
        if (e.ClickCount == 2) ToggleMaximize(); else DragWindow();
    }

    static bool FindButton(DependencyObject? d)
    {
        for (; d != null; d = d is Visual ? VisualTreeHelper.GetParent(d) : null)
            if (d is Button) return true;
        return false;
    }

    // ---------- volume ----------

    void RedrawVolume()
    {
        double w = VolTrack.ActualWidth, f = muted ? 0 : Math.Clamp(volume / 100, 0, 1);
        double x = f * Math.Max(0, w - VolKnob.Width);
        VolFill.Width = x + VolKnob.Width / 2;
        VolKnob.Margin = new Thickness(x, 0, 0, 0);
        VolFill.Opacity = muted ? 0.4 : 1;
        VolText.Text = muted ? "Off" : $"{Math.Round(volume)}";
        MuteBtn.Content = muted || volume <= 0 ? "\uE74F" : volume < 34 ? "\uE993" : volume < 67 ? "\uE994" : "\uE995";
    }

    void SetVol(double v)
    {
        volume = Math.Clamp(v, 0, 100);
        if (muted) Send(new object[] { "set_property", "mute", false });
        Send(new object[] { "set_property", "volume", Math.Round(volume) });
        RedrawVolume();
        Activity();
    }

    double VolAt(MouseEventArgs e) => e.GetPosition(VolTrack).X / Math.Max(1, VolTrack.ActualWidth) * 100;

    void OnVolDown(object s, MouseButtonEventArgs e) { volDragging = true; VolTrack.CaptureMouse(); SetVol(VolAt(e)); }
    void OnVolMove(object s, MouseEventArgs e) { if (volDragging) SetVol(VolAt(e)); }
    void OnVolUp(object s, MouseButtonEventArgs e) { volDragging = false; VolTrack.ReleaseMouseCapture(); }
    void OnMute(object s, RoutedEventArgs e) => Send(new object[] { "cycle", "mute" });

    public void Wheel(int delta) => SetVol(volume + Math.Sign(delta) * 5);
    void OnWheel(object s, MouseWheelEventArgs e) => Wheel(e.Delta);

    // ---------- buttons ----------

    void OnPlayPause(object s, RoutedEventArgs e) => Send(new object[] { "cycle", "pause" });
    void OnPrev(object s, RoutedEventArgs e) => Send(new object[] { "playlist-prev" });
    void OnNext(object s, RoutedEventArgs e) => Send(new object[] { "playlist-next" });
    void OnFullscreen(object s, RoutedEventArgs e) => Send(new object[] { "cycle", "fullscreen" });

    // ---------- timeline ----------

    double TimeAt(MouseEventArgs e) => Math.Clamp(e.GetPosition(Timeline).X / Math.Max(1, Timeline.ActualWidth), 0, 1) * duration;

    void OnTimelineDown(object s, MouseButtonEventArgs e)
    {
        dragging = true;
        Timeline.CaptureMouse();
        Seek(TimeAt(e), "absolute+keyframes");
    }

    void OnTimelineUp(object s, MouseButtonEventArgs e)
    {
        if (!dragging) return;
        dragging = false;
        Timeline.ReleaseMouseCapture();
        Seek(TimeAt(e), "absolute+exact");
        if (!overTimeline) HidePreview();
    }

    void OnTimelineEnter(object s, MouseEventArgs e)
    {
        overTimeline = true;
        KnobScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)) { EasingFunction = Ease });
        KnobScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)) { EasingFunction = Ease });
    }

    void OnTimelineLeave(object s, MouseEventArgs e)
    {
        overTimeline = false;
        KnobScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.6, TimeSpan.FromMilliseconds(200)));
        KnobScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.6, TimeSpan.FromMilliseconds(200)));
        HoverFill.Width = 0;
        if (!dragging) HidePreview();
    }

    void OnTimelineMove(object s, MouseEventArgs e)
    {
        Activity();
        var t = TimeAt(e);
        HoverFill.Width = Math.Clamp(e.GetPosition(Timeline).X, 0, Timeline.ActualWidth);
        if (dragging) Seek(t, "absolute+keyframes"); // fast while scrubbing; exact seek on release
        ShowPreview(t, e.GetPosition(PreviewLayer).X);
    }

    void Seek(double t, string mode)
    {
        time = t;
        Redraw();
        Send(new object[] { "seek", t, mode });
    }

    // ---------- hover preview ----------

    async void ShowPreview(double t, double x)
    {
        if (duration <= 0 || !S.Previews) return;
        if (Preview.Visibility != Visibility.Visible)
        {
            Preview.Visibility = Visibility.Visible;
            Preview.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
        }
        var pillTop = Pill.TranslatePoint(new Point(0, 0), Root).Y;
        Canvas.SetLeft(Preview, Math.Clamp(x - Preview.Width / 2, 8, Math.Max(8, PreviewLayer.ActualWidth - Preview.Width - 8)));
        Canvas.SetTop(Preview, pillTop - (Preview.ActualHeight > 0 ? Preview.ActualHeight : 236) - 14);
        PreviewTime.Text = MarkLabel(t) is { Length: > 0 } mark ? $"{Fmt(t)}  ·  {mark}" : Fmt(t);

        int sec = (int)Math.Round(t);
        if (sec == previewSecond && PreviewImage.Source != null) return; // same frame: no re-decode
        var img = thumbs?.Get(t);
        int my = ++previewSeq;
        if (img != null) { previewSecond = sec; PreviewImage.Source = img; return; }
        // Not decoded yet: steer the decoder here, then show the frame as soon as it lands (usually < 1 s).
        var th = thumbs;
        th?.Request(t);
        for (int k = 0; k < 60 && th != null; k++)
        {
            await Task.Delay(50);
            if (my != previewSeq || th != thumbs) return; // mouse moved on
            if (th.Get(t) is { } got) { previewSecond = sec; PreviewImage.Source = got; return; }
        }
    }

    int previewSecond = -1;

    void HidePreview()
    {
        previewSeq++;
        Preview.BeginAnimation(OpacityProperty, null);
        Preview.Opacity = 0;
        Preview.Visibility = Visibility.Collapsed;
    }
}
