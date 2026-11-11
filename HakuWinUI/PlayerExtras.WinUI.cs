using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AnimePlayer;

// The rest of the WinUI player, as the WPF app's PlayerControls and MainWindow do it:
//   - click the video to pause, double-click for fullscreen, the wheel for volume;
//   - drag the subtitles up or down (saved for every video);
//   - the subtitle track picked for you (your language, full dialogue over "Signs & Songs"), your own subtitle files and
//     their timing, kept per video;
//   - the menu's player settings, the Autoplay button, the quality badge, and the debug panel.
public partial class MainWindow
{
    record struct Track(int Id, string Type, string Lang, string Title, bool Selected, bool Default, bool Forced)
    {
        public string Label => string.Join(" · ", new[] { Lang.Length > 0 ? Language(Lang) : "", Title }.Where(x => x.Length > 0)) is { Length: > 0 } l ? l : $"Track {Id}";
    }
    List<Track> trackList = new();
    string currentFile = "";
    string? loadedFor, pickSubsFor;
    double subDelay;
    bool subShowing, suppressTap, subDragged, menuJustClosed, overBars, cursorHidden;
    double? subDragFrom; double subDragStart;
    static readonly string SubsDir = Path.Combine(Settings.DataDir, "subs");
    Border debugPanel = null!;
    readonly TextBlock debugText = new() { FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12.5, LineHeight = 19, Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)) };
    Microsoft.UI.Dispatching.DispatcherQueueTimer? debugTimer;
    int qualityHeight; string qualityCodec = "";

    void SetupPlayerExtras()
    {
        SetupMenu();
        // click: pause; double-click: fullscreen (undoing the first click's pause); wheel: volume
        videoHost.Tapped += (_, e) => { if (suppressTap) { suppressTap = false; return; } if (CloseMenu()) { menuJustClosed = true; return; } menuJustClosed = false; if (core != null) Send("cycle", "pause"); };
        root.DoubleTapped -= OnDoubleTap; root.DoubleTapped += OnDoubleTap;
        // the wheel over the video or the pill: volume, 5 a notch
        void Wheel(object s, PointerRoutedEventArgs e)
        {
            if (core == null) return;
            int delta = e.GetCurrentPoint((UIElement)s).Properties.MouseWheelDelta;
            volume = Math.Clamp(volume + Math.Sign(delta) * 5, 0, 100);
            bar.SetVolume(volume);
            Send("set_property", "volume", volume);
            Activity();
            e.Handled = true;
        }
        videoHost.PointerWheelChanged += Wheel;
        bar.Pill.PointerWheelChanged += Wheel;
        // the bars don't hide while the pointer is on them
        foreach (var el in new UIElement[] { bar.Pill, topBar.Panel })
        {
            el.PointerEntered += (_, _) => overBars = true;
            // WinUI also raises this under a still pointer (re-hit-testing after layout): only a real move counts
            el.PointerExited += (_, _) => { overBars = false; if (PointerReallyMoved()) Activity(); };
        }
        // a click anywhere but the open menu (and its own buttons) closes it
        root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (openMenu == null || e.OriginalSource is not DependencyObject d) return;
            if (IsInside(d, menuPanel) || IsInside(d, bar.TracksButton) || IsInside(d, bar.SpeedButton) || IsInside(d, bar.MenuButton) || IsInside(d, videoHost)) return;
            CloseMenu();
        }), true);
        videoHost.SizeChanged += (_, _) => UpdateFill();
        // the subtitles: press on them and drag up or down
        videoHost.PointerPressed += (_, e) =>
        {
            double y = e.GetCurrentPoint(videoHost).Position.Y;
            if (core == null || !subShowing || !OnSubtitles(y)) return;
            subDragFrom = y; subDragStart = Settings.Current.SubPos; subDragged = false;
            videoHost.CapturePointer(e.Pointer);
        };
        videoHost.PointerMoved += (_, e) =>
        {
            if (subDragFrom is not double from)
            {
                // a hint that they can be moved: the up/down cursor over them while the bars are up
                if (barTarget > 0 && !cursorHidden) videoHost.SetCursor(subShowing && OnSubtitles(e.GetCurrentPoint(videoHost).Position.Y) ? Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.SizeNorthSouth) : null);
                return;
            }
            double y = e.GetCurrentPoint(videoHost).Position.Y;
            if (!subDragged && Math.Abs(y - from) < 4) return; // a click, not a drag (yet)
            subDragged = true;
            double pos = Math.Clamp(subDragStart + (y - from) / Math.Max(1, videoHost.ActualHeight) * 100, 8, 100);
            if (pos > 98.5) pos = 100; // snaps home
            Settings.Current.SubPos = Math.Round(pos, 1);
            Send("set_property", "sub-pos", Settings.Current.SubPos);
        };
        videoHost.PointerReleased += (_, e) =>
        {
            if (subDragFrom == null) return;
            subDragFrom = null;
            videoHost.ReleasePointerCapture(e.Pointer);
            if (!subDragged) return; // a click: Tapped pauses
            suppressTap = true;
            Settings.Current.Save();
            Toast(Settings.Current.SubPos >= 99.5 ? "Subtitles back in their usual place" : "Subtitles stay here for every video · Reset in the player menu");
        };
        var dbg = new StackPanel();
        dbg.Children.Add(new TextBlock { Text = "DEBUG", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Opacity = 0.55, Foreground = GlassLook.TextB, Margin = new Thickness(0, 0, 0, 6) });
        dbg.Children.Add(debugText);
        debugPanel = GlassLook.Panel(dbg, 14, new Thickness(16, 12, 16, 12));
        debugPanel.Visibility = Visibility.Collapsed; debugPanel.IsHitTestVisible = false;
        debugPanel.HorizontalAlignment = HorizontalAlignment.Left; debugPanel.VerticalAlignment = VerticalAlignment.Top; debugPanel.Margin = new Thickness(24, 92, 24, 0);
        root.Children.Add(debugPanel);
        bar.ToggleAutoplay = ToggleAutoplay;
        bar.SetAutoplay(Settings.Current.Autoplay);
    }

    void OnDoubleTap(object s, DoubleTappedRoutedEventArgs e)
    {
        if (core == null || menuJustClosed) return;
        if (e.OriginalSource is DependencyObject d && IsInside(d, videoHost)) Send("cycle", "pause"); // undo the first click's pause
        Send("cycle", "fullscreen");
        e.Handled = true;
    }

    static bool IsInside(DependencyObject d, DependencyObject parent)
    {
        for (var x = d; x != null; x = VisualTreeHelper.GetParent(x)) if (x == parent) return true;
        return false;
    }

    // the pointer disappears over the video while the bars are hidden, as in the WPF app (Win32's cursor count, so balanced)
    // The pointer disappears over the video while the bars are hidden, as in the WPF app. In WinUI the pointer over
    // content is the element's ProtectedCursor (Win32 ShowCursor / SetCursor only reach the window frame:
    // microsoft-ui-xaml#10128), so the video area gets a transparent cursor (HiddenCursor); if one can't be made, a
    // closed cursor, which also shows as nothing (WindowsAppSDK discussion #3601). The arrow again when the bars return.
    void HideCursor(bool hide)
    {
        if (hide == cursorHidden) return;
        cursorHidden = hide;
        if (hide)
        {
            if (HiddenCursor.Get() is { } clear) videoHost.SetCursor(clear);
            else
            {
                var none = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Arrow);
                videoHost.SetCursor(none);
                none.Dispose(); // closed: no picture
            }
        }
        else videoHost.SetCursor(null);
        RefreshCursor();
        MpvCore.Note(hide ? "pointer hidden" : "pointer shown");
        if (hide && MpvCore.Trace != null) _ = TraceCursor();
        if (hide) cursorGuard.Start(); else cursorGuard.Stop();
    }

    // WinUI re-applies a cursor on its own layout passes without asking the window (no WM_SETCURSOR, so the hook above
    // never hears of it: microsoft-ui-xaml#11942), which brought the arrow back mid-playback. While hidden, check what
    // Windows is showing and ask again if it isn't ours.
    Microsoft.UI.Dispatching.DispatcherQueueTimer? guard;
    Microsoft.UI.Dispatching.DispatcherQueueTimer cursorGuard
    {
        get
        {
            if (guard != null) return guard;
            guard = DispatcherQueue.CreateTimer();
            guard.Interval = TimeSpan.FromMilliseconds(250);
            guard.Tick += (_, _) =>
            {
                var ci = new CURSORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<CURSORINFO>() };
                if (!cursorHidden || HiddenCursor.Handle == IntPtr.Zero || !GetCursorInfo(ref ci) || (ci.flags & 1) == 0 || ci.hCursor == HiddenCursor.Handle) return;
                if (WindowFromPoint(ci.pt) != cursorWindow) return; // over another window: not ours to set
                MpvCore.Note($"pointer: WinUI put cursor {ci.hCursor:X} back while hidden, hiding it again");
                RefreshCursor();
            };
            return guard;
        }
    }

    // trace: what Windows is really showing a moment after hiding (our transparent cursor, or what instead), and what's
    // under the pointer
    async System.Threading.Tasks.Task TraceCursor()
    {
        await System.Threading.Tasks.Task.Delay(200);
        var ci = new CURSORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<CURSORINFO>() };
        GetCursorInfo(ref ci);
        var under = WindowFromPoint(ci.pt);
        var cls = new System.Text.StringBuilder(64); GetClassName(under, cls, cls.Capacity);
        GetWindowThreadProcessId(under, out uint pid);
        MpvCore.Note($"cursor check: showing={(ci.flags & 1) != 0}, cursor {ci.hCursor:X} (ours {HiddenCursor.Handle:X}, arrow {LoadCursor(IntPtr.Zero, 32512):X}, hand {LoadCursor(IntPtr.Zero, 32649):X}), " +
                     $"at {ci.pt.X},{ci.pt.Y} over {cls} ({(pid == (uint)Environment.ProcessId ? "Haku" : $"process {pid}")}), still hidden: {cursorHidden}, over bars: {overBars}");
        // the XAML elements under the pointer, top first (the cursor comes from the first one that sets one)
        var client = ci.pt; ScreenToClient(hwnd, ref client);
        double k = root.XamlRoot?.RasterizationScale ?? 1;
        var hits = Microsoft.UI.Xaml.Media.VisualTreeHelper.FindElementsInHostCoordinates(new Windows.Foundation.Point(client.X / k, client.Y / k), root)
            .Take(6).Select(e => $"{e.GetType().Name}{(e is FrameworkElement { Name.Length: > 0 } f ? "#" + f.Name : "")}{(e == videoHost ? "(video area)" : "")}");
        MpvCore.Note($"cursor check: under the pointer: {string.Join(" > ", hits)}; video area cursor: {videoHost.CurrentCursor?.GetType().Name ?? "none"}");
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public POINT pt; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO ci);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr h, ref POINT p);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr LoadCursor(IntPtr inst, int id);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);

    // WinUI sets the pointer only when a mouse event comes in, so a cursor changed while the mouse is still shows only
    // after the next move (which brings the bars back). The trace showed it: the video area held the transparent cursor
    // and Windows kept drawing the arrow; a click showed it for a frame. Windows asks the window under the pointer which
    // cursor to show (WM_SETCURSOR), and that is WinUI's DesktopChildSiteBridge. So that window is hooked: while hidden it
    // answers with the transparent cursor, and it's asked once straight away (on its own thread, where SetCursor counts).
    IntPtr cursorWindow, cursorWindowProc;
    WndProc? cursorHook;
    void RefreshCursor()
    {
        if (cursorWindow == IntPtr.Zero)
        {
            EnumChildWindows(hwnd, (h, _) =>
            {
                var sb = new System.Text.StringBuilder(64);
                GetClassName(h, sb, sb.Capacity);
                if (sb.ToString() != "Microsoft.UI.Content.DesktopChildSiteBridge") return true;
                cursorWindow = h; return false;
            }, IntPtr.Zero);
            if (cursorWindow == IntPtr.Zero) { MpvCore.Note("pointer: WinUI's content window not found"); return; }
            cursorHook = (h, msg, w, l) =>
            {
                if (msg == 0x0020 /* WM_SETCURSOR */ && cursorHidden && ((long)l & 0xFFFF) == 1 /* HTCLIENT */ && HiddenCursor.Handle != IntPtr.Zero)
                {
                    SetCursor(HiddenCursor.Handle);
                    return (IntPtr)1;
                }
                return CallWindowProc(cursorWindowProc, h, msg, w, l);
            };
            cursorWindowProc = SetWindowLongPtr(cursorWindow, -4 /* GWLP_WNDPROC */, System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(cursorHook));
            GetWindowThreadProcessId(cursorWindow, out _);
            MpvCore.Note($"pointer: hooked WinUI's content window {cursorWindow:X} (thread {GetWindowThreadProcessId(cursorWindow, out _)}, this thread {GetCurrentThreadId()})");
        }
        // ask it now (only when the pointer is over it: never touches the pointer over another window)
        if (!GetCursorPos(out var pt) || WindowFromPoint(pt) != cursorWindow) return;
        SendMessage(cursorWindow, 0x0020, cursorWindow, (IntPtr)(1 | 0x0200 << 16));
    }

    // WinUI remembers which element is under the pointer and only looks again when the mouse moves; meanwhile it keeps
    // putting that element's cursor back on every layout pass (microsoft-ui-xaml#11942). After a page change under a still
    // pointer (an episode clicked: the video takes the page's place) it still thinks the pointer is over what was clicked,
    // so the video's hidden cursor never showed until you clicked the video. A real mouse move makes it look again: the
    // pointer goes 1 px over and straight back (SetCursorPos: no input reaches other apps). Only when Haku is in front and
    // the pointer is over it.
    async void PointerLookAgain()
    {
        if (GetForegroundWindow() != hwnd || !GetCursorPos(out var pt)) return;
        var under = WindowFromPoint(pt);
        GetWindowThreadProcessId(under, out uint pid);
        if (under == IntPtr.Zero || pid != (uint)Environment.ProcessId) return;
        int x = pt.X == 0 ? 1 : pt.X - 1;
        SetCursorPos(x, pt.Y);
        await System.Threading.Tasks.Task.Delay(30); // WinUI sees the move (moved and back at once, Windows may merge them away)
        if (GetCursorPos(out var now) && now.X == x && now.Y == pt.Y) SetCursorPos(pt.X, pt.Y); // unless you've moved it since
        MpvCore.Note($"pointer: page changed under it, WinUI told to look again at {pt.X},{pt.Y}");
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

    delegate bool EnumProc(IntPtr h, IntPtr l);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc f, IntPtr l);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SetCursor(IntPtr c);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")] static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();


    // Pointer activity: only when the pointer has really moved on the screen. Windows and WinUI send moves that aren't
    // the mouse: when the window is resized under a still pointer (the video sets its shape), when the cursor is changed
    // (WinUI raises a PointerMoved: hiding it would bring it straight back), and the cursor refresh. Positions inside
    // the window change with the window, so it's the screen position that's compared, as the WPF app does.
    POINT lastScreenAt;
    bool PointerReallyMoved()
    {
        if (!GetCursorPos(out var p)) return true;
        if (p.X == lastScreenAt.X && p.Y == lastScreenAt.Y) return false;
        lastScreenAt = p;
        return true;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    // Maximized or fullscreen: if the screen is nearly the video's shape, zoom a touch to fill it (crops a few percent);
    // a clearly different shape (4:3 on widescreen) keeps its side bars.
    bool? lastFill;
    void UpdateFill()
    {
        if (core == null || videoAspect <= 0 || videoHost.ActualHeight <= 0) return;
        bool fill = Math.Abs(videoHost.ActualWidth / videoHost.ActualHeight / videoAspect - 1) < 0.08;
        if (fill == lastFill) return;
        lastFill = fill;
        Send("set_property", "panscan", fill ? 1.0 : 0.0);
        SendGlass(); // the glass's picture area changes with it
    }

    // roughly where the subtitle lines are: sub-pos is the bottom of the text as a % of the height; two lines above it
    bool OnSubtitles(double y)
    {
        double h = videoHost.ActualHeight, line = h * Settings.Current.SubPos / 100;
        return h > 0 && y > line - h * 0.2 && y < line + h * 0.03;
    }

    // ---------- tracks and subtitles ----------

    void SetTracks(JsonElement list)
    {
        trackList = list.EnumerateArray().Select(t => new Track(
            t.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : 0, Str(t, "type"), Str(t, "lang"), Str(t, "title"),
            t.TryGetProperty("selected", out var s) && s.ValueKind == JsonValueKind.True, t.TryGetProperty("default", out var df) && df.ValueKind == JsonValueKind.True,
            t.TryGetProperty("forced", out var fo) && fo.ValueKind == JsonValueKind.True)).ToList();
        // the file's first track list: its subtitle track can be picked now (FileLoaded may have come first)
        if (openMenu == "tracks") BuildMenu();
        if (pickSubsFor == currentFile && currentFile != "" && trackList.Any(t => t.Type == "sub")) { pickSubsFor = null; PickSubs(); }
        else if (loadedFor != currentFile && currentFile != "") FileLoaded();
    }

    // Subtitle choice: your language first, and (if set) full dialogue over "Signs & Songs" / forced tracks, which anime
    // releases often mark as the default. With "File default" chosen, mpv's own pick stands.
    void PickSubs()
    {
        var S = Settings.Current;
        var pref = S.LangFor(entry?.Kind ?? "anime");
        var langs = Settings.Langs(pref.Subs).Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (!pref.On || langs.Length == 0) return;
        static bool Has(string s, string pattern) => System.Text.RegularExpressions.Regex.IsMatch(s, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var best = trackList.Where(t => t.Type == "sub")
            .Select(t => (t.Id, t.Selected, Score:
                (langs.Contains(t.Lang) || (t.Lang == "" && langs.Any(l => l.Length > 3 && t.Title.Contains(l, StringComparison.OrdinalIgnoreCase))) ? 100 : 0)
                + (S.FullSubs && (t.Forced || Has(t.Title, "sign|song|forced|karaoke")) ? -50 : 0)
                + (Has(t.Title, "full|dialog") ? 10 : 0)
                + (t.Default ? 2 : 0)))
            .OrderByDescending(x => x.Score).FirstOrDefault();
        if (best.Id > 0 && best.Score >= 50 && !best.Selected) Send("set_property", "sid", best.Id); // only when it's in your language
    }

    // A video finished loading: bring back its added subtitles (newest selected) and its timing, else pick the best track.
    async void FileLoaded()
    {
        var file = currentFile;
        if (loadedFor == file || file == "") return;
        loadedFor = file;
        var S = Settings.Current;
        if (S.ExtraSubs.TryGetValue(file, out var list) && list.Any(File.Exists))
        {
            var present = list.Where(File.Exists).ToList();
            for (int i = 0; i < present.Count; i++)
                Send("sub-add", present[i], i == present.Count - 1 ? "select" : "auto", Path.GetFileNameWithoutExtension(present[i]));
        }
        else
        {
            pickSubsFor = file;
            await Task.Delay(300);
            if (file == currentFile && pickSubsFor == file && trackList.Any(t => t.Type == "sub")) { pickSubsFor = null; PickSubs(); }
        }
        Send("set_property", "sub-delay", S.SubDelays.GetValueOrDefault(file));
    }

    async Task AddSubtitleFile()
    {
        if (currentFile == "") return;
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        foreach (var ext in new[] { ".srt", ".ass", ".ssa", ".vtt", ".sub", ".sup" }) picker.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var f = await picker.PickSingleFileAsync();
        if (f == null) return;
        try
        {
            var folder = Path.Combine(SubsDir, Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(currentFile.ToLowerInvariant())))[..16]);
            Directory.CreateDirectory(folder);
            var copy = Path.Combine(folder, Path.GetFileName(f.Path));
            File.Copy(f.Path, copy, true);
            var S = Settings.Current;
            if (!S.ExtraSubs.TryGetValue(currentFile, out var list)) S.ExtraSubs[currentFile] = list = new();
            list.RemoveAll(p => string.Equals(p, copy, StringComparison.OrdinalIgnoreCase));
            list.Add(copy);
            S.Save();
            Send("sub-add", copy, "select", Path.GetFileNameWithoutExtension(copy));
        }
        catch (Exception ex) { Toast($"Couldn't add that subtitle file: {ex.Message}"); }
        Activity();
    }

    // mpv's subtitle delay changed (the menu, or its z / x keys): remember it for this video
    void SetSubDelay(double v)
    {
        v = Math.Round(v, 2);
        if (v == subDelay) return;
        subDelay = v;
        if (currentFile == "") return;
        var S = Settings.Current;
        if (v == 0) S.SubDelays.Remove(currentFile); else S.SubDelays[currentFile] = v;
        S.Save();
        if (openMenu == "tracks") BuildMenu();
    }

    void ToggleAutoplay()
    {
        var S = Settings.Current;
        S.Autoplay = !S.Autoplay; S.Save();
        Send("set_property", "keep-open", S.Autoplay ? "no" : "yes");
        bar.SetAutoplay(S.Autoplay);
    }

    // "1080p → 4K  H264  AI", "1080p  HEVC"
    void UpdateQuality()
    {
        static string Res(int h) => h >= 2000 ? "4K" : h >= 1400 ? "1440p" : h > 0 ? $"{h}p" : "";
        string res = Res(qualityHeight);
        if (upscaleHeight > 0 && res != "") res += $" → {Res(upscaleHeight)}";
        topBar.SetBadge(string.Join("  ", new[] { res, qualityCodec.ToUpperInvariant(), upscaleHeight > 0 ? upscaleTag : "" }.Where(x => x != "")));
    }

    // ---------- the menus over the pill: audio & subtitles, speed, player settings (WPF PlayerControls' glass menu) ----------

    readonly StackPanel menuItems = new();
    readonly ScrollViewer menuScroll = new() { MaxHeight = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    Border menuPanel = null!;
    string? openMenu;

    void SetupMenu()
    {
        menuScroll.Content = menuItems;
        SmoothScroll.Attach(menuScroll);
        menuPanel = GlassLook.VideoPanel(new Border { Padding = new Thickness(8), Child = menuScroll }, 18, Color.FromArgb(0x38, 0x08, 0x0A, 0x10));
        menuPanel.HorizontalAlignment = HorizontalAlignment.Left;
        menuPanel.VerticalAlignment = VerticalAlignment.Bottom;
        menuPanel.Visibility = Visibility.Collapsed;
        menuPanel.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(140) };
        root.Children.Add(menuPanel);
        bar.OpenMenu = ToggleMenu;
    }

    void ToggleMenu(string which, FrameworkElement anchor)
    {
        if (openMenu == which) { CloseMenu(); return; }
        openMenu = which;
        double W = root.ActualWidth;
        menuPanel.Width = Math.Min(which == "settings" ? 360 : which == "tracks" ? 380 : 300, Math.Max(200, W - 16));
        // fits between the top bar and the pill; the rest scrolls
        double pillTop = bar.Pill.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
        double top = topBar.Panel.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(0, topBar.Panel.ActualHeight)).Y + 10;
        menuScroll.MaxHeight = Math.Clamp(pillTop - 14 - top - 16, 80, 520);
        BuildMenu();
        var a = anchor.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(anchor.ActualWidth / 2, 0));
        menuPanel.Margin = new Thickness(Math.Clamp(a.X - menuPanel.Width / 2, 8, Math.Max(8, W - menuPanel.Width - 8)), 0, 0, root.ActualHeight - pillTop + 14);
        menuPanel.Opacity = 0;
        menuPanel.Visibility = Visibility.Visible;
        DispatcherQueue.TryEnqueue(() => { menuPanel.Opacity = 1; SendGlass(); });
        Activity();
    }

    // true if a menu was open (a click on the video then only closes it)
    bool CloseMenu()
    {
        if (openMenu == null) return false;
        openMenu = null;
        menuPanel.Visibility = Visibility.Collapsed;
        SendGlass();
        return true;
    }

    void BuildMenu()
    {
        menuItems.Children.Clear();
        if (openMenu == "settings") { BuildMenuSettings(); return; }
        if (openMenu == "speed")
        {
            MenuHeader("Playback speed");
            foreach (var v in new[] { 0.5, 0.75, 1, 1.25, 1.5, 1.75, 2 })
                MenuItem(v == 1 ? "Normal" : $"{v.ToString("0.##", CultureInfo.InvariantCulture)}×", Math.Abs(speed - v) < 0.01, () => Send("set_property", "speed", v));
            return;
        }
        MenuHeader("Audio");
        var audio = trackList.Where(t => t.Type == "audio").ToList();
        if (audio.Count == 0) MenuNote("No audio tracks");
        foreach (var t in audio) MenuItem(t.Label, t.Selected, () => Send("set_property", "aid", t.Id));
        MenuHeader("Subtitles");
        var subs = trackList.Where(t => t.Type == "sub").ToList();
        MenuItem("Off", !subs.Any(t => t.Selected), () => Send("set_property", "sid", "no"));
        foreach (var t in subs) MenuItem(t.Label, t.Selected, () => Send("set_property", "sid", t.Id));
        MenuItem("Add subtitle file…", false, () => _ = AddSubtitleFile());
        if (subs.Any(t => t.Selected))
        {
            MenuHeader($"Subtitle timing  ·  {(subDelay == 0 ? "in sync" : $"{subDelay:+0.0;-0.0} s")}");
            MenuChips("Shift", new[] { (-0.5, "−0.5"), (-0.1, "−0.1"), (0.1, "+0.1"), (0.5, "+0.5"), (0.0, "Reset") }, double.NaN,
                d => Send("set_property", "sub-delay", d == 0 ? 0 : Math.Round(subDelay + d, 2)));
        }
    }

    // Each change applies right away (where mpv allows) and is saved for next time.
    void BuildMenuSettings()
    {
        var S = Settings.Current;
        void Change(Action apply) { apply(); S.Save(); BuildMenu(); }
        var kind = entry?.Kind ?? "anime";

        MenuHeader("Playback");
        MenuItem("Autoplay next video", S.Autoplay, () => { ToggleAutoplay(); BuildMenu(); });
        MenuItem("Auto pop-out", S.AutoPopOut, () => Change(() => S.AutoPopOut = !S.AutoPopOut));
        MenuItem("Skip opening / ending buttons", S.SkipButtons, () => Change(() => S.SkipButtons = !S.SkipButtons));
        MenuItem("Skip openings automatically", S.AutoSkip, () => Change(() => S.AutoSkip = !S.AutoSkip));
        MenuItem("Up next countdown", S.UpNext, () => Change(() => S.UpNext = !S.UpNext));
        MenuItem("Resume where I left off", S.Resume, () => Change(() => { S.Resume = !S.Resume; if (entry?.Unit != "themes") Send("set_property", "save-position-on-quit", S.Resume); }));

        MenuHeader("AI upscaling");
        MenuItem("AI upscaling  (Ctrl+U)", S.Upscale, () => { ToggleAi(); BuildMenu(); });
        MenuChips("Model", new[] { ("animejanai", "AnimeJaNai"), ("adore", "Adore") }, S.UpscaleModel, m => Change(() => { S.UpscaleModel = m; up?.Apply(); }));
        if (S.Upscale)
        {
            MenuChips("Tier", new[] { ("auto", "Auto") }.Concat(Upscaler.Tiers.Select(t => (t.Key, t.Name))).ToArray(), S.UpscaleTier, t => Change(() => { S.UpscaleTier = t; up?.Apply(); }));
            MenuChips("Look", new[] { ("sharp", "Sharp"), ("soft", "Soft") }, S.UpscaleLook, l => Change(() => { S.UpscaleLook = l; up?.Apply(); }));
        }

        // these edit the defaults for this kind of video (anime, or TV shows & movies)
        var pref = S.LangFor(kind);
        MenuHeader(kind == "anime" ? "Audio & subtitles · anime" : "Audio & subtitles · TV & movies");
        MenuChips("Audio", new[] { ("jpn", "Japanese"), ("eng", "English"), ("", "File") }, pref.Audio, code => Change(() =>
        {
            S.SetLang(kind, audio: code);
            var alang = Settings.Langs(code);
            Send("set_property", "alang", alang);
            var match = trackList.FirstOrDefault(t => t.Type == "audio" && alang.Split(',').Contains(t.Lang));
            if (code != "" && match.Id > 0) Send("set_property", "aid", match.Id);
        }));
        MenuChips("Subtitles", new[] { ("eng", "English"), ("jpn", "Japanese"), ("", "File") }, pref.Subs, code => Change(() => { S.SetLang(kind, subs: code); Send("set_property", "slang", Settings.Langs(code)); PickSubs(); }));
        MenuItem("Show subtitles", pref.On, () => Change(() => { S.SetLang(kind, on: !pref.On); Send("set_property", "sid", !pref.On ? "auto" : "no"); if (!pref.On) PickSubs(); }));
        MenuItem("Prefer full subtitles over signs only", S.FullSubs, () => Change(() => { S.FullSubs = !S.FullSubs; PickSubs(); }));
        MenuChips("Text size", new[] { (0.8, "S"), (1.0, "M"), (1.3, "L") }, S.SubScale, scale => Change(() => { S.SubScale = scale; Send("set_property", "sub-scale", scale); }));
        MenuChips("Position (or drag them)", new[] { (100.0, "Bottom"), (85.0, "Higher") }, S.SubPos, pos => Change(() => { S.SubPos = pos; Send("set_property", "sub-pos", pos); }));

        MenuHeader("Controls");
        MenuChips("Hide after", new[] { (2.0, "2s"), (3.0, "3s"), (5.0, "5s") }, S.HideAfter, secs => Change(() => { S.HideAfter = secs; hideTimer.Interval = TimeSpan.FromSeconds(secs); }));
        MenuItem("Timeline previews", S.Previews, () => Change(() => S.Previews = !S.Previews));

        MenuHeader("Advanced");
        MenuItem("Debug info", S.Debug, () => Change(() => { S.Debug = !S.Debug; ApplyDebug(); }));
    }

    void MenuHeader(string text) => menuItems.Children.Add(new TextBlock
    {
        Text = text.ToUpperInvariant(), FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Opacity = 0.55, Foreground = GlassLook.TextB,
        Margin = new Thickness(12, menuItems.Children.Count == 0 ? 6 : 14, 12, 4),
    });

    void MenuNote(string text) => menuItems.Children.Add(new TextBlock { Text = text, FontSize = 13, Opacity = 0.6, Foreground = GlassLook.TextB, Margin = new Thickness(12, 4, 12, 4) });

    // a line with a tick when it's the current choice (WPF "MenuItem")
    void MenuItem(string label, bool selected, Action pick)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) }); row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(new FontIcon { Glyph = selected ? "\uE73E" : "", FontFamily = Icons, FontSize = 13, Foreground = ThemeW.Accent, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center });
        var text = new TextBlock { Text = label, FontSize = 14, Foreground = GlassLook.TextB, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal };
        Grid.SetColumn(text, 1); row.Children.Add(text);
        var face = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8, 12, 8), Background = new SolidColorBrush(Colors.Transparent), Child = row };
        var b = GlassLook.Bare(face, label, () => { pick(); Activity(); });
        b.HorizontalAlignment = HorizontalAlignment.Stretch; b.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        b.PointerEntered += (_, _) => face.Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        b.PointerExited += (_, _) => face.Background = new SolidColorBrush(Colors.Transparent);
        b.PointerPressed += (_, _) => face.Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        menuItems.Children.Add(b);
    }

    // a labelled row of pill choices, the current one lit
    void MenuChips<T>(string label, (T Value, string Name)[] options, T current, Action<T> pick)
    {
        var row = new Grid { Margin = new Thickness(12, 6, 8, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = label, FontSize = 14, Foreground = GlassLook.TextB, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        var chips = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (value, name) in options)
        {
            var c = GlassLook.Chip(name, Equals(value, current), () => { pick(value); Activity(); }, 13, $"{label}: {name}");
            c.Margin = new Thickness(4, 0, 0, 0);
            chips.Children.Add(c);
        }
        Grid.SetColumn(chips, 1); row.Children.Add(chips);
        menuItems.Children.Add(row);
    }

    // ---------- debug info ----------

    const string DebugTemplate =
        "File           ${filename:–}\n" +
        "Position       ${time-pos:–} / ${duration:–}  (${percent-pos:–}%)\n" +
        "Playlist       ${playlist-pos-1:–} of ${playlist-count:–}\n" +
        "Video          ${video-params/w:–}×${video-params/h:–}  ${video-codec:–}\n" +
        "Colour         ${video-params/hw-pixelformat:${video-params/pixelformat:–}}  ${video-params/colormatrix:–} / ${video-params/primaries:–} / ${video-params/gamma:–}\n" +
        "Output         ${target-trc:auto} / ${target-prim:auto}, peak ${target-peak:auto}\n" +
        "Decoder        ${hwdec-current:–}\n" +
        "Frame rate     ${container-fps:–} file · ${estimated-vf-fps:–} actual · ${display-fps:–} display\n" +
        "Dropped        ${frame-drop-count:0} output · ${decoder-frame-drop-count:0} decoder\n" +
        "A/V sync       ${avsync:–}\n" +
        "Audio          ${audio-codec-name:–}  ${audio-params/channel-count:–} ch  ${audio-params/samplerate:–} Hz\n" +
        "Subtitles      ${current-tracks/sub/title:${current-tracks/sub/lang:off}}  delay ${sub-delay:0}\n" +
        "Cache          ${demuxer-cache-duration:–} s ahead";

    void ApplyDebug()
    {
        bool on = Settings.Current.Debug && core != null;
        debugPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (debugTimer == null)
        {
            debugTimer = DispatcherQueue.CreateTimer();
            debugTimer.Interval = TimeSpan.FromSeconds(1);
            debugTimer.Tick += async (_, _) => await RefreshDebug();
        }
        if (on) { debugTimer.Start(); _ = RefreshDebug(); } else debugTimer.Stop();
    }

    async Task RefreshDebug()
    {
        if (ipc == null) return;
        var text = await ipc.Ask(new object?[] { "expand-text", DebugTemplate });
        if (text == null || !Settings.Current.Debug) return;
        var d = display;
        var shown = d == null ? "–" : video?.Hdr == true ? $"{d} · Haku shows HDR10" : d.ToString();
        debugText.Text = $"Show           {entry?.Title}\n{text}\nDisplay        {shown}\nPreviews       {thumbs?.Progress ?? "–"}";
    }
}
