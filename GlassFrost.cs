using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace AnimePlayer;

// Frost for the shared GlassPanel pop-ups (tooltips, What's missing): the screen behind the pop-up, blurred,
// so it reads like the Settings card instead of a flat tint you can see text through.
// Grab() copies that bit of screen just before the pop-up opens (a few ms, GPU-composed pixels: video and acrylic included);
// rendering the page ourselves instead took ~300 ms each time and made every tooltip stutter.
static class GlassFrost
{
    const int Inflate = 40; // device px of blur overhang, so the edges blur real pixels rather than fading out
    static BitmapSource? grab;
    static Int32Rect grabAt; // screen px

    // Call right before a pop-up next to `near` opens.
    public static void Grab(FrameworkElement near)
    {
        grab = null;
        if (Application.Current.MainWindow is not { IsLoaded: true } mw || PresentationSource.FromVisual(near) == null || mw.WindowState == WindowState.Minimized) return;
        var a = near.PointToScreen(new Point(0, 0));
        var b = near.PointToScreen(new Point(near.ActualWidth, near.ActualHeight));
        var screen = System.Windows.Forms.SystemInformation.VirtualScreen;
        // tooltips open under the mouse, pop-ups under their button: a generous box around it, kept on screen
        int x = (int)Math.Max(screen.Left, a.X - 500), y = (int)Math.Max(screen.Top, a.Y - 150);
        int r = (int)Math.Min(screen.Right, b.X + 800), bt = (int)Math.Min(screen.Bottom, b.Y + 450);
        if (r - x < 8 || bt - y < 8) return;
        try
        {
            using var bmp = new System.Drawing.Bitmap(r - x, bt - y, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(x, y, 0, 0, bmp.Size);
            var d = bmp.LockBits(new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height), System.Drawing.Imaging.ImageLockMode.ReadOnly, bmp.PixelFormat);
            try { grab = BitmapSource.Create(bmp.Width, bmp.Height, 96, 96, PixelFormats.Bgr32, null, d.Scan0, d.Stride * d.Height, d.Stride); }
            finally { bmp.UnlockBits(d); }
            grab.Freeze();
            grabAt = new Int32Rect(x, y, bmp.Width, bmp.Height);
        }
        catch { grab = null; } // no frost, just the tint
    }

    public static void Attach(ContentControl panel)
    {
        if (panel.Template?.FindName("Frost", panel) is not Grid host) return;
        host.Children.Clear();
        // a pop-up's window is placed after its content loads, so measure once it's settled
        panel.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (grab == null || !panel.IsLoaded || PresentationSource.FromVisual(panel) is not { CompositionTarget: { } ct }) return;
            var p = panel.PointToScreen(new Point(0, 0));
            double sx = ct.TransformToDevice.M11, sy = ct.TransformToDevice.M22;
            int x = (int)p.X - Inflate - grabAt.X, y = (int)p.Y - Inflate - grabAt.Y;
            int w = (int)(panel.ActualWidth * sx) + 2 * Inflate, h = (int)(panel.ActualHeight * sy) + 2 * Inflate;
            if (x < 0 || y < 0 || x + w > grabAt.Width || y + h > grabAt.Height) return; // not over the part we grabbed
            host.Clip = new RectangleGeometry(new Rect(0, 0, panel.ActualWidth, panel.ActualHeight), 14, 14);
            host.Children.Add(new Rectangle
            {
                Fill = new ImageBrush(new CroppedBitmap(grab, new Int32Rect(x, y, w, h))),
                Margin = new Thickness(-Inflate / sx, -Inflate / sy, -Inflate / sx, -Inflate / sy), IsHitTestVisible = false,
                Effect = new BlurEffect { Radius = 28, RenderingBias = RenderingBias.Performance },
            });
        });
    }
}
