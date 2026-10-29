using System;
using System.Linq;
using System.Windows;

namespace AnimePlayer;

// Pop-out: a small always-on-top player in the bottom-right corner, shaped to the video,
// so you can watch while working or gaming. The same button puts it back.
public partial class MainWindow
{
    bool popped;
    WINDOWPLACEMENT placementBeforePop;

    void TogglePopOut()
    {
        if (fullscreen) return;
        if (!popped)
        {
            popped = true;
            placementBeforePop = Placement();
            var wa = System.Windows.Forms.Screen.FromHandle(Hwnd).WorkingArea;
            double aspect = videoAspect > 0 ? videoAspect : 16.0 / 9;
            int w = (int)(wa.Width * 0.3), h = (int)Math.Round(w / aspect), m = (int)(28 * VisualTreeHelper_Dpi());
            var to = new RECT { L = wa.Right - w - m, T = wa.Bottom - h - m, R = wa.Right - m, B = wa.Bottom - m };
            // reopen where it was last time (same width, reshaped to this video), if that spot is still on a screen
            if (Settings.Current.PopRect is { Length: 4 } pr)
            {
                int pw = Math.Max(320, pr[2] - pr[0]), ph = (int)Math.Round(pw / aspect);
                var saved = new System.Drawing.Rectangle(pr[0], pr[1], pw, ph);
                // must mostly sit on a screen and still be pop-out sized (a bad save once made it open full size)
                var scr = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(sc => sc.WorkingArea.IntersectsWith(saved) &&
                    System.Drawing.Rectangle.Intersect(sc.WorkingArea, saved).Width * System.Drawing.Rectangle.Intersect(sc.WorkingArea, saved).Height > pw * ph / 2
                    && pw <= sc.WorkingArea.Width * 0.75 && ph <= sc.WorkingArea.Height * 0.75);
                if (scr != null)
                {
                    var a = scr.WorkingArea;
                    int l = Math.Clamp(saved.Left, a.Left, Math.Max(a.Left, a.Right - pw)), t = Math.Clamp(saved.Top, a.Top, Math.Max(a.Top, a.Bottom - ph));
                    to = new RECT { L = l, T = t, R = l + pw, B = t + ph };
                }
            }
            Topmost = true;
            MinWidth = 320; MinHeight = 180; // the normal minimum (900x600) would add bars around a small video
            Transition(to, null, () =>
            {
                if (WindowState == WindowState.Maximized)
                {
                    GetWindowRect(Hwnd, out var cur);
                    var wp = placementBeforePop; wp.showCmd = 1; wp.normal = cur;
                    SetWindowPlacement(Hwnd, ref wp);
                }
                Shell.Margin = new Thickness(0);
            });
        }
        else
        {
            SavePopRect();
            popped = false;
            Topmost = false;
            MinWidth = 900; MinHeight = 600;
            var before = placementBeforePop;
            bool wasMax = before.showCmd == 3;
            Transition(wasMax ? MaxRect() : before.normal, () =>
            {
                if (wasMax) MaximizeKeeping(before.normal); else SetWindowPlacement(Hwnd, ref before);
                FitMaximized();
            });
        }
        controls?.SetPopped(popped);
    }

    // leaving the player while popped: go straight back to the normal window
    void UnpopNow()
    {
        if (!popped) return;
        SavePopRect();
        popped = false;
        Topmost = false;
        MinWidth = 900; MinHeight = 600;
        var before = placementBeforePop;
        SetWindowPlacement(Hwnd, ref before);
        FitMaximized();
    }

    void SavePopRect()
    {
        // only a normal pop-out window: not while maximized/fullscreen from the popped state
        if (!popped || fullscreen || WindowState != WindowState.Normal || !GetWindowRect(Hwnd, out var r)) return;
        var wa = System.Windows.Forms.Screen.FromHandle(Hwnd).WorkingArea;
        if (r.R - r.L > wa.Width * 0.75 || r.B - r.T > wa.Height * 0.75) return;
        Settings.Current.PopRect = new[] { r.L, r.T, r.R, r.B };
        Settings.Current.Save();
    }

    // while popped out, the edges/corners resize the window (the overlay passes those grabs through)
    public bool CanResizeEdges => !fullscreen && WindowState == WindowState.Normal;

    double VisualTreeHelper_Dpi() => System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
}
