using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AnimePlayer;

// Accent colour themes: swaps the app's Accent / AccentGrad brushes and the logo; everything using them recolours live.
static class Theme
{
    public static readonly (string Key, string Name)[] All =
    {
        ("sakura", "Sakura"), ("violet", "Violet"), ("ocean", "Ocean"), ("emerald", "Emerald"), ("sunset", "Sunset"), ("custom", "Custom"),
    };

    // light end, main accent, deep end
    public static (Color A, Color B, Color C) Colors(string key) => key switch
    {
        "violet" => (C("#C3A6FF"), C("#8E5CFF"), C("#4F5BFF")),
        "ocean" => (C("#7FE9FF"), C("#2BA8FF"), C("#3A5BFF")),
        "emerald" => (C("#8DF5C6"), C("#22C48A"), C("#1A8FB0")),
        "sunset" => (C("#FFC36B"), C("#FF8A3D"), C("#E8475F")),
        "custom" => FromOne(Parse(Settings.Current.CustomColor) ?? C("#2BA8FF")),
        _ => (C("#FF7A8C"), C("#E8475F"), C("#C23BD0")),
    };

    static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static Color? Parse(string? hex)
    {
        try { return ColorConverter.ConvertFromString(hex) is Color c ? Color.FromRgb(c.R, c.G, c.B) : null; }
        catch (FormatException) { return null; }
    }

    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    // A custom colour's light and deep ends, shaped like the built-in themes: lighter, and darker with a slight hue turn.
    static (Color, Color, Color) FromOne(Color b)
    {
        var (h, s, l) = Hsl(b);
        return (FromHsl(h, s, Math.Min(0.86, l + 0.2)), b, FromHsl((h + 18) % 360, s, Math.Max(0.25, l - 0.12)));
    }

    public static void Install() => Apply(Settings.Current.Theme);

    // App resources get frozen, so a theme change swaps in new brushes; DynamicResource users update live.
    public static void Apply(string key)
    {
        var (a, b, c) = Colors(key);
        var r = Application.Current.Resources;
        r["Accent"] = new SolidColorBrush(b);
        r["AccentColor"] = b; // for glows (effects take a Color, not a brush)
        r["AccentLight"] = new SolidColorBrush(a);
        r["AccentLightColor"] = a;
        r["AccentGrad"] = new LinearGradientBrush(new GradientStopCollection { new(a, 0), new(b, 0.6), new(c, 1) }, new Point(0, 0), new Point(1, 0));
        r["Logo"] = Logo(b, small: false);
        r["AppIcon"] = Bitmap(Logo(b, small: true), 48);
    }

    // The iceberg logo (Assets\make-icon.ps1 draws the same shapes in its own blue for haku.ico),
    // turned to the accent's hue. small: the two-tone version, for the taskbar and window icon.
    public static DrawingImage Logo(Color accent, bool small)
    {
        var (hue, sat, _) = Hsl(accent);
        Brush Tint(string hex)
        {
            var (_, s, l) = Hsl(C(hex));
            var br = new SolidColorBrush(FromHsl(hue, Math.Min(1, s * sat), l)); br.Freeze(); return br;
        }
        Brush Down(string top, string bottom) =>
            new LinearGradientBrush(((SolidColorBrush)Tint(top)).Color, ((SolidColorBrush)Tint(bottom)).Color, 90.0);
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 256, 256)))); // keep the 8 px margin
        var tile = new DrawingGroup { ClipGeometry = new RectangleGeometry(new Rect(8, 8, 240, 240), 62, 62) };
        void Rect(Brush br, double y) => tile.Children.Add(new GeometryDrawing(br, null, new RectangleGeometry(new Rect(8, y, 240, 248 - y))));
        DrawingGroup Shapes(double k, double x, double y) { var d = new DrawingGroup { Transform = new MatrixTransform(k, 0, 0, k, x, y) }; tile.Children.Add(d); return d; }
        void Poly(DrawingGroup d, string pts, string hex) => d.Children.Add(new GeometryDrawing(Tint(hex), null, Geometry.Parse("M " + pts.Replace(" ", " L ") + " Z")));
        if (small)
        {
            Rect(Tint("#4FB0DA"), 8);
            Rect(Tint("#0A3F5E"), 98);
            var d = Shapes(204 / 256.0, 26, 24);
            Poly(d, "56,93 56,18 88,46 100,40 171,93", "#DDF7FD");
            Poly(d, "56,93 171,93 222,134 132,190 136,210 56,240", "#2A9ACB");
        }
        else
        {
            Rect(Down("#2C8FC0", "#7DCFEA"), 8);
            Rect(Down("#0E5F8E", "#062638"), 94);
            var d = Shapes(210 / 256.0, 23, 18);
            Poly(d, "66,38 80,60 66,92", "#F2FDFF");
            Poly(d, "80,60 92,54 106,72 66,92", "#CDF3FA");
            Poly(d, "106,72 155,92 66,92", "#A6E6F2");
            Poly(d, "106,72 116,68 155,92", "#7FD3EA");
            Poly(d, "66,92 155,92 120,124", "#3FB0D8");
            Poly(d, "155,92 212,128 120,124", "#2A9ACB");
            Poly(d, "66,92 120,124 66,160", "#2391C2");
            Poly(d, "120,124 212,128 146,169 150,188 132,179 100,170", "#1A78AA");
            Poly(d, "66,160 120,124 100,170", "#136896");
            Poly(d, "66,160 100,170 132,179 103,197 108,216 92,204 72,232 66,220", "#0C5480");
        }
        g.Children.Add(tile);
        if (!small)
        {
            var rim = ((SolidColorBrush)Tint("#7FDCEB")).Color; rim.A = 90;
            g.Children.Add(new GeometryDrawing(null, new Pen(new SolidColorBrush(rim), 3), new RectangleGeometry(new Rect(9.5, 9.5, 237, 237), 60.5, 60.5)));
        }
        var img = new DrawingImage(g); img.Freeze(); return img;
    }

    static BitmapSource Bitmap(ImageSource img, int px)
    {
        var v = new DrawingVisual();
        using (var dc = v.RenderOpen()) dc.DrawImage(img, new Rect(0, 0, px, px));
        var bmp = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(v); bmp.Freeze(); return bmp;
    }

    public static (double H, double S, double L) Hsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2, d = max - min;
        if (d == 0) return (0, 0, l);
        double s = d / (1 - Math.Abs(2 * l - 1));
        double h = max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        return ((h + 360) % 360, s, l);
    }

    public static Color FromHsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - c / 2;
        var (r, g, b) = h < 60 ? (c, x, 0.0) : h < 120 ? (x, c, 0.0) : h < 180 ? (0.0, c, x) : h < 240 ? (0.0, x, c) : h < 300 ? (x, 0.0, c) : (c, 0.0, x);
        byte B(double v) => (byte)Math.Round(Math.Clamp(v + m, 0, 1) * 255);
        return Color.FromRgb(B(r), B(g), B(b));
    }
}
