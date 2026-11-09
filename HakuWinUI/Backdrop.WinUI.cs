using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics.Imaging;
using Windows.UI;

namespace AnimePlayer;

// Behind the pages, as the WPF app (MainWindow.xaml "Shell"): a dark tint over Windows' acrylic, three slow drifting
// blobs of the theme's colours, on a show page its cover as a soft glow, and a shade towards the bottom.
public partial class MainWindow
{
    readonly Canvas blobs = new() { IsHitTestVisible = false };
    readonly Image ambient = new() { Stretch = Stretch.UniformToFill, Opacity = 0, IsHitTestVisible = false };
    readonly System.Collections.Generic.List<Storyboard> blobBoards = new();
    readonly System.Collections.Generic.List<(Ellipse E, double X, double Y)> blobAt = new();
    bool windowActive = true, blobsScrolling;

    static byte GlassAlpha => Settings.Current.Glass switch { 0 => 0x80, 2 => 0xE2, _ => 0xB8 };

    void SetupBackdrop()
    {
        tint.Background = new SolidColorBrush(Color.FromArgb(GlassAlpha, 0x0A, 0x0B, 0x10));
        root.Children.Add(blobs);
        ambient.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(500) };
        root.Children.Add(ambient);
        root.Children.Add(new Border
        {
            IsHitTestVisible = false,
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1),
                GradientStops = { new GradientStop { Color = Color.FromArgb(0, 0, 0, 0), Offset = 0.4 }, new GradientStop { Color = Color.FromArgb(0x70, 0, 0, 0), Offset = 1 } },
            },
        });
        blobs.SizeChanged += (_, _) => PlaceBlobs();
        Activated += (_, e) => { windowActive = e.WindowActivationState != WindowActivationState.Deactivated; UpdateBlobs(); };
        MakeBlobs();
    }

    // Slow drifting colour blobs behind the glass: the "liquid" part.
    void MakeBlobs()
    {
        foreach (var sb in blobBoards) sb.Stop();
        blobBoards.Clear(); blobAt.Clear(); blobs.Children.Clear();
        var (ta, tb, tc) = ThemeW.Colors(Settings.Current.Theme);
        foreach (var (c, x, y, dx, dy, secs) in new[] { (tb, 0.08, 0.05, 340.0, 180.0, 23.0), (tc, 0.62, -0.15, -280.0, 240.0, 29.0), (ta, 0.30, 0.55, 260.0, -200.0, 34.0) })
        {
            var move = new TranslateTransform();
            var e = new Ellipse
            {
                Width = 1100, Height = 1100, Opacity = 0.42, RenderTransform = move,
                Fill = new RadialGradientBrush { GradientStops = { new GradientStop { Color = Color.FromArgb(150, c.R, c.G, c.B), Offset = 0 }, new GradientStop { Color = Color.FromArgb(0, c.R, c.G, c.B), Offset = 1 } } },
            };
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
            var sb = new Storyboard();
            foreach (var (prop, to, s) in new[] { ("X", dx, secs), ("Y", dy, secs * 0.8) })
            {
                var a = new DoubleAnimation { From = 0, To = to, Duration = TimeSpan.FromSeconds(s), AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease };
                Storyboard.SetTarget(a, move); Storyboard.SetTargetProperty(a, prop); sb.Children.Add(a);
            }
            blobs.Children.Add(e);
            blobAt.Add((e, x, y));
            blobBoards.Add(sb);
        }
        PlaceBlobs();
        blobs.Opacity = Settings.Current.MovingBackground ? 1 : 0;
        foreach (var sb in blobBoards) sb.Begin();
        UpdateBlobs();
    }

    void PlaceBlobs()
    {
        foreach (var (e, x, y) in blobAt) { Canvas.SetLeft(e, x * blobs.ActualWidth - 200); Canvas.SetTop(e, y * blobs.ActualHeight - 200); }
    }

    // Blobs only move when someone can see them: not while playing, minimized, or while another app is in front.
    void UpdateBlobs()
    {
        bool run = Settings.Current.MovingBackground && core == null && !blobsScrolling && (windowActive || Settings.Current.MovingAlways)
                   && AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized };
        foreach (var sb in blobBoards) { if (run) sb.Resume(); else sb.Pause(); }
    }

    // A show page: its cover, shrunk to a few pixels, blurred and stretched back up, makes a soft ambient glow.
    int ambientSeq;
    async void ShowAmbient(Entry? e)
    {
        int my = ++ambientSeq;
        if (e?.Cover is not CoverImage c) { ambient.Opacity = 0; return; }
        var glow = await SoftGlow(c.Path);
        if (my != ambientSeq) return;
        if (glow == null) { ambient.Opacity = 0; return; }
        ambient.Source = glow;
        ambient.Opacity = 0.5;
    }

    static async Task<WriteableBitmap?> SoftGlow(string file)
    {
        try
        {
            const int w = 24;
            using var fs = File.OpenRead(file);
            using var ras = fs.AsRandomAccessStream();
            var dec = await BitmapDecoder.CreateAsync(ras);
            uint h = (uint)Math.Max(1, Math.Round((double)dec.PixelHeight * w / Math.Max(1u, dec.PixelWidth)));
            var data = await dec.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                new BitmapTransform { ScaledWidth = w, ScaledHeight = h, InterpolationMode = BitmapInterpolationMode.Fant },
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
            var px = data.DetachPixelData();
            Blur(px, w, (int)h);
            var bmp = new WriteableBitmap(w, (int)h);
            using (var s = bmp.PixelBuffer.AsStream()) s.Write(px, 0, px.Length);
            bmp.Invalidate();
            return bmp;
        }
        catch { return null; }
    }

    // 3 box blurs (5 wide) each way ~ a gaussian, as the WPF app's SoftGlow
    static void Blur(byte[] px, int w, int h)
    {
        int stride = w * 4;
        var tmp = new byte[px.Length];
        for (int pass = 0; pass < 3; pass++)
        {
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    for (int c = 0; c < 4; c++)
                    {
                        int sum = 0;
                        for (int d = -2; d <= 2; d++) sum += px[y * stride + Math.Clamp(x + d, 0, w - 1) * 4 + c];
                        tmp[y * stride + x * 4 + c] = (byte)(sum / 5);
                    }
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    for (int c = 0; c < 4; c++)
                    {
                        int sum = 0;
                        for (int d = -2; d <= 2; d++) sum += tmp[Math.Clamp(y + d, 0, h - 1) * stride + x * 4 + c];
                        px[y * stride + x * 4 + c] = (byte)(sum / 5);
                    }
        }
    }
}

// Windows' acrylic behind the window, the lighter "thin" kind: the same as the WPF app's (DWM transient-window acrylic)
sealed class ThinAcrylicBackdrop : Microsoft.UI.Xaml.Media.SystemBackdrop
{
    Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController? controller;

    protected override void OnTargetConnected(Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        base.OnTargetConnected(target, root);
        controller = new Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController { Kind = Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicKind.Thin };
        controller.SetSystemBackdropConfiguration(GetDefaultSystemBackdropConfiguration(target, root));
        controller.AddSystemBackdropTarget(target);
    }

    protected override void OnTargetDisconnected(Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop target)
    {
        base.OnTargetDisconnected(target);
        controller?.RemoveSystemBackdropTarget(target);
        controller?.Dispose();
        controller = null;
    }
}
