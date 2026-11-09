using System;
using System.Runtime.InteropServices;

namespace AnimePlayer;

// Test runs (HAKU_TEST_WINDOW="x,y,w,h" set): the app's main window is created right where the test puts it, so it never
// comes up over what you're doing (a game, say) or takes focus from it. Normal use never sets HAKU_TEST_WINDOW, so this
// does nothing then. Call Install on the UI thread before the first window. Found 2026-11-08 (tests\focus-watch.ps1):
//   - WPF made its window on the main monitor and then moved it to the test one. With the main monitor at 150% and the
//     test one at 100%, WPF's handling of that DPI change (HwndTarget.OnDpiChanged) calls SetWindowPos without
//     SWP_NOACTIVATE, and the window came to the front (ShowActivated = false doesn't cover it). Made in place
//     (HCBT_CREATEWND), there's no monitor change, so no DPI change and nothing activates.
//   - WinUI shows and activates its window while making it (inside Window's constructor), and that sometimes took the
//     foreground: a program started by the one in front (the test scripts, from your terminal) may come to the front
//     once. A test copy gives that up first (LockSetForegroundWindow). Adding SWP_NOACTIVATE to the window's moves didn't
//     stop it (it isn't a move), nor did WS_EX_NOACTIVATE or making it hidden.
//   - Refusing activations outright (HCBT_ACTIVATE) left the window you were in deactivated (no foreground window at all,
//     which a fullscreen game can take as alt-tab), so they're only noted in the trace. Pressing a WinUI button through
//     UI Automation activates its window too, so the WinUI tests don't press buttons (tests\winui-tests.ps1).
static class TestFocus
{
    public static readonly (int X, int Y, int W, int H)? Rect =
        Environment.GetEnvironmentVariable("HAKU_TEST_WINDOW")?.Split(',') is { Length: 4 } p
        && int.TryParse(p[0], out var x) && int.TryParse(p[1], out var y) && int.TryParse(p[2], out var w) && int.TryParse(p[3], out var h)
            ? (x, y, w, h) : null;

    // The test monitor's scale (1 = 100%), for a window that's made on it in its own DIPs (WPF's Left/Top/Width/Height).
    public static double Scale
    {
        get
        {
            if (Rect is not { } r) return 1;
            var m = MonitorFromPoint(new POINT { X = r.X + 10, Y = r.Y + 10 }, 2);
            return GetDpiForMonitor(m, 0, out uint dpi, out _) == 0 ? dpi / 96.0 : 1;
        }
    }

    static HookProc? proc; // kept alive: Windows holds a pointer to it
    static IntPtr hook;
    static bool placed;

    public static void Install()
    {
        if (Rect is not { } r || hook != IntPtr.Zero) return;
        // A program started by the one in front may bring itself to the front, once; a test copy gives that up before it
        // makes any window (WinUI activates its window while making it). It goes on its own as soon as you press Alt or
        // click, and this process puts it back as it exits.
        bool locked = LockSetForegroundWindow(1 /* LSFW_LOCK */);
        if (locked) AppDomain.CurrentDomain.ProcessExit += (_, _) => LockSetForegroundWindow(2 /* LSFW_UNLOCK */);
        if (MpvCore.Trace != null) MpvCore.Note($"test run: gave up bringing itself to the front: {locked}");
        proc = (code, w, l) =>
        {
            if (code == 5 /* HCBT_ACTIVATE */ && MpvCore.Trace != null)
            {
                var cls = new System.Text.StringBuilder(128); GetClassName(w, cls, 128);
                MpvCore.Note($"test run: window {w} ({cls}, visible {IsWindowVisible(w)}) is being activated, from:\n" + Environment.StackTrace);
            }
            if (code == 3 /* HCBT_CREATEWND */ && !placed)
            {
                // CBT_CREATEWND { CREATESTRUCT* lpcs; HWND insertAfter }; CREATESTRUCT (x64): hwndParent 24, cy 32, cx 36,
                // y 40, x 44, style 48. The first resizable top-level window is the app's main window.
                var cs = Marshal.ReadIntPtr(l);
                int style = Marshal.ReadInt32(cs, 48);
                if (Marshal.ReadIntPtr(cs, 24) == IntPtr.Zero && (style & 0x40000000 /* WS_CHILD */) == 0 && (style & 0x00040000 /* WS_THICKFRAME */) != 0)
                {
                    placed = true;
                    Marshal.WriteInt32(cs, 44, r.X); Marshal.WriteInt32(cs, 40, r.Y);
                    Marshal.WriteInt32(cs, 36, r.W); Marshal.WriteInt32(cs, 32, r.H);
                    if (MpvCore.Trace != null) MpvCore.Note($"test run: main window made at {r.X},{r.Y} {r.W}x{r.H}, style {style:X8}");
                }
            }
            return CallNextHookEx(IntPtr.Zero, code, w, l);
        };
        hook = SetWindowsHookEx(5 /* WH_CBT */, proc, IntPtr.Zero, GetCurrentThreadId());
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool LockSetForegroundWindow(uint code);
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint threadId);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr m, int type, out uint x, out uint y);
}
