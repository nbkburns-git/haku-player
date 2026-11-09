using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace AnimePlayer;

// The WinUI window's behaviour around the video, as the WPF app's:
//   - shaped to the video while playing in a normal window (and kept that shape while you drag an edge), back to its
//     browsing size afterwards;
//   - pop-out: a small always-on-top player in the bottom-right corner (where it was last time), the same button back;
//   - one Haku at a time: a video opened while it runs plays in this window (App.OnLaunched hands it over);
//   - Discord status while playing.
public partial class MainWindow
{
    // ---------- the video's shape ----------

    RectInt32? sizeBeforePlayer;     // the window's size and place to go back to after playback
    bool ShapeLocked => core != null && !fullscreen && AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored } && videoAspect > 0;

    void FitWindowToVideo()
    {
        if (!ShapeLocked || TestFocus.Rect != null) return; // test runs keep the size the test asked for
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var pos = AppWindow.Position; var size = AppWindow.Size;
        sizeBeforePlayer ??= new RectInt32(pos.X, pos.Y, size.Width, size.Height);
        double w = size.Width, h = w / videoAspect;
        if (h > area.Height) { h = area.Height; w = h * videoAspect; }
        int cx = pos.X + size.Width / 2, cy = pos.Y + size.Height / 2;
        int x = Math.Clamp((int)(cx - w / 2), area.X, Math.Max(area.X, area.X + area.Width - (int)w));
        int y = Math.Clamp((int)(cy - h / 2), area.Y, Math.Max(area.Y, area.Y + area.Height - (int)h));
        AppWindow.MoveAndResize(new RectInt32(x, y, (int)w, (int)Math.Round(h)));
    }

    void RestoreBrowsingSize()
    {
        // the browsing size again, kept where the window is now (moved while watching: it stays there), on screen
        if (sizeBeforePlayer is { } r && AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored } && !popped)
        {
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            var pos = AppWindow.Position; var size = AppWindow.Size;
            int cx = pos.X + size.Width / 2, cy = pos.Y + size.Height / 2;
            int x = Math.Clamp(cx - r.Width / 2, area.X, Math.Max(area.X, area.X + area.Width - r.Width));
            int y = Math.Clamp(cy - r.Height / 2, area.Y, Math.Max(area.Y, area.Y + area.Height - r.Height));
            AppWindow.MoveAndResize(new RectInt32(x, y, r.Width, r.Height));
        }
        sizeBeforePlayer = null;
    }

    // dragging an edge while playing keeps the video's shape (WM_SIZING)
    WndProc? shapeProc; IntPtr oldProc;
    void HookSizing()
    {
        shapeProc = ShapeWndProc;
        oldProc = SetWindowLongPtr(hwnd, -4 /* GWLP_WNDPROC */, Marshal.GetFunctionPointerForDelegate(shapeProc));
    }

    IntPtr ShapeWndProc(IntPtr h, uint msg, IntPtr w, IntPtr l)
    {
        if (msg == 0x0214 /* WM_SIZING */ && ShapeLocked)
        {
            var r = Marshal.PtrToStructure<RECT>(l);
            int edge = w.ToInt32(); // 1 left, 2 right, 3 top, 4 top-left, 5 top-right, 6 bottom, 7 bottom-left, 8 bottom-right
            int width = r.R - r.L, height = r.B - r.T;
            if (edge is 3 or 6) r.R = r.L + (int)Math.Round(height * videoAspect);
            else { int nh = (int)Math.Round(width / videoAspect); if (edge is 4 or 5) r.T = r.B - nh; else r.B = r.T + nh; }
            Marshal.StructureToPtr(r, l, false);
            return (IntPtr)1;
        }
        return CallWindowProc(oldProc, h, msg, w, l);
    }

    // ---------- pop-out ----------

    bool popped;
    RectInt32 placementBeforePop;
    bool maximizedBeforePop;

    void TogglePopOut()
    {
        if (fullscreen || AppWindow.Presenter is not OverlappedPresenter op) return;
        if (!popped)
        {
            popped = true;
            maximizedBeforePop = op.State == OverlappedPresenterState.Maximized;
            if (maximizedBeforePop) op.Restore();
            placementBeforePop = new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
            var wa = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            double aspect = videoAspect > 0 ? videoAspect : 16.0 / 9;
            int w = (int)(wa.Width * 0.3), h = (int)Math.Round(w / aspect), m = (int)(28 * (root.XamlRoot?.RasterizationScale ?? 1));
            var to = new RectInt32(wa.X + wa.Width - w - m, wa.Y + wa.Height - h - m, w, h);
            // reopen where it was last time (same width, reshaped to this video), if that spot is still on a screen
            if (Settings.Current.PopRect is { Length: 4 } pr)
            {
                int pw = Math.Max(320, pr[2] - pr[0]), ph = (int)Math.Round(pw / aspect);
                var area = DisplayArea.GetFromRect(new RectInt32(pr[0], pr[1], pw, ph), DisplayAreaFallback.None);
                if (area != null && pw <= area.WorkArea.Width * 0.75 && ph <= area.WorkArea.Height * 0.75)
                {
                    var a = area.WorkArea;
                    to = new RectInt32(Math.Clamp(pr[0], a.X, Math.Max(a.X, a.X + a.Width - pw)), Math.Clamp(pr[1], a.Y, Math.Max(a.Y, a.Y + a.Height - ph)), pw, ph);
                }
            }
            op.IsAlwaysOnTop = true;
            op.SetBorderAndTitleBar(true, false);
            AppWindow.MoveAndResize(to);
        }
        else
        {
            SavePopRect();
            popped = false;
            op.IsAlwaysOnTop = false;
            op.SetBorderAndTitleBar(true, false);
            AppWindow.MoveAndResize(placementBeforePop);
            if (maximizedBeforePop) op.Maximize();
        }
        topBar.SetPopped(popped);
    }

    void UnpopNow() { if (popped) TogglePopOut(); }

    void SavePopRect()
    {
        if (!popped || fullscreen) return;
        var wa = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var p = AppWindow.Position; var s = AppWindow.Size;
        if (s.Width > wa.Width * 0.75 || s.Height > wa.Height * 0.75) return;
        Settings.Current.PopRect = new[] { p.X, p.Y, p.X + s.Width, p.Y + s.Height };
        Settings.Current.Save();
    }

    // ---------- one Haku at a time ----------

    public static readonly string InstanceKey = "HakuPlayerWinUI-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Settings.DataDir.ToLowerInvariant())))[..16];

    // Videos handed over by a second launch (App.OnLaunched): play them here.
    async void ListenForOpens()
    {
        while (true)
        {
            try
            {
                using var s = new NamedPipeServerStream(InstanceKey, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await s.WaitForConnectionAsync();
                var path = await new StreamReader(s).ReadLineAsync();
                if (path == null || !File.Exists(path) || !IsVideo(path)) continue;
                if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } o) o.Restore();
                if (TestFocus.Rect == null) Activate();
                Play(path);
            }
            catch { await Task.Delay(1000); }
        }
    }

    // ---------- Discord ----------

    double discordPos; DateTime discordAt;

    // What Discord shows: the show and episode (or the show and which opening), its cover, and time left while playing.
    void DiscordUpdate()
    {
        if (!Settings.Current.DiscordStatus || entry is not { } e || playingIndex < 0 || playingIndex >= e.Videos.Count) return;
        discordPos = position; discordAt = DateTime.UtcNow;
        var v = e.Videos[playingIndex];
        string details = e.Title, state = e.Kind == "movie" ? "Movie" : v.Label;
        if (e.Unit == "themes") (details, state) = ThemeText(v.Title, e.Name);
        Discord.Watching(details, state, paused, position, duration, InfoService.Cached(e.InfoKey)?.CoverUrl);
    }

    // "Code Geass R2 – OP1" -> ("Code Geass R2", "Opening 1")
    static (string, string) ThemeText(string title, string show)
    {
        var m = System.Text.RegularExpressions.Regex.Match(title, @"^(.*?)\s*[–-]\s*(OP|ED)(\d*)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return (title, "Opening / ending");
        string kind = m.Groups[2].Value.Equals("OP", StringComparison.OrdinalIgnoreCase) ? "Opening" : "Ending";
        string name = m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : show;
        return (name, m.Groups[3].Length > 0 ? $"{kind} {int.Parse(m.Groups[3].ValueSpan)}" : kind);
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")] static extern IntPtr CallWindowProc(IntPtr prev, IntPtr h, uint msg, IntPtr w, IntPtr l);
}
