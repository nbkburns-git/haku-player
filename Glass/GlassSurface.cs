using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace AnimePlayer;

// "Liquid glass" over video, after Apple's material: mostly clear, with the content behind it blurred,
// saturated and brightened, a lens-like band at the edges, specular rim highlights, and a tint that
// adapts to what's underneath (lifts dark scenes, shades bright ones) instead of a fixed dark wash.
sealed class GlassSurface
{
    const double Inflate = 40; // frost rectangle overhang, so the blur has real pixels at the edges
    readonly double radius;
    readonly ImageBrush frost = new() { ViewboxUnits = BrushMappingMode.RelativeToBoundingBox, Stretch = Stretch.Fill };
    readonly ImageBrush lens = new() { ViewboxUnits = BrushMappingMode.RelativeToBoundingBox, Stretch = Stretch.Fill };
    readonly SolidColorBrush tint = new(Color.FromArgb(46, 12, 14, 20));
    readonly Grid body = new() { ClipToBounds = true };
    readonly Grid shadow = new() { IsHitTestVisible = false };
    Rect region; // part of the frame under this glass, 0..1 units
    readonly Rectangle frostRect, lensRect;
    readonly Border rim; // bright specular edge: fades over dark scenes so it never reads as a white outline

    // Live: mpv's GPU shader blurs the real video under this glass every frame, so the
    // thumbnail-based frost and lens layers are switched off (only tint, sheen and rims stay).
    public bool Live
    {
        set => frostRect.Visibility = lensRect.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
    }

    public GlassSurface(Grid host, double radius)
    {
        this.radius = radius;

        // Drop shadow, cut out under the glass so it can't darken it from behind.
        var s1 = new Border { CornerRadius = new CornerRadius(radius), Background = Brushes.Black, Margin = new Thickness(4, 8, 4, 0),
            Effect = new DropShadowEffect { BlurRadius = 46, ShadowDepth = 12, Opacity = 0.42 } };
        var s2 = new Border { CornerRadius = new CornerRadius(radius), Background = Brushes.Black,
            Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 1.5, Opacity = 0.35 } };
        shadow.Children.Add(s1);
        shadow.Children.Add(s2);
        shadow.CacheMode = new BitmapCache();

        // 1. blurred, colour-boosted video
        frostRect = new Rectangle { Margin = new Thickness(-Inflate), Fill = frost,
            Effect = new BlurEffect { Radius = 28, RenderingBias = RenderingBias.Performance } };
        body.Children.Add(frostRect);
        // 2. adaptive tint
        body.Children.Add(new Rectangle { Fill = tint });
        // 3. lensing: a sharper, slightly magnified copy that shows only in a band along the edges
        var ringMask = new Border { CornerRadius = new CornerRadius(radius), BorderThickness = new Thickness(6), BorderBrush = Brushes.Black,
            Effect = new BlurEffect { Radius = 10 } };
        lensRect = new Rectangle { Fill = lens, Opacity = 0.5, Effect = new BlurEffect { Radius = 10 },
            OpacityMask = new VisualBrush(ringMask) { Stretch = Stretch.Fill } };
        body.Children.Add(lensRect);
        body.SizeChanged += (_, _) =>
        {
            var sz = body.RenderSize;
            ringMask.Width = sz.Width; ringMask.Height = sz.Height;
            ringMask.Measure(sz);
            ringMask.Arrange(new Rect(sz));
        };
        // 4. specular: soft top sheen + a hot spot near the top-left
        body.Children.Add(new Rectangle { Fill = Gradient(0, 0, 0, 1, (0x22, 0), (0x06, 0.45), (0x00, 1)) });
        body.Children.Add(new Rectangle { Fill = new RadialGradientBrush(Color.FromArgb(0x30, 255, 255, 255), Color.FromArgb(0, 255, 255, 255))
            { Center = new Point(0.12, 0), GradientOrigin = new Point(0.1, 0), RadiusX = 0.35, RadiusY = 1.1 } });

        // Rims: bright specular edge (top-left strong, bottom-right faint) over a darker inner edge.
        var darkRim = new Border { CornerRadius = new CornerRadius(radius), BorderThickness = new Thickness(2.5), IsHitTestVisible = false,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x2A, 0, 0, 0)), Effect = new BlurEffect { Radius = 3 } };
        rim = new Border { CornerRadius = new CornerRadius(radius), BorderThickness = new Thickness(1), IsHitTestVisible = false, Opacity = 0.5,
            BorderBrush = Gradient(0, 0, 1, 1, (0x80, 0), (0x22, 0.35), (0x0A, 0.6), (0x3A, 1)) };

        host.Children.Insert(0, rim);
        host.Children.Insert(0, darkRim);
        host.Children.Insert(0, body);
        host.Children.Insert(0, shadow);

        host.SizeChanged += (_, _) =>
        {
            var r = new Rect(host.RenderSize);
            var shape = new RectangleGeometry(r, radius, radius);
            body.Clip = shape;
            var outside = new RectangleGeometry(new Rect(-200, -200, r.Width + 400, r.Height + 400));
            shadow.Clip = new CombinedGeometry(GeometryCombineMode.Exclude, outside, shape);
        };
    }

    static LinearGradientBrush Gradient(double x0, double y0, double x1, double y1, params (byte A, double At)[] stops)
    {
        var g = new LinearGradientBrush { StartPoint = new Point(x0, y0), EndPoint = new Point(x1, y1) };
        foreach (var (a, at) in stops) g.GradientStops.Add(new GradientStop(Color.FromArgb(a, 255, 255, 255), at));
        g.Freeze();
        return g;
    }

    // Where this glass sits over the frame (0..1 units) and its size in pixels.
    public void SetRegion(Rect r, Size size)
    {
        region = r;
        if (size.Width <= 0) return;
        double mx = Inflate * r.Width / size.Width, my = Inflate * r.Height / size.Height;
        frost.Viewbox = new Rect(r.X - mx, r.Y - my, r.Width + 2 * mx, r.Height + 2 * my);
        double k = 0.015; // lens magnification: show a slightly smaller patch stretched over the glass
        lens.Viewbox = new Rect(r.X + r.Width * k / 2, r.Y + r.Height * k / 2, r.Width * (1 - k), r.Height * (1 - k));
    }

    public void SetFrame(GlassFrame f)
    {
        frost.ImageSource = f.Boosted;
        lens.ImageSource = f.Boosted;
        Adapt(f.Luminance(region));
    }

    // Dark scene -> a faint white lift so the glass reads as glass, not a hole.
    // Bright scene -> a bit more shade so white icons and text stay legible.
    void Adapt(double lum)
    {
        double k = Settings.Current.Glass switch { 0 => 0.55, 2 => 1.2, _ => 0.8 }; // Clear / Balanced / Frosted
        Color c = lum < 0.35
            ? Color.FromArgb((byte)((10 + (0.35 - lum) / 0.35 * 16) * k), 255, 255, 255)
            : Color.FromArgb((byte)((18 + (lum - 0.35) / 0.65 * 80) * k), 10, 12, 18);
        tint.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(c, TimeSpan.FromMilliseconds(450)));
        // light only catches the edge when there's light behind the glass
        double rimTo = 0.12 + 0.88 * Math.Clamp((lum - 0.08) / 0.5, 0, 1);
        rim.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(rimTo, TimeSpan.FromMilliseconds(450)));
    }
}

// A video thumbnail prepared for glass: saturation and brightness boosted (Apple's glass makes content
// behind it more vivid, not murkier), plus luminance lookup for adaptive tint.
sealed class GlassFrame
{
    public BitmapSource Boosted { get; }
    readonly byte[] lum; // brightness at every 2nd pixel (all Luminance reads): ~21 KB, not a 332 KB copy of the frame
    readonly int w, h, lw;

    // Built about once a second while the bars show. The full-size working buffers (332 KB each, which .NET puts on the
    // large-object heap, only freed by full collections) are rented from the shared pool and handed straight back.
    public GlassFrame(BitmapSource src)
    {
        var b = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        w = b.PixelWidth; h = b.PixelHeight;
        int n = w * h * 4;
        var pool = System.Buffers.ArrayPool<byte>.Shared;
        byte[] px = pool.Rent(n), o = pool.Rent(n);
        try
        {
            b.CopyPixels(px, w * 4, 0);
            for (int i = 0; i < n; i += 4)
            {
                double bl = px[i], g = px[i + 1], r = px[i + 2];
                double gray = 0.299 * r + 0.587 * g + 0.114 * bl;
                o[i] = Clamp((gray + (bl - gray) * 1.6) * 1.08 + 6);
                o[i + 1] = Clamp((gray + (g - gray) * 1.6) * 1.08 + 6);
                o[i + 2] = Clamp((gray + (r - gray) * 1.6) * 1.08 + 6);
                o[i + 3] = 255;
            }
            lw = (w + 1) / 2;
            lum = new byte[lw * ((h + 1) / 2)];
            for (int y = 0; y < h; y += 2)
                for (int x = 0; x < w; x += 2)
                {
                    int i = (y * w + x) * 4;
                    lum[(y / 2) * lw + x / 2] = (byte)(0.0722 * px[i] + 0.7152 * px[i + 1] + 0.2126 * px[i + 2]);
                }
            Boosted = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, o, w * 4); // copies the pixels
            Boosted.Freeze();
        }
        finally { pool.Return(px); pool.Return(o); }
    }

    static byte Clamp(double v) => (byte)Math.Clamp(v, 0, 255);

    // Average brightness (0..1) of a region given in 0..1 frame units; areas off the frame count as black.
    public double Luminance(Rect r)
    {
        int x0 = (int)(r.X * w), y0 = (int)(r.Y * h), x1 = (int)(r.Right * w), y1 = (int)(r.Bottom * h);
        double sum = 0; int n = 0;
        for (int y = y0; y < y1; y += 2)
            for (int x = x0; x < x1; x += 2)
            {
                n++;
                if (x < 0 || y < 0 || x >= w || y >= h) continue;
                sum += lum[(y / 2) * lw + x / 2] / 255.0;
            }
        return n == 0 ? 0 : sum / n;
    }
}
