using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using Ellipse = System.Windows.Shapes.Ellipse;
using Path = System.IO.Path;
using System.Windows.Shell;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace AnimePlayer;

// Season/Ep: from "S02E05" or a "Season 2" folder (0 = unknown, -1 = extras); Num shows the episode within its season when known.
public record Video(string Path, string Title, int N, int Season = 0, int Ep = 0)
{
    public string Num => (Ep > 0 ? Ep : N).ToString("00");
    // with the episode code, for places without a season/number column (player title bar, Continue watching)
    public string Label => Season >= 0 && Ep > 0 && !Regex.IsMatch(Title, @"\bS\d+\s*[-–]?\s*E\d+", RegexOptions.IgnoreCase)
        ? $"S{Math.Max(1, Season):00}E{Ep:00} · {Title}" : Title;
}

// Kind: "anime", "tv" or "movie". Folder is the series folder (the first one, for a TV show split over several),
// or the file itself for a movie sitting loose in the movies folder.
// Tag: the tag of the library folder it came from (filter chips on the library page).
public record Entry(string Name, ImageSource? Cover, List<Video> Videos, string Unit, bool IsRandom = false, string Folder = "", string Kind = "anime", string Tag = "")
{
    // cache key for details: anime keeps its plain name (shared by a show and its openings)
    public string InfoKey => Kind == "anime" ? Name : $"{Kind}-{Name}";
    public string Count => IsRandom ? (Videos.Count == 1 ? $"Play the 1 {Unit.TrimEnd('s')}" : $"Shuffle all {Videos.Count} {Unit}") : $"{Videos.Count} {(Videos.Count == 1 ? Unit.TrimEnd('s') : Unit)}";
}

public partial class MainWindow : Window
{
    // Libraries: a command-line arg (one anime folder with Anime Shows / Anime Openings, for testing), else the ones set in Settings.
    static readonly string? Arg = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault();
    static readonly string? ArgRoot = Arg != null && Directory.Exists(Arg) ? Arg : null;
    static readonly string? ArgFile = Arg != null && File.Exists(Arg) ? Arg : null; // "Open with" / a video dropped on the exe: play it
    static readonly List<MediaLibrary> ArgLibs = ArgRoot == null ? new() : new()
    {
        new() { Id = "shows", Name = "Shows and Movies", Folders = { new() { Path = ArgRoot, Kind = "anime" } } },
        new() { Id = "themes", Name = "Openings / Endings", Folders = { new() { Path = ArgRoot, Kind = "themes" } } },
    };
    static List<MediaLibrary> Libs => ArgRoot != null ? ArgLibs : Settings.Current.Libs;

    // Test runs (tests\app-tests.ps1): HAKU_TEST_WINDOW="x,y,w,h" in screen pixels puts the window there (e.g. on another
    // monitor) and Haku never takes focus or comes to the front, so tests can run while you use the PC.
    static readonly (int X, int Y, int W, int H)? TestWindow =
        Environment.GetEnvironmentVariable("HAKU_TEST_WINDOW")?.Split(',') is { Length: 4 } p && p.All(x => int.TryParse(x, out _))
            ? (int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3])) : null;
    void Front() { if (TestWindow == null) Activate(); }
    static readonly string[] VideoExt = { ".mp4", ".mkv", ".webm", ".avi", ".mov", ".m4v" };
    static readonly string[] CoverExt = { ".png", ".jpg", ".jpeg", ".webp", ".bmp" };
    static readonly string DataDir = Settings.DataDir; // %LOCALAPPDATA%\Haku Player
    public static readonly string GlassShader = Path.Combine(AppContext.BaseDirectory, "Assets", "glass.glsl");

    Dictionary<string, List<Entry>> lib = new();
    MpvVideo? mpv;
    StreamWriter? ipc;
    PlayerControls? controls;
    UIElement returnTo = null!; // page to go back to when playback ends
    static readonly string VolumeFile = Path.Combine(DataDir, "volume.txt");
    double volume = LoadVolume();
    int videoHeight;
    string videoCodec = "";
    bool fullscreen;
    System.Drawing.Point lastMouse;
    bool menuJustClosed;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadLibrary();
        AllowDrop = true; // drop videos anywhere to play them (DropPlay.cs)
        DragEnter += OnDragFilesEnter; DragOver += OnDragFiles; Drop += OnDropFiles;
        LocationChanged += (_, _) => PlaceControls();
        VideoHost.SizeChanged += (_, _) => { PlaceControls(); UpdateFill(); };
        SourceInitialized += (_, _) =>
        {
            SetupGlass();
            HwndSource.FromHwnd(Hwnd).AddHook(KeepVideoShape);
            if (TestWindow is { } r) SetWindowPos(Hwnd, IntPtr.Zero, r.X, r.Y, r.W, r.H, 0x0014 /* NOZORDER | NOACTIVATE */);
        };
        if (TestWindow != null) ShowActivated = false; // a test run: never take focus (you may be in a game)
        StateChanged += (_, _) => { FitMaximized(); UpdateBlobs(); };
        Activated += (_, _) => UpdateBlobs();
        Deactivated += (_, _) => UpdateBlobs();
        // loading screen: spinning ring + breathing logo (stopped again in HideSplash)
        SplashRot.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.4)) { RepeatBehavior = RepeatBehavior.Forever });
        var pulse = new DoubleAnimation(0.94, 1.04, TimeSpan.FromSeconds(0.9)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        SplashPulse.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
        SplashPulse.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
        MakeBlobs();
        ApplyAppearance();
        ApplyGlass();
        SmoothScroll.Attach(LibScroll, s => { blobsScrolling = s; UpdateBlobs(); });
        SmoothScroll.Attach(VideoScroll);
        returnTo = Detail;
        Watch.Changed += () => Dispatcher.BeginInvoke(RefreshWatchViews);

        // The video is part of this window: it drives the bar, cursor and clicks. (Resize edges come from WindowChrome.)
        VideoHost.MouseMove += (_, e) =>
        {
            var p = System.Windows.Forms.Cursor.Position;
            if (p == lastMouse) return; // Windows re-sends moves when windows change under a still cursor
            lastMouse = p;
            controls?.Activity();
        };
        VideoHost.MouseWheel += (_, e) => controls?.Wheel(e.Delta);
        VideoHost.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                if (menuJustClosed) return;
                Send(new object[] { "cycle", "pause" }); // undo the single click's pause
                Send(new object[] { "cycle", "fullscreen" });
                return;
            }
            if (controls?.CloseMenu() == true) { menuJustClosed = true; return; }
            menuJustClosed = false;
            Send(new object[] { "cycle", "pause" });
        };
        VideoHost.SizeChanged += (_, _) => SizeVideo();
    }

    // mpv draws at the video area's real pixel size, in the same layout pass
    void SizeVideo()
    {
        if (mpv == null) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        mpv.SetSize((int)Math.Round(VideoHost.ActualWidth * dpi.DpiScaleX), (int)Math.Round(VideoHost.ActualHeight * dpi.DpiScaleY));
    }

    // ---------- Window chrome & glass ----------

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    [DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr h);
    [DllImport("user32.dll")] static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] static extern IntPtr CreateCursor(IntPtr inst, int x, int y, int w, int h, byte[] and, byte[] xor);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(System.Drawing.Point p);
    [DllImport("user32.dll")] static extern bool IsChild(IntPtr parent, IntPtr child);

    IntPtr Hwnd => new WindowInteropHelper(this).Handle;

    void Dwm(int attr, int value) => DwmSetWindowAttribute(Hwnd, attr, ref value, 4);

    void SetupGlass()
    {
        // Let DWM's acrylic show through the window background.
        HwndSource.FromHwnd(Hwnd).CompositionTarget.BackgroundColor = Colors.Transparent;
        Dwm(20, 1); // DWMWA_USE_IMMERSIVE_DARK_MODE
        Dwm(38, 3); // DWMWA_SYSTEMBACKDROP_TYPE = acrylic (Windows 11 22H2+)
        Dwm(33, 2); // DWMWA_WINDOW_CORNER_PREFERENCE = round
        Dwm(35, unchecked((int)0xFFFFFFFE)); // DWMWA_CAPTION_COLOR = none: no OS title strip
        // Drop WS_SYSMENU so DWM stops drawing its own caption buttons under ours.
        SetWindowLong(Hwnd, -16, GetWindowLong(Hwnd, -16) & ~0x00080000);
    }

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);

    // A chromeless maximized window overhangs the screen by the frame width; pad it back in.
    void FitMaximized()
    {
        bool max = WindowState == WindowState.Maximized;
        MaxBtn.Content = max ? "\uE923" : "\uE922";
        MaxBtn.ToolTip = max ? "Restore" : "Maximize";
        controls?.SetMaximized(max);
        if (!max) Dispatcher.BeginInvoke(FitWindowToVideo);
        UpdatePanelEdge();
        if (fullscreen) return;
        if (max)
        {
            uint dpi = (uint)(96 * VisualTreeHelper.GetDpi(this).DpiScaleX);
            double f = (GetSystemMetricsForDpi(32, dpi) + GetSystemMetricsForDpi(92, dpi)) / (dpi / 96.0); // SIZEFRAME + PADDEDBORDER
            Shell.Margin = new Thickness(f);
        }
        else Shell.Margin = new Thickness(0);
    }

    void UpdatePanelEdge() { }

    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

    void ToggleMaximizeOrFullscreen()
    {
        if (fullscreen) Send(new object[] { "cycle", "fullscreen" });
        else OnMaximize(this, new RoutedEventArgs());
    }

    void DragFromOverlay()
    {
        if (fullscreen) return;
        ReleaseCapture();
        SendMessage(Hwnd, 0xA1 /* WM_NCLBUTTONDOWN */, (IntPtr)2 /* HTCAPTION */, IntPtr.Zero);
    }

    void OnMinimize(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    // Maximize / restore: Windows' own animation, everywhere. The video and the bars are part of this one window now
    // (MpvVideo.cs), so Windows' snapshot of it has them in it, and the new size is drawn in one go.
    void OnMaximize(object s, RoutedEventArgs e)
    {
        if (fullscreen) return;
        WindowAnimations();
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        PlaceControls();
    }

    // Windows' minimize / maximize animation: always on (it used to be off in the player: mpv's own window wasn't in its snapshot).
    void WindowAnimations() => Dwm(3 /* DWMWA_TRANSITIONS_FORCEDISABLED */, 0);

    // ---------- smooth size changes ----------

    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    EventHandler? animTick;

    WINDOWPLACEMENT Placement()
    {
        var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        GetWindowPlacement(Hwnd, ref wp);
        return wp;
    }

    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);

    // Maximize where we already are (no flash back to the old size), then remember the size to restore to.
    void MaximizeKeeping(RECT normal)
    {
        ShowWindow(Hwnd, 3); // SW_MAXIMIZE
        var wp = Placement();
        wp.normal = normal;
        wp.showCmd = 3;
        SetWindowPlacement(Hwnd, ref wp);
    }

    // Where a maximized window sits: the work area plus the invisible frame Windows hangs off-screen.
    RECT MaxRect()
    {
        var a = System.Windows.Forms.Screen.FromHandle(Hwnd).WorkingArea;
        uint dpi = (uint)(96 * VisualTreeHelper.GetDpi(this).DpiScaleX);
        int f = GetSystemMetricsForDpi(32, dpi) + GetSystemMetricsForDpi(92, dpi);
        return new RECT { L = a.Left - f, T = a.Top - f, R = a.Right + f, B = a.Bottom + f };
    }

    // Size change: in the player, dip to black (resizing the video every frame stutters);
    // elsewhere, glide.
    // prepare runs right before the resize (hidden behind the black when covering).
    void Transition(RECT to, Action? done, Action? prepare = null)
    {
        if (Player.IsVisible) _ = CoverTransition(to, done, prepare);
        else { Dwm(3, 1); prepare?.Invoke(); AnimateTo(to, done); }
    }

    Cover? cover;
    bool covering;

    async System.Threading.Tasks.Task CoverTransition(RECT to, Action? done, Action? prepare)
    {
        if (covering)
        {
            prepare?.Invoke();
            // already mid-transition: just jump, the running fade hides it
            SetWindowPos(Hwnd, IntPtr.Zero, to.L, to.T, to.R - to.L, to.B - to.T, 0x0004 | 0x0010);
            done?.Invoke();
            PlaceControls();
            return;
        }
        covering = true;
        cover ??= new Cover(Hwnd);
        GetWindowRect(Hwnd, out var from);
        Dwm(3, 1);
        controls?.BeginTransition();
        cover.Place(from.L, from.T, from.R, from.B, from.L > 0 || from.T > 0); // windowed = rounded corners
        await cover.Fade(0, 1, 110);

        prepare?.Invoke();
        bool round = to.L > 0 || to.T > 0; // fullscreen / maximized start at or past the screen edge: square corners
        cover.Place(to.L, to.T, to.R, to.B, round);
        SetWindowPos(Hwnd, IntPtr.Zero, to.L, to.T, to.R - to.L, to.B - to.T, 0x0004 | 0x0010);
        done?.Invoke();
        PlaceControls();
        await System.Threading.Tasks.Task.Delay(110); // let mpv draw its first frame at the new size

        await cover.Fade(1, 0, 220);
        cover.Hide();
        controls?.EndTransition();
        covering = false;
        Dispatcher.BeginInvoke(WindowAnimations, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    // Glide the window to a new rectangle (ease-out, ~1/4 s). Player bars fade out meanwhile so they never lag.
    void AnimateTo(RECT to, Action? done)
    {
        if (animTick != null) CompositionTarget.Rendering -= animTick;
        GetWindowRect(Hwnd, out var from);
        Dwm(3, 1); // no Windows min/max animation on top of ours
        controls?.BeginTransition();
        var sw = Stopwatch.StartNew();
        static int Lerp(int a, int b, double k) => (int)Math.Round(a + (b - a) * k);
        animTick = (_, _) =>
        {
            double t = Math.Min(1, sw.Elapsed.TotalMilliseconds / 260.0), k = 1 - Math.Pow(1 - t, 3);
            int l = Lerp(from.L, to.L, k), tp = Lerp(from.T, to.T, k), r = Lerp(from.R, to.R, k), b = Lerp(from.B, to.B, k);
            SetWindowPos(Hwnd, IntPtr.Zero, l, tp, r - l, b - tp, 0x0004 | 0x0010); // NOZORDER | NOACTIVATE
            if (t < 1) return;
            CompositionTarget.Rendering -= animTick;
            animTick = null;
            done?.Invoke();
            PlaceControls();
            controls?.EndTransition();
            Dispatcher.BeginInvoke(WindowAnimations, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        };
        CompositionTarget.Rendering += animTick;
    }
    void OnCloseWindow(object s, RoutedEventArgs e) => Close();

    readonly List<Drift> blobClocks = new();

    bool blobsScrolling;

    // Blobs only move when someone can see them: not while scrolling, playing, minimized, or while another app is in front.
    void UpdateBlobs()
    {
        bool run = Settings.Current.MovingBackground && !blobsScrolling && !Player.IsVisible && upscaleDetecting == null // Detect times the GPU
                   && (IsActive || Settings.Current.MovingAlways) && WindowState != WindowState.Minimized;
        foreach (var c in blobClocks) { if (run) c.Go(); else c.Hold(); }
        bool walls = run && Home.IsVisible; // cover walls only drift on the home page
        foreach (var (canvas, clocks) in wallClocks) // tiles on another page of libraries stay still
            foreach (var c in clocks) { if (walls && canvas.IsVisible) c.Go(); else c.Hold(); }
    }

    // Slow drifting colour blobs behind the glass: the "liquid" part.
    void MakeBlobs()
    {
        foreach (var c0 in blobClocks) c0.End();
        blobClocks.Clear();
        Blobs.Children.Clear();
        var (ta, tb, tc) = Theme.Colors(Settings.Current.Theme);
        var blobs = new (Color Color, double X, double Y, double Dx, double Dy, double Secs)[]
        {
            (tb, 0.08, 0.05, 340, 180, 23),
            (tc, 0.62, -0.15, -280, 240, 29),
            (ta, 0.30, 0.55, 260, -200, 34),
        };
        foreach (var b in blobs)
        {
            var c = b.Color;
            var e = new Ellipse
            {
                Width = 1100, Height = 1100, Opacity = 0.42,
                Fill = new RadialGradientBrush(Color.FromArgb(150, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B)),
                // drawn once as a small texture (it's a soft glow, so a quarter resolution looks the same); drifting then only moves it
                CacheMode = new BitmapCache { RenderAtScale = 0.25 },
            };
            var tt = new TranslateTransform();
            e.RenderTransform = tt;
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
            var ax = new DoubleAnimation(0, b.Dx, TimeSpan.FromSeconds(b.Secs)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease };
            var ay = new DoubleAnimation(0, b.Dy, TimeSpan.FromSeconds(b.Secs * 0.8)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease };
            // slow drift looks the same at 30 fps and costs half of 60
            Timeline.SetDesiredFrameRate(ax, 30); Timeline.SetDesiredFrameRate(ay, 30);
            blobClocks.Add(new Drift(tt, TranslateTransform.XProperty, ax));
            blobClocks.Add(new Drift(tt, TranslateTransform.YProperty, ay));
            Blobs.Children.Add(e);
            void Place() { Canvas.SetLeft(e, b.X * Blobs.ActualWidth - 200); Canvas.SetTop(e, b.Y * Blobs.ActualHeight - 200); }
            Place();
            Blobs.SizeChanged += (_, _) => Place();
        }
    }

    // Keep the control-bar window glued to the bottom of the video area.
    void PlaceControls()
    {
        if (controls == null || !VideoHost.IsVisible) return;
        controls.SetHostHeight(VideoHost.ActualHeight); // the bars lie over the video in the same grid: only the glass needs telling
    }

    // ---------- Library ----------

    async Task LoadLibrary()
    {
        lib = await Task.Run(ScanAll);
        Watch.Seed(Everything(lib).Where(e => e.Unit != "themes").SelectMany(e => e.Videos)); // first run: resume points mpv already saved
        ApplyLibrary();
        StartWatcher();
        HideSplash();
        if (ArgFile != null && IsVideo(ArgFile)) PlayFiles(new() { Path.GetFullPath(ArgFile) });
        ListenForOpens();
        _ = PrefetchDetails(); // details, banners and missing covers for anything new
    }

    // Videos handed over by a second launch (App.OnStartup): play them here.
    async void ListenForOpens()
    {
        while (true)
        {
            try
            {
                using var s = new NamedPipeServerStream(App.InstanceKey, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await s.WaitForConnectionAsync();
                var path = await new StreamReader(s).ReadLineAsync();
                if (path == null || !File.Exists(path) || !IsVideo(path)) continue;
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                PlayFiles(new() { path });
            }
            catch { await Task.Delay(1000); }
        }
    }

    // A tiny, pre-blurred copy of a cover: stretched over the page it reads as a soft colour glow,
    // and costs nothing per frame (a live BlurEffect at 4K made resizing stutter).
    static BitmapSource SoftGlow(BitmapSource cover)
    {
        const int w = 24;
        var small = new FormatConvertedBitmap(new TransformedBitmap(cover, new ScaleTransform((double)w / cover.PixelWidth, (double)w / cover.PixelWidth)), PixelFormats.Bgra32, null, 0);
        int h = small.PixelHeight, stride = w * 4;
        var px = new byte[h * stride];
        small.CopyPixels(px, stride, 0);
        var tmp = new byte[px.Length];
        for (int pass = 0; pass < 3; pass++) // 3 box blurs ~ a gaussian
        {
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    for (int c = 0; c < 4; c++)
                    {
                        int sum = 0, n = 0;
                        for (int dx = -2; dx <= 2; dx++) { int xx = Math.Clamp(x + dx, 0, w - 1); sum += px[y * stride + xx * 4 + c]; n++; }
                        tmp[y * stride + x * 4 + c] = (byte)(sum / n);
                    }
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    for (int c = 0; c < 4; c++)
                    {
                        int sum = 0, n = 0;
                        for (int dy = -2; dy <= 2; dy++) { int yy = Math.Clamp(y + dy, 0, h - 1); sum += tmp[yy * stride + x * 4 + c]; n++; }
                        px[y * stride + x * 4 + c] = (byte)(sum / n);
                    }
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
        bmp.Freeze();
        return bmp;
    }

    // Loading screen: fades away once the library (and its covers) is ready.
    void HideSplash()
    {
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(450)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (_, _) =>
        {
            Splash.Visibility = Visibility.Collapsed;
            // its spinner and pulse repeat forever; left running they keep the whole window redrawing 60x a second
            SplashRot.BeginAnimation(RotateTransform.AngleProperty, null);
            SplashPulse.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            SplashPulse.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        };
        Splash.BeginAnimation(OpacityProperty, fade);
        SplashScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.08, TimeSpan.FromMilliseconds(450)));
        SplashScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.08, TimeSpan.FromMilliseconds(450)));
        Show(Home);
    }

    // Every library, keyed by its id: the shows in all its folders, each tagged with its folder's tag.
    static Dictionary<string, List<Entry>> ScanAll()
    {
        var scanned = new Dictionary<(string, string), List<Entry>>(); // a folder in two libraries is read once
        var result = new Dictionary<string, List<Entry>>();
        foreach (var l in Libs.ToList())
        {
            var list = new List<Entry>();
            foreach (var f in l.Folders.ToList())
            {
                var key = (f.Path.TrimEnd('\\').ToLowerInvariant(), f.Kind);
                if (!scanned.TryGetValue(key, out var got))
                {
                    try { got = ScanFolder(f); } catch { got = new(); } // a drive that isn't plugged in: skip it, keep the rest
                    scanned[key] = got;
                }
                list.AddRange(got.Select(e => e with { Tag = f.TagName }));
            }
            result[l.Id] = list.DistinctBy(e => (e.Folder, e.Unit)).OrderBy(e => e.Name, Natural.I).ToList();
        }
        return result;
    }

    static List<Entry> ScanFolder(LibraryFolder f) => f.Kind switch
    {
        "tv" => ScanTv(f.Path),
        "movie" => ScanMovies(f.Path),
        "themes" => Scan(Inner(f.Path, "Anime Openings"), "themes"),
        _ => Scan(Inner(f.Path, "Anime Shows"), "episodes"),
    };

    // a whole anime folder (Anime Shows + Anime Openings inside) works too: use the part that fits
    static string Inner(string dir, string sub) => Directory.Exists(Path.Combine(dir, sub)) ? Path.Combine(dir, sub) : dir;

    // every show once, even when it's in more than one library
    static IEnumerable<Entry> Everything(Dictionary<string, List<Entry>> l) => l.Values.SelectMany(x => x).DistinctBy(e => (e.Folder, e.Unit));

    static readonly RegexOptions I = RegexOptions.IgnoreCase;

    // "Game.of.Thrones.Season.1-8.COMPLETE.1080p…" -> ("Game of Thrones", null); "Hellboy (2004) [1080p]" -> ("Hellboy", 2004).
    // Cuts at the first year, season marker or release tag that isn't at the very start.
    public static (string Title, int? Year) CleanTitle(string raw) => CleanTitle(raw, true);

    // keepRaw: when nothing is left after cleaning, return the input (a name) or "" (an episode's leftover tags)
    static (string Title, int? Year) CleanTitle(string raw, bool keepRaw)
    {
        var s = Regex.Replace(raw, @"^www\.\S+\s*-\s*", "", I);          // "www.UIndex.org - Sinners 2025…"
        s = Regex.Replace(s, @"\[[^\]]*\]", " ").Replace('.', ' ').Replace('_', ' ');
        var cut = Regex.Matches(s, @"\(?\b(19|20)\d\d\b\)?|\bS\d{1,2}(E\d+)?\b|\bSeasons?\b|\b(2160p|1080p|720p|480p|4K|UHD|Blu-?Ray|BDRip|BRRip|WEB(-?DL|-?Rip)?|HDR|x26[45]|HEVC|Mp4|Complete|REMUX|MULTi|RERIP|REMASTERED|DVDRip)\b", I)
            .FirstOrDefault(m => m.Index > 0);
        int? year = Regex.Matches(s, @"\b(19|20)\d\d\b").FirstOrDefault(m => m.Index > 0) is { } y && (cut == null || y.Index <= cut.Index + 1) ? int.Parse(y.Value) : null;
        var title = Regex.Replace(cut != null ? s[..cut.Index] : s, @"\s+", " ").Trim(' ', '-', '(', '+');
        return (title.Length > 0 || !keepRaw ? title : raw, year);
    }

    // Videos in a folder, without sample clips.
    static IEnumerable<string> VideosIn(string dir) => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
        .Where(f => VideoExt.Contains(Path.GetExtension(f).ToLowerInvariant()) && !Regex.IsMatch(Path.GetFileNameWithoutExtension(f), @"\bsample\b", I));

    // Season/episode from "S01E02" or ".../Season 1/... Episode 02", for ordering a show split over folders.
    static (int S, int E) EpisodeOrder(string f)
    {
        if (Regex.IsMatch(Path.GetDirectoryName(f) ?? "", @"\\(Extras?|Featurettes?|Bonus|Specials? Features?|Behind The Scenes)(\\|$)", I)) return (int.MaxValue, 0); // bonus clips after the episodes
        var n = Path.GetFileNameWithoutExtension(f);
        if (Regex.Match(n, @"S(\d{1,2})\s*[-–]?\s*E(\d{1,3})", I) is { Success: true } m) return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
        int season = Regex.Match(f, @"Season[ ._]?(\d+)", I) is { Success: true } sm ? int.Parse(sm.Groups[1].Value) : 0;
        return Regex.Match(n, @"(?:Episode|Ep)[ ._]?(\d+)", I) is { Success: true } em ? (season, int.Parse(em.Groups[1].Value)) : (season, 999);
    }

    // Official episode names (from the show's details) in place of the file names. Only in the app: files aren't renamed.
    // A file with no season ("Episode 03") is matched to season 1, not to specials (season 0).
    public static void NameEpisodes(Entry e)
    {
        if (e.Kind == "movie" || e.Unit != "episodes" || InfoService.Cached(e.InfoKey)?.EpisodeNames is not { Count: > 0 } names) return; // not movies, not openings
        for (int i = 0; i < e.Videos.Count; i++)
        {
            var v = e.Videos[i];
            if (v.Season >= 0 && v.Ep > 0 && names.TryGetValue($"{Math.Max(1, v.Season)}:{v.Ep}", out var name)) e.Videos[i] = v with { Title = name };
        }
    }

    static Video WithSeason(Video v)
    {
        var (s, e) = EpisodeOrder(v.Path);
        return v with { Season = s == int.MaxValue ? -1 : s, Ep = s == int.MaxValue || e == 999 ? 0 : e };
    }

    // "Rick and Morty - S08E01 - Summer of All Fears" -> "S08E01 · Summer of All Fears"; "Curb.Your.Enthusiasm.S01E01.1080p…" -> "S01E01"
    static string EpisodeTitle(string f)
    {
        var n = Path.GetFileNameWithoutExtension(f);
        var m = Regex.Match(n, @"S(\d{1,2})\s*[-–]?\s*E(\d{1,3})", I);
        if (!m.Success) return Regex.Match(n, @"Episode[ ._]?\d+", I) is { Success: true } em ? em.Value : CleanTitle(n).Title;
        var rest = CleanTitle(" " + n[(m.Index + m.Length)..], false).Title.Trim(' ', '-');
        var code = $"S{int.Parse(m.Groups[1].Value):00}E{int.Parse(m.Groups[2].Value):00}";
        return rest.Length > 1 ? $"{code} · {rest}" : code;
    }

    // TV shows: show folders found by Crawl (any depth), plus loose episodes grouped by show; seasons of the same show
    // in separate folders ("Rick.and.Morty.S01…", "Rick and Morty Season 9") merge.
    static List<Entry> ScanTv(string dir)
    {
        var (shows, loose) = Crawl(dir, false);
        var items = shows.Select(d => (Key: CleanTitle(Path.GetFileName(d)).Title, Folder: d, Files: (List<string>?)null))
            .Concat(LooseShows(loose, false).Select(g => (Key: CleanTitle(g.Name).Title, g.Folder, Files: (List<string>?)g.Files)));
        return items
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .AsParallel()
            .Select(g =>
            {
                var parts = g.OrderBy(x => x.Files != null).ThenBy(x => Path.GetFileName(x.Folder), Natural.I).ToList(); // real folders first
                var videos = parts.SelectMany(x => x.Files ?? VideosIn(x.Folder)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(EpisodeOrder).ThenBy(f => f, Natural.I)
                    .Select((f, i) => WithSeason(new Video(f, EpisodeTitle(f), i + 1))).ToList();
                var e = new Entry(g.Key, null, videos, "episodes", false, parts[0].Folder, "tv");
                NameEpisodes(e);
                return e with { Cover = LoadCover(parts[0].Folder) ?? LoadCoverFile(CoverFile(e.InfoKey)) };
            })
            .Where(e => e.Videos.Count > 0)
            .ToList();
    }

    // Movies: a folder per movie (biggest file first: the film, then any extras) or a video file on its own,
    // at any depth; a folder of several full-length films is a collection (one movie each).
    static List<Entry> ScanMovies(string dir)
    {
        var (shows, loose) = Crawl(dir, true);
        var folders = shows.Select(d => (Path: d, Name: Path.GetFileName(d), Files: VideosIn(d).OrderByDescending(f => new FileInfo(f).Length).ToList()));
        var files = loose.Select(f => (Path: f, Name: Path.GetFileNameWithoutExtension(f), Files: new List<string> { f }));
        return folders.Concat(files).Where(x => x.Files.Count > 0).AsParallel().Select(x =>
        {
            var (title, year) = CleanTitle(x.Name);
            var videos = x.Files.Select((f, i) => new Video(f, i == 0 ? (year != null ? $"{title} ({year})" : title) : CleanTitle(Path.GetFileNameWithoutExtension(f)).Title, i + 1)).ToList();
            var e = new Entry(title, null, videos, "videos", false, x.Path, "movie");
            return e with { Cover = LoadCover(x.Path) ?? LoadCoverFile(CoverFile(e.InfoKey)) };
        }).ToList();
    }

    // Covers fetched for TV shows and movies live in the app's data folder (nothing is written into your media folders).
    public static string CoverFile(string infoKey) => Path.Combine(DataDir, "covers", Regex.Replace(infoKey, @"[^\w]+", "_") + ".jpg");

    // Anime shows ("episodes") and openings / endings ("themes"): show folders found by Crawl, plus loose files grouped by show.
    static List<Entry> Scan(string dir, string unit)
    {
        bool themes = unit == "themes";
        var (shows, loose) = Crawl(dir, false);
        Video Make(string f, int i) => themes ? new Video(f, Friendly(f), i + 1) : WithSeason(new Video(f, Friendly(f), i + 1));
        var folders = shows.Select(d => (Name: Path.GetFileName(d), Folder: d, Files: VideosIn(d).OrderBy(f => Path.GetRelativePath(d, f), Natural.I).ToList()));
        return folders.Concat(LooseShows(loose, themes))
            .OrderBy(x => x.Name, Natural.I)
            .AsParallel().AsOrdered() // covers decode in parallel: the slow part of startup
            .Select(x => new Entry(x.Name, LoadCover(x.Folder) ?? LoadCoverFile(CoverFile(x.Name)), x.Files.Select(Make).ToList(), unit, false, x.Folder))
            .Where(e => e.Videos.Count > 0)
            .Select(e => { NameEpisodes(e); return e; }) // anime shows get official episode names too (openings don't)
            .ToList();
    }

    // cover.jpg / poster.png / folder.jpg in the show's folder (none for loose files)
    static ImageSource? LoadCover(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        foreach (var name in new[] { "cover", "poster", "folder" })
            if (Directory.EnumerateFiles(dir, name + ".*").FirstOrDefault(p => CoverExt.Contains(Path.GetExtension(p).ToLowerInvariant())) is string f) return LoadCoverFile(f);
        return null;
    }

    static ImageSource? LoadCoverFile(string? f)
    {
        if (f == null || !File.Exists(f)) return null;
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.UriSource = new Uri(f);
            bi.DecodePixelWidth = 420;              // 2x card width; keeps memory low for big PNGs
            bi.CreateOptions = BitmapCreateOptions.IgnoreImageCache; // WPF caches by path: a replaced cover would keep showing the old picture
            bi.CacheOption = BitmapCacheOption.OnLoad; // don't lock the file, so covers can be replaced while open
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; } // unreadable cover -> placeholder
    }

    // "CodeGeassR2-OP1-Encoded.23.976fps..." -> "Code Geass R2 – OP1"; "[Grp] Show S01 - E01 [1080p]-Encoded..." -> "Show S01 - E01"
    public static string Friendly(string path)
    {
        var n = Path.GetFileNameWithoutExtension(path);
        var i = n.IndexOf("-Encoded", StringComparison.OrdinalIgnoreCase);
        if (i > 0) n = n[..i];
        n = Regex.Replace(n, @"\[[^\]]*\]", "");
        n = Regex.Replace(n, @"-(NC|BD|DVD)\w*$", "").Trim(' ', '-', '_', '.');
        if (!n.Contains(' ')) // AnimeThemes-style basename
        {
            n = Regex.Replace(n, @"(?<=[a-z])(?=[A-Z0-9])|(?<=\d)(?=[A-Z][a-z])", " ");
            n = Regex.Replace(n, @"-(?=(OP|ED)\d)", " – ");
        }
        return n;
    }

    // ---------- Home ----------

    readonly Dictionary<Canvas, List<Drift>> wallClocks = new();

    // Home panel background: a tilted wall of the category's covers, rows drifting slowly in alternate directions.
    // Each row is cached as one bitmap, so drifting only moves a texture (cheap), and it pauses with the moving background.
    readonly Dictionary<Canvas, (List<Entry> Entries, int Rows)> walls = new();
    const double WallRowH = 198 + 14;

    // Enough rows to cover the panel (tilt and top offset included); more when the window grows, e.g. fullscreen.
    static int WallRows(Canvas c) => Math.Max(4, (int)Math.Ceiling((c.ActualHeight + 300) / WallRowH));

    void BuildWall(Canvas c, List<Entry> entries)
    {
        if (!walls.ContainsKey(c))
            c.SizeChanged += (_, _) => { if (walls.TryGetValue(c, out var w) && WallRows(c) > w.Rows) BuildWall(c, w.Entries); };
        int rows = WallRows(c);
        walls[c] = (entries, rows);
        if (wallClocks.TryGetValue(c, out var old)) foreach (var k in old) k.End();
        var clocks = wallClocks[c] = new();
        c.Children.Clear();
        var covers = entries.Select(e => e.Cover).OfType<ImageSource>().OrderBy(_ => Random.Shared.Next()).ToList();
        if (covers.Count == 0) return;
        const double W = 132, H = 198, G = 14, Span = 1900; // one copy of a row is at least this wide (wider than any panel)
        var wall = new Canvas { RenderTransform = new RotateTransform(-8) };
        for (int r = 0; r < rows; r++)
        {
            var seq = new List<ImageSource>();
            for (int k = r * 3; seq.Count * (W + G) < Span; k++) seq.Add(covers[k % covers.Count]);
            var row = new StackPanel { Orientation = Orientation.Horizontal, CacheMode = new BitmapCache() };
            foreach (var img in seq.Concat(seq)) // two copies back to back, so the loop is seamless
                row.Children.Add(new Border { Width = W, Height = H, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, G, 0),
                    Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill } });
            double aw = seq.Count * (W + G);
            Canvas.SetTop(row, r * (H + G));
            var tt = new TranslateTransform();
            row.RenderTransform = tt;
            bool left = r % 2 == 0;
            var anim = new DoubleAnimation(left ? 0 : -aw, left ? -aw : 0, TimeSpan.FromSeconds(aw / 10)) { RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(anim, 30);
            clocks.Add(new Drift(tt, TranslateTransform.XProperty, anim));
            wall.Children.Add(row);
        }
        Canvas.SetLeft(wall, -160);
        Canvas.SetTop(wall, -90);
        c.Children.Add(wall);
        UpdateBlobs();
    }

    // Rounded corners for a panel's content (Border corners don't clip what's inside).
    static void RoundClip(FrameworkElement el, double radius) =>
        el.SizeChanged += (_, _) => el.Clip = new RectangleGeometry(new Rect(el.RenderSize), radius, radius);

    // Continue watching row: see ContinueRow.cs

    // ---------- Navigation ----------

    void Show(UIElement view)
    {
        foreach (var v in new UIElement[] { Home, Library, Detail, Player })
            v.Visibility = v == view ? Visibility.Visible : Visibility.Collapsed;
        if (Hwnd != IntPtr.Zero) WindowAnimations(); // also covers minimizing from the taskbar while playing
        // the header's drag strip: none over the video (clicks there are the video's; the top bar drags the window itself)
        WindowChrome.GetWindowChrome(this).CaptionHeight = view == Player ? 0 : 52;
        ApplyGlass(); // solid black behind the player, glass everywhere else
        if (view == Home && lib != null) UpdateContinue();
        if (view == Detail) RefreshWatchViews(); // progress from the episode you just watched
        UpdateBlobs();
        BackBtn.Visibility = view == Home ? Visibility.Collapsed : Visibility.Visible;
        Header.Visibility = view == Player ? Visibility.Collapsed : Visibility.Visible;
        Logo.Visibility = view == Home ? Visibility.Visible : Visibility.Collapsed;
        Blobs.Visibility = view == Player ? Visibility.Collapsed : Visibility.Visible; // no point animating behind video
        if (view != Player) HeaderTitle.Text = view == Home ? "Haku Player" : LibraryTitle.Text;

        // Show page: its cover, shrunk to a few pixels and stretched back up, makes a soft ambient glow.
        var cover = view == Detail && Settings.Current.CoverGlow ? (Detail.DataContext as Entry)?.Cover as BitmapSource : null;
        if (cover != null) Ambient.Source = SoftGlow(cover);
        Ambient.BeginAnimation(OpacityProperty, new DoubleAnimation(cover != null ? 0.5 : 0, TimeSpan.FromMilliseconds(500)));

        // Gentle slide-up as each page appears.
        if (view is FrameworkElement fe && view != Player)
        {
            var tt = new TranslateTransform();
            fe.RenderTransform = tt;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease });
            fe.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)));
        }
    }

    void OnTile(object sender, RoutedEventArgs e)
    {
        if (tileDragged) { tileDragged = false; return; } // that press was a drag, not a click
        var key = (string)((Button)sender).Tag;
        currentCategory = key;
        LibraryTitle.Text = Libs.FirstOrDefault(l => l.Id == key)?.Name ?? "";
        ResetFilters();
        ApplyFilter();
        Show(Library);
    }

    void OnCard(object sender, RoutedEventArgs e)
    {
        var entry = (Entry)((FrameworkElement)sender).DataContext;
        if (entry.IsRandom)
        {
            // Shuffled playlist of every opening: Next just goes to another random one.
            var shuffled = entry.Videos.OrderBy(_ => Random.Shared.Next()).ToList();
            returnTo = Library;
            Play(entry with { Name = "Random", Videos = shuffled }, 0);
            return;
        }
        Detail.DataContext = entry;
        ShowEpisodes(entry, null);
        DetailStar.Content = IsFav(entry) ? "\uE735" : "\uE734";
        Show(Detail);
        _ = LoadDetailInfo(entry);
    }

    // ---------- show details (AniList) ----------

    async Task LoadDetailInfo(Entry entry)
    {
        DetailAlt.Text = ""; DetailAlt.Visibility = Visibility.Collapsed; DetailDesc.Text = ""; DetailGenres.Children.Clear(); Facts.Children.Clear();
        DescMore.Visibility = Visibility.Collapsed; DetailDesc.MaxHeight = 120; DescMoreText.Text = "More";
        DetailBanner.BeginAnimation(OpacityProperty, null); DetailBanner.Opacity = 0; DetailBanner.Source = null;
        var info = await InfoService.Get(entry);
        if (Detail.DataContext != entry || info == null) return;

        // other names, skipping ones that only differ from the folder name by case or punctuation
        string N(string? t) => System.Text.RegularExpressions.Regex.Replace((t ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}]", "");
        DetailAlt.Text = string.Join("  ·  ", new[] { info.Title, info.Native }
            .Where(x => !string.IsNullOrEmpty(x) && N(x) != N(entry.Name)).Distinct());
        DetailAlt.Visibility = DetailAlt.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var g in info.Genres.Take(6))
            DetailGenres.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 4, 12, 5), Margin = new Thickness(0, 0, 8, 8),
                Background = new SolidColorBrush(Color.FromArgb(0x1E, 255, 255, 255)), BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)),
                Child = new TextBlock { Text = g, FontSize = 13 },
            });
        DetailDesc.Text = info.Description ?? "";
        DetailDesc.UpdateLayout();
        DescMore.Visibility = DetailDesc.Text.Length > 300 ? Visibility.Visible : Visibility.Collapsed;

        void Fact(string label, string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var v = new TextBlock { Text = value, FontSize = 15, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis };
            DockPanel.SetDock(v, Dock.Right);
            row.Children.Add(v);
            row.Children.Add(new TextBlock { Text = label, FontSize = 14, Foreground = (Brush)FindResource("Muted") });
            Facts.Children.Add(row);
        }
        Fact("Format", info.Format);
        Fact("Year", info.Year?.ToString());
        Fact("Episodes", info.Episodes?.ToString());
        Fact(info.Source == "cinemeta" ? "Director" : "Studio", info.Studio);
        Fact("Score", info.Score is double sc ? $"★ {sc:0.0}" : null);

        if (info.BannerFile is { } file && File.Exists(file))
        {
            // decoded off the UI thread at screen width (some backgrounds are 4K), so opening the page stays smooth
            var bi = await Task.Run(() =>
            {
                try
                {
                    var b = new BitmapImage();
                    b.BeginInit(); b.UriSource = new Uri(file); b.DecodePixelWidth = 1920; b.CacheOption = BitmapCacheOption.OnLoad; b.EndInit(); b.Freeze();
                    return b;
                }
                catch { return null; }
            });
            if (bi == null || Detail.DataContext != entry) return;
            DetailBanner.Source = bi;
            DetailBanner.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, TimeSpan.FromMilliseconds(500)));
        }
    }

    void OnDescMore(object s, RoutedEventArgs e)
    {
        bool open = DetailDesc.MaxHeight < 1000;
        DetailDesc.MaxHeight = open ? double.PositiveInfinity : 120;
        DescMoreText.Text = open ? "Less" : "More";
    }

    // ---------- seasons ----------

    int? openSeason; // the season being shown, or null for the season list / a show without seasons

    static List<IGrouping<int, Video>> Seasons(Entry e) => e.Unit != "episodes" ? new()
        : e.Videos.GroupBy(v => v.Season).OrderBy(g => g.Key < 0 ? int.MaxValue : g.Key).ToList(); // extras (-1) last

    // A show with several seasons opens on its seasons; one season (or none known) goes straight to the episodes.
    void ShowEpisodes(Entry entry, int? season)
    {
        var seasons = Seasons(entry);
        bool picker = seasons.Count(g => g.Key >= 0) > 1;
        if (!picker) season = null;
        openSeason = season;
        SeasonList.Children.Clear();
        DetailCount.Text = entry.Count;
        MissingBtn.Visibility = entry.Kind != "movie" && entry.Unit == "episodes" && InfoService.Cached(entry.InfoKey)?.EpisodeNames is { Count: > 0 }
            ? Visibility.Visible : Visibility.Collapsed;
        if (picker && season == null)
        {
            Videos.ItemsSource = null;
            SeasonList.Visibility = Visibility.Visible;
            SeasonBack.Visibility = Visibility.Collapsed;
            int real = seasons.Count(g => g.Key >= 0);
            DetailCount.Text = $"{real} season{(real == 1 ? "" : "s")} · {entry.Videos.Count(v => v.Season >= 0)} episodes{WatchedSuffix(entry.Videos.Where(v => v.Season >= 0))}";
            foreach (var g in seasons) SeasonList.Children.Add(SeasonRow(entry, g));
        }
        else
        {
            SeasonList.Visibility = Visibility.Collapsed;
            SeasonBack.Visibility = picker ? Visibility.Visible : Visibility.Collapsed;
            Videos.ItemsSource = picker ? entry.Videos.Where(v => v.Season == season).ToList() : entry.Videos;
            if (picker) { int n = ((List<Video>)Videos.ItemsSource).Count; DetailCount.Text = $"{SeasonName(season!.Value)} · {n} {(season < 0 ? "video" : "episode")}{(n == 1 ? "" : "s")}"; }
            if (entry.Unit == "episodes") DetailCount.Text += WatchedSuffix((IEnumerable<Video>)Videos.ItemsSource);
        }
        UpdateMarkAll();
        VideoScroll.ScrollToTop();
    }

    static string SeasonName(int s) => s < 0 ? "Extras" : s == 0 ? "Other episodes" : $"Season {s}";

    Button SeasonRow(Entry entry, IGrouping<int, Video> g)
    {
        int n = g.Count();
        var row = new DockPanel();
        var num = new TextBlock { Text = g.Key > 0 ? g.Key.ToString("00") : "•", FontSize = 16, FontWeight = FontWeights.Bold, Width = 44, VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("Accent") };
        DockPanel.SetDock(num, Dock.Left);
        var chevron = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("Muted") };
        DockPanel.SetDock(chevron, Dock.Right);
        var count = new TextBlock { Text = $"{n} {(g.Key < 0 ? "video" : "episode")}{(n == 1 ? "" : "s")}{WatchedSuffix(g)}", FontSize = 14, Foreground = (Brush)FindResource("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        DockPanel.SetDock(count, Dock.Right);
        row.Children.Add(num);
        row.Children.Add(chevron);
        row.Children.Add(count);
        row.Children.Add(new TextBlock { Text = SeasonName(g.Key), FontSize = 17, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Style = (Style)FindResource("Row"), Content = row, Padding = new Thickness(18, 14, 18, 14), Margin = new Thickness(0, 4, 0, 4),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Foreground = (Brush)FindResource("Text") };
        System.Windows.Automation.AutomationProperties.SetName(b, SeasonName(g.Key));
        b.Click += (_, _) => ShowEpisodes(entry, g.Key);
        return b;
    }

    void OnSeasonBack(object s, RoutedEventArgs e) { if (Detail.DataContext is Entry entry) ShowEpisodes(entry, null); }

    // ---------- what's missing ----------

    // The show's official episodes (aired, specials left out) that have no file in your library, by season.
    static List<(int Season, List<int> Eps, int Total)> Missing(Entry e, Dictionary<string, string> names)
    {
        var have = e.Videos.Where(v => v.Season >= 0 && v.Ep > 0).Select(v => (Math.Max(1, v.Season), v.Ep)).ToHashSet();
        var official = names.Keys.Select(k => k.Split(':')).Where(p => p.Length == 2 && int.TryParse(p[0], out _) && int.TryParse(p[1], out _))
            .Select(p => (S: int.Parse(p[0]), E: int.Parse(p[1]))).Where(x => x.S >= 1).ToList();
        return official.GroupBy(x => x.S).OrderBy(g => g.Key)
            .Select(g => (g.Key, g.Where(x => !have.Contains(x)).Select(x => x.E).OrderBy(x => x).ToList(), g.Count()))
            .Where(x => x.Item2.Count > 0).ToList();
    }

    // 1,2,3,5,7,8 -> "E1–3, E5, E7–8"
    static string Ranges(List<int> eps)
    {
        var parts = new List<string>();
        for (int i = 0; i < eps.Count; i++)
        {
            int start = eps[i];
            while (i + 1 < eps.Count && eps[i + 1] == eps[i] + 1) i++;
            parts.Add(eps[i] == start ? $"E{start}" : $"E{start}–{eps[i]}");
        }
        return string.Join(", ", parts);
    }

    void OnMissing(object s, RoutedEventArgs e)
    {
        if (Detail.DataContext is not Entry entry || InfoService.Cached(entry.InfoKey)?.EpisodeNames is not { Count: > 0 } names) return;
        var missing = Missing(entry, names);
        MissingItems.Children.Clear();
        TextBlock Line(string text, double size = 14, bool bold = false, double top = 0, Brush? color = null) =>
            new() { Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(0, top, 0, 0),
                TextWrapping = TextWrapping.Wrap, Foreground = color ?? (Brush)FindResource("Text") };
        int count = missing.Sum(m => m.Eps.Count);
        if (count == 0)
        {
            MissingItems.Children.Add(Line("You have every episode", 16, true));
            MissingItems.Children.Add(Line("Nothing missing from the aired seasons (specials not counted).", 13, top: 4, color: (Brush)FindResource("Muted")));
        }
        else
        {
            MissingItems.Children.Add(Line($"Missing {count} episode{(count == 1 ? "" : "s")}", 16, true));
            foreach (var (season, eps, total) in missing)
            {
                var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
                var label = Line($"Season {season}", 14, true);
                label.Width = 92;
                DockPanel.SetDock(label, Dock.Left);
                row.Children.Add(label);
                // a whole season, a few by name, or a compact list of numbers
                string what = eps.Count == total ? $"All {total} episodes"
                    : eps.Count <= 3 ? string.Join("\n", eps.Select(ep => $"E{ep} · {names.GetValueOrDefault($"{season}:{ep}")}"))
                    : $"{Ranges(eps)}  ({eps.Count} of {total})";
                row.Children.Add(Line(what, 14, color: new SolidColorBrush(Color.FromArgb(0xDD, 255, 255, 255))));
                MissingItems.Children.Add(row);
            }
        }
        MissingItems.Children.Add(Line("Compared with the show's official episode list. Episodes that haven't aired yet and specials aren't counted.", 12, top: 14, color: (Brush)FindResource("Muted")));
        GlassFrost.Grab(MissingBtn); // the screen behind it, before it covers it
        MissingPopup.IsOpen = true;
    }

    void OnVideo(object sender, RoutedEventArgs e)
    {
        var entry = (Entry)Detail.DataContext;
        returnTo = Detail;
        Play(entry, entry.Videos.IndexOf((Video)((FrameworkElement)sender).DataContext));
    }

    async void OnBack(object? sender, RoutedEventArgs? e)
    {
        if (Player.IsVisible)
        {
            // Leave right away; mpv saves the resume point and shuts down (~1.3 s) in the background.
            if (mpv is { } p) { mpv = null; session++; lastStop = StopMpv(p); }
            LeavePlayer();
        }
        else if (Detail.IsVisible) Show(Library);
        else if (Library.IsVisible) Show(Home);
    }

    // Keyboard focus never reaches mpv's window (it lives in another process and WPF keeps focus),
    // so while playing, keys are forwarded to mpv over IPC. Printable characters come via OnText
    // so they match the keyboard layout; this handles named keys and Ctrl/Alt shortcuts.
    void OnKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (FixLayer.IsVisible)
        {
            if (key == Key.Escape) { CloseFix(); e.Handled = true; }
            return;
        }
        if (Keyboard.FocusedElement is TextBox)
        {
            if (key == Key.Escape) { SearchBox.Text = ""; Keyboard.ClearFocus(); Focus(); e.Handled = true; }
            return;
        }
        if (SettingsLayer.IsVisible)
        {
            if (key == Key.Escape) { CloseSettings(); e.Handled = true; }
            return;
        }
        if (!(Player.IsVisible && ipc != null) || (!fullscreen && key is Key.Escape or Key.Back))
        {
            if (key is Key.Escape or Key.Back) { OnBack(null, null); e.Handled = true; }
            return;
        }
        var mods = Keyboard.Modifiers;
        if (key == Key.U && mods == ModifierKeys.Control) { ToggleUpscaleNow(); controls?.Activity(); e.Handled = true; return; } // AI upscaling on/off
        var name = MpvKey(key);
        if (name == null && (mods & (ModifierKeys.Control | ModifierKeys.Alt)) != 0 && key is >= Key.A and <= Key.Z)
            name = key.ToString().ToLowerInvariant();
        if (name == null) return; // printable: arrives in OnText
        if (mods.HasFlag(ModifierKeys.Control)) name = "Ctrl+" + name;
        if (mods.HasFlag(ModifierKeys.Alt)) name = "Alt+" + name;
        if (mods.HasFlag(ModifierKeys.Shift) && MpvKey(key) != null) name = "Shift+" + name;
        SendKey(name);
        e.Handled = true;
    }

    void OnText(object sender, TextCompositionEventArgs e)
    {
        if (!Player.IsVisible || ipc == null) return;
        foreach (var c in e.Text) SendKey(c == '#' ? "SHARP" : c.ToString());
        e.Handled = true;
    }

    static string? MpvKey(Key k) => k switch
    {
        Key.Space => "SPACE", Key.Enter => "ENTER", Key.Escape => "ESC", Key.Tab => "TAB",
        Key.Back => "BS", Key.Delete => "DEL", Key.Insert => "INS",
        Key.Left => "LEFT", Key.Right => "RIGHT", Key.Up => "UP", Key.Down => "DOWN",
        Key.PageUp => "PGUP", Key.PageDown => "PGDWN", Key.Home => "HOME", Key.End => "END",
        >= Key.F1 and <= Key.F12 => k.ToString(),
        Key.MediaPlayPause => "PLAYPAUSE", Key.MediaNextTrack => "NEXT", Key.MediaPreviousTrack => "PREV", Key.MediaStop => "STOP",
        _ => null,
    };

    void SendKey(string name)
    {
        Send(new object[] { "keypress", name });
        controls?.Activity();
    }

    void Send(object[] command)
    {
        try { ipc?.WriteLine(JsonSerializer.Serialize(new { command })); } catch { }
    }

    // mpv's expand-text with a reply (for the debug panel). Replies come back through Listen.
    Task<string?> Expand(string text) => Ask(new object[] { "expand-text", text });

    // A command whose reply we wait for: mpv answers once it has carried it out (data string, or null).
    int reqId = 1000;
    readonly Dictionary<int, TaskCompletionSource<string?>> pending = new();
    Task<string?> Ask(object[] command)
    {
        if (ipc == null) return Task.FromResult<string?>(null);
        int id = ++reqId;
        var tcs = new TaskCompletionSource<string?>();
        pending[id] = tcs;
        try { ipc.WriteLine(JsonSerializer.Serialize(new { command, request_id = id })); }
        catch { pending.Remove(id); return Task.FromResult<string?>(null); }
        _ = Task.Delay(1500).ContinueWith(_ => Dispatcher.Invoke(() => { if (pending.Remove(id)) tcs.TrySetResult(null); })); // no reply: give up
        return tcs.Task;
    }

    // ---------- Player (mpv inside the app, MpvVideo.cs; controlled over its JSON pipe) ----------

    // ---------- video fills the window ----------

    double videoAspect;
    Rect? sizeBeforePlayer; // window size to go back to after playback
    RECT? sizeBeforePlacement; // same, when playback started maximized

    bool dragSizing; // user is dragging an edge right now
    bool ShapeLocked =>Player.IsVisible && !fullscreen && WindowState == WindowState.Normal && videoAspect > 0;

    // Windowed: make the window exactly the video's shape (keep the width, fit on screen), so there are no bars.
    void FitWindowToVideo()
    {
        if (Player.IsVisible && !fullscreen && WindowState == WindowState.Maximized && videoAspect > 0)
        {
            // maximized: quietly give the restore size the video's shape, so restoring goes straight to it
            var wp = Placement();
            sizeBeforePlacement ??= wp.normal;
            int nw = wp.normal.R - wp.normal.L, nh = (int)Math.Round(nw / videoAspect), mid = (wp.normal.T + wp.normal.B) / 2;
            wp.normal.T = mid - nh / 2; wp.normal.B = wp.normal.T + nh;
            wp.showCmd = 3;
            SetWindowPlacement(Hwnd, ref wp);
            return;
        }
        if (!ShapeLocked) return;
        // the work area of the monitor the window is on (SystemParameters.WorkArea is the main monitor's: a window on a
        // second monitor jumped to the main one when a video started)
        var work = MonitorWorkArea();
        double w = Width, h = w / videoAspect;
        if (h > work.Height) { h = work.Height; w = h * videoAspect; }
        sizeBeforePlayer ??= new Rect(Left, Top, Width, Height);
        double cx = Left + Width / 2, cy = Top + Height / 2;
        Width = w; Height = h;
        Left = Math.Clamp(cx - w / 2, work.Left, Math.Max(work.Left, work.Right - w));
        Top = Math.Clamp(cy - h / 2, work.Top, Math.Max(work.Top, work.Bottom - h));
    }

    // This window's monitor's work area (no taskbar), in the window's own units (WPF device-independent pixels).
    Rect MonitorWorkArea()
    {
        try
        {
            var wa = System.Windows.Forms.Screen.FromHandle(Hwnd).WorkingArea; // real pixels
            var toDip = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            return new Rect(toDip.Transform(new Point(wa.Left, wa.Top)), toDip.Transform(new Point(wa.Right, wa.Bottom)));
        }
        catch { return SystemParameters.WorkArea; }
    }

    // While playing in a normal window, dragging an edge keeps the video's shape.
    IntPtr KeepVideoShape(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Edge drag: hide the overlay and stop re-laying it out on every step (that's what lagged), restore once released.
        if (msg == 0x0232 /* WM_EXITSIZEMOVE */ && dragSizing)
        {
            dragSizing = false;
            PlaceControls(); UpdateFill();
            controls?.EndTransition();
            return IntPtr.Zero;
        }
        if (msg == 0x0214 && !dragSizing && controls != null) { dragSizing = true; controls.BeginTransition(); }
        if (msg != 0x0214 /* WM_SIZING */ || !ShapeLocked) return IntPtr.Zero;
        var r = Marshal.PtrToStructure<RECT>(lParam);
        int edge = wParam.ToInt32(); // 1 left, 2 right, 3 top, 4 top-left, 5 top-right, 6 bottom, 7 bottom-left, 8 bottom-right
        int w = r.R - r.L, h = r.B - r.T;
        if (edge is 3 or 6) { int nw = (int)Math.Round(h * videoAspect); r.R = r.L + nw; }
        else
        {
            int nh = (int)Math.Round(w / videoAspect);
            if (edge is 4 or 5) r.T = r.B - nh; else r.B = r.T + nh;
        }
        Marshal.StructureToPtr(r, lParam, false);
        handled = true;
        return (IntPtr)1;
    }

    // Maximized / fullscreen: if the screen is nearly the video's shape, zoom a touch to fill it
    // (crops a few percent); a clearly different shape (4:3 on widescreen) keeps its side bars.
    bool? lastFill;
    void UpdateFill()
    {
        if (!Player.IsVisible || videoAspect <= 0 || VideoHost.ActualHeight <= 0 || dragSizing) return;
        double hostAspect = VideoHost.ActualWidth / VideoHost.ActualHeight;
        bool fill = Math.Abs(hostAspect / videoAspect - 1) < 0.08;
        if (fill == lastFill) return; // called on every size step while the window glides
        lastFill = fill;
        Send(new object[] { "set_property", "panscan", fill ? 1.0 : 0.0 });
        controls?.SetFill(fill);
    }

    bool autoPop; // pop out once this video's shape is known

    async void Play(Entry entry, int index)
    {
        // already playing (a video dropped on the player): finish the old one cleanly, then play the new one in its place
        if (mpv is { } old)
        {
            RecordWatch();
            Watch.Save();
            mpv = null; // its exit is no longer "leaving the player"
            session++;
            lastStop = StopMpv(old); // mpv saves its resume point as it quits
            controls?.Close();
            controls = null;
        }
        videoAspect = 0;
        lastFill = null; // a new mpv: its zoom-to-fill starts off
        upscaleActive = false; // and its filter chain is empty
        controls?.SetUpscale(0);
        videoWidth = videoHeight = 0;
        ResetUpscale();
        // AI buffer: start paused and let UpdateUpscale resume once the AI is running (or straight away if it won't upscale)
        aiHolding = false;
        bool aiStart = Settings.Current.Upscale && Upscaler.Installed;
        playingEntry = entry;
        autoPop = Settings.Current.AutoPopOut;
        Show(Player);
        // reopening within a second of leaving: let the old mpv finish saving its resume point first
        if (!lastStop.IsCompleted) await lastStop;
        int my = ++session;
        Directory.CreateDirectory(DataDir);
        // Whole folder as an mpv playlist, so next/prev episode work from the player controls.
        var m3u = Path.Combine(DataDir, "playlist.m3u8");
        File.WriteAllLines(m3u, entry.Videos.Select(v => v.Path).Prepend("#EXTM3U"), new UTF8Encoding(false));

        var pipe = $"animeplayer-{Environment.ProcessId}-{my}";
        var args = new[]
        {
            "--drag-and-drop=no", // files dropped on the video come to us instead (DropPlay.cs)
            $@"--input-ipc-server=\\.\pipe\{pipe}",
            "--profile=high-quality",
            // decode on the GPU; AI upscaling needs D3D11 frames (vf_hakusr), which d3d11va gives
            Settings.Current.Upscale && Upscaler.Installed ? "--hwdec=d3d11va,auto-safe" : "--hwdec=auto-safe",
            // shows resume where you left off; openings/endings always play from the start
            $"--save-position-on-quit={(Settings.Current.Resume && entry.Unit != "themes" ? "yes" : "no")}",
            $"--resume-playback={(Settings.Current.Resume && entry.Unit != "themes" ? "yes" : "no")}",
            $"--watch-later-dir={Path.Combine(DataDir, "watch_later")}",
            "--osc=no", "--osd-bar=no",         // our own control bar replaces mpv's
            "--cursor-autohide=no",           // matches the bar's 2s auto-hide
            $"--keep-open={(Settings.Current.Autoplay ? "no" : "yes")}", // autoplay next, or stop at the end
            // language defaults: anime and TV/movies each have their own; our picker then prefers full subtitles over signs-only
            $"--alang={Settings.Langs(Settings.Current.LangFor(entry.Kind).Audio)}",
            $"--slang={Settings.Langs(Settings.Current.LangFor(entry.Kind).Subs)}",
            $"--sid={(Settings.Current.LangFor(entry.Kind).On ? "auto" : "no")}",
            $"--sub-scale={Settings.Current.SubScale.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            "--title=Haku Player",               // name shown in the Windows volume mixer
            "--audio-client-name=Haku Player",
            $"--glsl-shaders={GlassShader}",     // real-time liquid glass under the player bars
            "--border-background=blur",          // letterbox bars show the video, blurred, instead of black
            "--background-blur-radius=48",
            // volume is app-wide; subtitle timing is ours, per video; the AI filter and its start-up pause are Haku's to set
            "--watch-later-options-remove=volume,mute,speed,sub-delay,vf,pause",
            $"--pause={(aiStart ? "yes" : "no")}",
            // read ahead (shown in blue on the timeline): a minute or two of a typical episode; seeks inside it are instant
            "--cache=yes", "--demuxer-max-bytes=256MiB", "--demuxer-max-back-bytes=64MiB",
            $"--volume={volume.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            $"--playlist-start={index}",
            $"--playlist={m3u}",
        };

        try
        {
            var p = new MpvVideo(args);
            mpv = p;
            if (aiStart) AiHold(); // mpv is already paused; this arms the hold and its time limit
            p.Exited += () => { if (mpv == p) { mpv = null; LeavePlayer(); } };
            VideoImage.Source = p.Image;
            SizeVideo();
            _ = Listen(pipe, entry, my);
            controls = new PlayerControls
            {
                Owner = this, Send = Send, Notify = Toast, ToggleUpscale = ToggleUpscaleNow, ApplyUpscale = ApplyUpscaleSetting, SeekStarted = TimelineSeekStart, SeekEnded = TimelineSeekEnd, Seeking = BeforeSeek, Expand = Expand, Ask = Ask,ResumeAllowed = entry.Unit != "themes", Kind = entry.Kind,
                Back = () => OnBack(null, null),
                Minimize = () => WindowState = WindowState.Minimized,
                ToggleMaximize = ToggleMaximizeOrFullscreen,
                ActivateOwner = () => Front(),
                PopOut = TogglePopOut,
                Resizable = () => CanResizeEdges,
                CloseApp = Close,
                DragWindow = DragFromOverlay,
            };
            controls.SetTitle(entry.Name);
            controls.SetUpscaleButton(Settings.Current.Upscale, false, Settings.Current.Upscale ? "AI upscaling: waiting for the video (Ctrl+U)" : "AI upscaling: off (Ctrl+U)");
            controls.SetMaximized(WindowState == WindowState.Maximized);
            controls.SetFullscreen(fullscreen);
            // new bars start shown without raising ShownChanged, so clear a hidden cursor left by the last video
            VideoHost.Cursor = null;
            controls.ShownChanged += on => VideoHost.Cursor = on ? null : Cursors.None;
            Player.Children.Add(controls); // over the video
            PlaceControls();
        }
        catch (Exception ex)
        {
            Toast($"Couldn't play that video: {ex.Message}");
            LeavePlayer();
        }
    }

    async Task Listen(string pipe, Entry entry, int my)
    {
        StreamWriter? w = null;
        try
        {
            using var c = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            await c.ConnectAsync(5000);
            w = new StreamWriter(c) { AutoFlush = true };
            var r = new StreamReader(c);
            ipc = w;
            await w.WriteLineAsync("""{"command":["observe_property",1,"fullscreen"]}""");
            foreach (var prop in new[] { "playlist-pos", "pause", "time-pos", "duration", "volume", "mute", "video-params/aspect", "speed", "track-list", "height", "width", "container-fps", "frame-drop-count", "video-format", "sub-delay", "chapter-list", "demuxer-cache-time" })
                await w.WriteLineAsync($$"""{"command":["observe_property",2,"{{prop}}"]}""");
            string? line;
            while ((line = await r.ReadLineAsync()) != null)
            {
                if (my != session) continue; // we've left this video: ignore its shutdown chatter, but keep the pipe open until mpv closes it
                using var j = JsonDocument.Parse(line);
                var m = j.RootElement;
                if (m.TryGetProperty("request_id", out var rid) && rid.ValueKind == JsonValueKind.Number && pending.Remove(rid.GetInt32(), out var waiting))
                {
                    waiting.TrySetResult(m.TryGetProperty("data", out var rd) && rd.ValueKind == JsonValueKind.String ? rd.GetString() : null);
                    continue;
                }
                if (!m.TryGetProperty("event", out var ev)) continue;
                if (ev.GetString() == "file-loaded") { controls?.FileLoaded(); continue; } // re-add your subtitles + timing
                if (ev.GetString() == "playback-restart") { AiPlaybackRestart(); continue; }
                if (ev.GetString() != "property-change") continue;
                m.TryGetProperty("data", out var d);
                switch (m.GetProperty("name").GetString())
                {
                    case "fullscreen":
                        SetFullscreen(d.ValueKind == JsonValueKind.True);
                        break;
                    case "playlist-pos" when d.ValueKind == JsonValueKind.Number && d.GetInt32() is var pos && pos >= 0 && pos < entry.Videos.Count:
                        controls?.SetTitle($"{entry.Name}  ·  {entry.Videos[pos].Label}");
                        controls?.SetFile(entry.Videos[pos].Path);
                        if (controls != null) controls.NextTitle = entry.Unit != "themes" && pos + 1 < entry.Videos.Count ? entry.Videos[pos + 1].Label : null;
                        playingIndex = pos;
                        fileChapters = new();
                        segmentsFor = null;
                        ResetUpscale(); // the next episode may be another size
                        break;
                    case "chapter-list" when d.ValueKind == JsonValueKind.Array:
                        fileChapters = d.EnumerateArray()
                            .Select(c => new Chapter(c.TryGetProperty("time", out var tm) && tm.ValueKind == JsonValueKind.Number ? tm.GetDouble() : 0,
                                                     c.TryGetProperty("title", out var tt) && tt.ValueKind == JsonValueKind.String ? tt.GetString() ?? "" : ""))
                            .OrderBy(c => c.Time).ToList();
                        controls?.SetChapters(fileChapters);
                        break;
                    case "pause":
                        controls?.SetPause(d.ValueKind == JsonValueKind.True);
                        AiPauseChanged(d.ValueKind == JsonValueKind.True);
                        DiscordUpdate();
                        break;
                    case "time-pos" when d.ValueKind == JsonValueKind.Number:
                        controls?.SetTime(d.GetDouble());
                        if (entry.Unit != "themes" && controls is { CurrentFile.Length: > 0 } cc) Watch.Tick(cc.CurrentFile, d.GetDouble(), cc.Length, cc.EndingStart);
                        // Discord's "time left" is worked out from timestamps: refresh it after a seek
                        if (controls is { Paused: false } pc && Math.Abs(d.GetDouble() - (discordPos + (DateTime.UtcNow - discordAt).TotalSeconds * pc.Speed)) > 5) DiscordUpdate();
                        break;
                    case "duration" when d.ValueKind == JsonValueKind.Number:
                        controls?.SetDuration(d.GetDouble());
                        DiscordUpdate();
                        if (entry.Unit != "themes" && playingIndex >= 0 && playingIndex < entry.Videos.Count) _ = FindSegments(entry, entry.Videos[playingIndex], d.GetDouble(), my);
                        break;
                    case "volume" when d.ValueKind == JsonValueKind.Number:
                        volume = d.GetDouble();
                        controls?.SetVolume(volume);
                        break;
                    case "speed" when d.ValueKind == JsonValueKind.Number:
                        controls?.SetSpeed(d.GetDouble());
                        break;
                    case "track-list":
                        controls?.SetTracks(d);
                        break;
                    case "height" when d.ValueKind == JsonValueKind.Number:
                        videoHeight = d.GetInt32();
                        controls?.SetQuality(videoHeight, videoCodec);
                        UpdateUpscale(my);
                        break;
                    case "width" when d.ValueKind == JsonValueKind.Number:
                        videoWidth = d.GetInt32();
                        UpdateUpscale(my);
                        break;
                    case "demuxer-cache-time" when d.ValueKind == JsonValueKind.Number:
                        controls?.SetCache(d.GetDouble());
                        break;
                    case "container-fps" when d.ValueKind == JsonValueKind.Number:
                        videoFps = d.GetDouble();
                        break;
                    case "frame-drop-count" when d.ValueKind == JsonValueKind.Number:
                        CheckUpscaleDrops(d.GetInt64(), my);
                        break;
                    case "video-format" when d.ValueKind == JsonValueKind.String:
                        videoCodec = d.GetString() ?? "";
                        controls?.SetQuality(videoHeight, videoCodec);
                        break;
                    case "video-params/aspect" when d.ValueKind == JsonValueKind.Number:
                        videoAspect = d.GetDouble();
                        controls?.SetAspect(videoAspect);
                        if (autoPop) { autoPop = false; if (!popped && !fullscreen) { TogglePopOut(); UpdateFill(); break; } }
                        FitWindowToVideo();
                        UpdateFill();
                        break;
                    case "mute":
                        controls?.SetMute(d.ValueKind == JsonValueKind.True);
                        break;
                    case "sub-delay" when d.ValueKind == JsonValueKind.Number:
                        controls?.SetSubDelay(d.GetDouble());
                        break;
                }
            }
        }
        catch { } // pipe closes when mpv exits; Exited handles navigation
        finally { if (ipc == w) ipc = null; }
    }

    // mpv can't fullscreen itself while embedded, so mirror its fullscreen flag onto our window:
    // cover the whole monitor (taskbar included), no header, no rounded corners or resize edges.
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct WINDOWPLACEMENT { public int length, flags, showCmd; public POINT min, max; public RECT normal; }
    [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT p);
    [DllImport("user32.dll")] static extern bool SetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT p);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int cy, uint flags);
    WINDOWPLACEMENT placementBeforeFullscreen;

    // Glide from the current size to the whole monitor and back.
    void SetFullscreen(bool on)
    {
        if (on == fullscreen) return;
        if (on && popped) UnpopNow();
        fullscreen = on;
        WindowChrome.GetWindowChrome(this).ResizeBorderThickness = new Thickness(on ? 0 : 7);
        Dwm(33, on ? 1 : 2);
        controls?.SetFullscreen(on);
        UpdatePanelEdge();
        if (on)
        {
            placementBeforeFullscreen = Placement();
            var sc = System.Windows.Forms.Screen.FromHandle(Hwnd).Bounds;
            Transition(new RECT { L = sc.Left, T = sc.Top, R = sc.Right, B = sc.Bottom }, null, () =>
            {
                if (WindowState == WindowState.Maximized)
                {
                    // become a normal window at the same spot first, so nothing jumps
                    GetWindowRect(Hwnd, out var cur);
                    var wp = placementBeforeFullscreen;
                    wp.showCmd = 1; wp.normal = cur;
                    SetWindowPlacement(Hwnd, ref wp);
                }
                Shell.Margin = new Thickness(0);
            });
        }
        else
        {
            var before = placementBeforeFullscreen;
            bool wasMax = before.showCmd == 3;
            Transition(wasMax ? MaxRect() : before.normal, () =>
            {
                if (wasMax) MaximizeKeeping(before.normal); else SetWindowPlacement(Hwnd, ref before);
                FitMaximized();
                FitWindowToVideo();
            });
        }
    }

    Entry? playingEntry;

    // Remember the episode for the home page's "Continue watching"; a finished one moves on to the next.
    void RecordWatch()
    {
        if (playingEntry is not { Unit: not "themes" } e || controls is not { CurrentFile.Length: > 0 } c) return;
        int i = e.Videos.FindIndex(v => string.Equals(v.Path, c.CurrentFile, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return;
        bool finished = c.Length > 0 && c.Position >= c.Length - 90; // into the end credits
        if (!finished) Settings.Current.LastWatched = new LastWatch { Folder = e.Folder, Path = c.CurrentFile, Position = c.Position, Duration = c.Length };
        else Settings.Current.LastWatched = e.Kind != "movie" && i + 1 < e.Videos.Count ? new LastWatch { Folder = e.Folder, Path = e.Videos[i + 1].Path } : null;
        Settings.Current.Save();
    }

    void LeavePlayer()
    {
        RecordWatch();
        Watch.Save();
        Discord.Clear();
        controls?.Close();
        controls = null;
        UnpopNow();
        SetFullscreen(false);
        videoAspect = 0; // unlock the window shape
        if (sizeBeforePlayer is Rect b && WindowState == WindowState.Normal)
        {
            Left = b.Left; Top = b.Top; Width = b.Width; Height = b.Height; // back to the browsing size
        }
        if (sizeBeforePlacement is RECT n && WindowState == WindowState.Maximized)
        {
            var wp = Placement(); wp.normal = n; wp.showCmd = 3; SetWindowPlacement(Hwnd, ref wp);
        }
        sizeBeforePlayer = null;
        sizeBeforePlacement = null;
        SaveVolume();
        Show(returnTo);
        Front();
    }

    static double LoadVolume()
    {
        try { return Math.Clamp(double.Parse(File.ReadAllText(VolumeFile), System.Globalization.CultureInfo.InvariantCulture), 0, 100); }
        catch { return 100; }
    }

    void SaveVolume()
    {
        try { Directory.CreateDirectory(DataDir); File.WriteAllText(VolumeFile, volume.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        catch { }
    }

    int session;                              // bumped per video session, so a closing mpv can't touch the next one
    Task lastStop = Task.CompletedTask;       // previous mpv still shutting down

    async Task StopMpv(MpvVideo? p)
    {
        if (p == null) return;
        var w = ipc;
        try { if (w != null) await w.WriteLineAsync("""{"command":["quit"]}"""); } catch { }
        if (await Task.WhenAny(p.Stopped, Task.Delay(3000)) != p.Stopped) { p.Kill(); await Task.WhenAny(p.Stopped, Task.Delay(3000)); }
    }

    async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (mpv == null) return;
        RecordWatch();
        e.Cancel = true; // let mpv save the resume position first
        await StopMpv(mpv);
        mpv = null;
        SaveVolume();
        Close();
    }
}

sealed class Natural : IComparer<string?>
{
    public static readonly Natural I = new();
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] static extern int StrCmpLogicalW(string a, string b);
    public int Compare(string? a, string? b) => StrCmpLogicalW(a ?? "", b ?? ""); // "OP2" before "OP10"
}

// Wheel scrolling that glides to the target instead of jumping 48px per notch.
static class SmoothScroll
{
    public static void Attach(ScrollViewer sv, Action<bool>? scrolling = null)
    {
        double target = 0;
        bool running = false;
        void Tick(object? s, EventArgs e)
        {
            double cur = sv.VerticalOffset, step = (target - cur) * 0.22;
            if (Math.Abs(target - cur) < 0.5) { sv.ScrollToVerticalOffset(target); CompositionTarget.Rendering -= Tick; running = false; scrolling?.Invoke(false); return; }
            sv.ScrollToVerticalOffset(cur + step);
        }
        sv.PreviewMouseWheel += (_, e) =>
        {
            if (!running) target = sv.VerticalOffset;
            target = Math.Clamp(target - e.Delta * 1.6, 0, sv.ScrollableHeight);
            if (!running) { CompositionTarget.Rendering += Tick; running = true; scrolling?.Invoke(true); }
            e.Handled = true;
        };
        sv.ScrollChanged += (_, e) => { if (!running) target = sv.VerticalOffset; };
    }
}
