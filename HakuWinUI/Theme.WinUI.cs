using System;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AnimePlayer;

// Accent colour themes for the WinUI app, the same palettes as the WPF app's Theme.cs. Everything uses these few shared
// brushes, and a theme change recolours them in place, so the whole app follows live.
static partial class ThemeW
{
    public static readonly (string Key, string Name)[] All =
    {
        ("sakura", "Sakura"), ("violet", "Violet"), ("ocean", "Ocean"), ("emerald", "Emerald"), ("sunset", "Sunset"), ("custom", "Custom"),
    };

    public static readonly SolidColorBrush Accent = new(), Light = new();
    public static readonly LinearGradientBrush Grad = new()
    {
        StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 0),
        GradientStops = { new GradientStop { Offset = 0 }, new GradientStop { Offset = 0.6 }, new GradientStop { Offset = 1 } },
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

    public static void Apply(string key)
    {
        var (a, b, c) = Colors(key);
        Accent.Color = b; Light.Color = a;
        Grad.GradientStops[0].Color = a; Grad.GradientStops[1].Color = b; Grad.GradientStops[2].Color = c;
    }

    static Color C(string hex) => Parse(hex)!.Value;

    public static Color? Parse(string? hex)
    {
        var t = hex?.Trim().TrimStart('#') ?? "";
        if (t.Length != 6 || !uint.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out var v)) return null;
        return Color.FromArgb(0xFF, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    // A custom colour's light and deep ends, shaped like the built-in themes: lighter, and darker with a slight hue turn.
    static (Color, Color, Color) FromOne(Color b)
    {
        var (h, s, l) = Hsl(b);
        return (FromHsl(h, s, Math.Min(0.86, l + 0.2)), b, FromHsl((h + 18) % 360, s, Math.Max(0.25, l - 0.12)));
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
        return Color.FromArgb(0xFF, B(r), B(g), B(b));
    }
}
