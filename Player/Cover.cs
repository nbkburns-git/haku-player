using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace AnimePlayer;

// A plain black window that fades over the player while its size changes, so the video never
// visibly stutters mid-resize. WinForms' Form.Opacity uses whole-window alpha (cheap even at 4K).
sealed class Cover : System.Windows.Forms.Form
{
    [DllImport("user32.dll")] static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int cy, uint f);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int a, ref int v, int s);

    public Cover(IntPtr owner)
    {
        FormBorderStyle = System.Windows.Forms.FormBorderStyle.None;
        StartPosition = System.Windows.Forms.FormStartPosition.Manual;
        ShowInTaskbar = false;
        BackColor = System.Drawing.Color.Black;
        Opacity = 0;
        CreateHandle();
        SetWindowLongPtr(Handle, -8 /* GWLP_HWNDPARENT */, owner); // stays above the player
    }

    protected override bool ShowWithoutActivation => true;

    protected override System.Windows.Forms.CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x80; return p; } // NOACTIVATE | TOOLWINDOW
    }

    public void Place(int l, int t, int r, int b, bool round)
    {
        int corner = round ? 2 : 1;
        DwmSetWindowAttribute(Handle, 33, ref corner, 4);
        SetWindowPos(Handle, IntPtr.Zero /* HWND_TOP */, l, t, r - l, b - t, 0x0010 | 0x0040); // NOACTIVATE | SHOWWINDOW
    }

    public async Task Fade(double from, double to, double ms)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            double t = Math.Min(1, sw.Elapsed.TotalMilliseconds / ms);
            double k = t * t * (3 - 2 * t); // smoothstep
            Opacity = Math.Min(0.999, from + (to - from) * k); // stay layered: Opacity 1 would drop the alpha style and flash
            if (t >= 1) break;
            await Task.Delay(8);
        }
    }
}
