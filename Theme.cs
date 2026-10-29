using System.Windows;
using System.Windows.Media;

namespace AnimePlayer;

// Accent colour themes: swaps the app's Accent / AccentGrad brushes; everything using them recolours live.
static class Theme
{
    public static readonly (string Key, string Name)[] All =
    {
        ("sakura", "Sakura"), ("violet", "Violet"), ("ocean", "Ocean"), ("emerald", "Emerald"), ("sunset", "Sunset"),
    };

    // light end, main accent, deep end
    public static (Color A, Color B, Color C) Colors(string key) => key switch
    {
        "violet" => (C("#C3A6FF"), C("#8E5CFF"), C("#4F5BFF")),
        "ocean" => (C("#7FE9FF"), C("#2BA8FF"), C("#3A5BFF")),
        "emerald" => (C("#8DF5C6"), C("#22C48A"), C("#1A8FB0")),
        "sunset" => (C("#FFC36B"), C("#FF8A3D"), C("#E8475F")),
        _ => (C("#FF7A8C"), C("#E8475F"), C("#C23BD0")),
    };

    static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static void Install() => Apply(Settings.Current.Theme);

    // App resources get frozen, so a theme change swaps in new brushes; DynamicResource users update live.
    public static void Apply(string key)
    {
        var (a, b, c) = Colors(key);
        var r = Application.Current.Resources;
        r["Accent"] = new SolidColorBrush(b);
        r["AccentColor"] = b; // for glows (effects take a Color, not a brush)
        r["AccentGrad"] = new LinearGradientBrush(new GradientStopCollection { new(a, 0), new(b, 0.6), new(c, 1) }, new Point(0, 0), new Point(1, 0));
    }
}
