using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace AnimePlayer;

// The player's glass, as the WPF app draws it: a light tint (the blur underneath is mpv's glass shader), a white sheen
// across the top and a thin bright rim. Every glass piece over the video (pill, top bar, skip card) is made here.
static partial class GlassLook
{
    public static readonly Color Text = Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF);

    // THE floating glass panel (WPF App.xaml "GlassPanel"): see-through tint, a white sheen across the top and a bright
    // specular rim. Used for tooltips, pop-ups, the toast, the hover preview and the AI panel.
    public static Border Panel(UIElement child, double radius, Thickness padding)
    {
        var sheen = new Border { CornerRadius = new CornerRadius(radius), IsHitTestVisible = false,
            Background = Stops(new Point(0.4, 1), (Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF), 0), (Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF), 0.5), (Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF), 1)) };
        var holder = new ContentPresenter { Content = child, Margin = padding };
        var g = new Grid(); g.Children.Add(sheen); g.Children.Add(holder);
        return new Border
        {
            Child = g, CornerRadius = new CornerRadius(radius), BorderThickness = new Thickness(1),
            Background = Stops(new Point(0.3, 1), (Color.FromArgb(0x5A, 0x2A, 0x2E, 0x3C), 0), (Color.FromArgb(0x6C, 0x12, 0x14, 0x1B), 1)),
            BorderBrush = Stops(new Point(1, 1), (Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF), 0), (Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF), 0.4), (Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF), 0.7), (Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF), 1)),
        };
    }

    // Glass over the video (WPF GlassSurface with mpv's live blur): a light tint, a soft top sheen with a hot spot near
    // the top-left, and a faint specular rim. The blur itself is mpv's glass shader.
    public static Border VideoPanel(UIElement child, double radius, Color? shade = null)
    {
        var g = new Grid { CornerRadius = new CornerRadius(radius) };
        g.Children.Add(new Border { IsHitTestVisible = false, Background = Stops(new Point(0, 1), (Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF), 0), (Color.FromArgb(0x06, 0xFF, 0xFF, 0xFF), 0.45), (Color.FromArgb(0, 0xFF, 0xFF, 0xFF), 1)) });
        g.Children.Add(new Border { IsHitTestVisible = false, Background = new RadialGradientBrush
        {
            Center = new Point(0.12, 0), GradientOrigin = new Point(0.1, 0), RadiusX = 0.35, RadiusY = 1.1,
            GradientStops = { new GradientStop { Color = Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF), Offset = 0 }, new GradientStop { Color = Color.FromArgb(0, 0xFF, 0xFF, 0xFF), Offset = 1 } },
        } });
        if (shade is { } sh) g.Children.Add(new Border { IsHitTestVisible = false, Background = new SolidColorBrush(sh) });
        g.Children.Add(child);
        return new Border
        {
            Child = g, CornerRadius = new CornerRadius(radius), BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromArgb(46, 12, 14, 20)),
            BorderBrush = Stops(new Point(1, 1), (Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF), 0), (Color.FromArgb(0x11, 0xFF, 0xFF, 0xFF), 0.35), (Color.FromArgb(0x05, 0xFF, 0xFF, 0xFF), 0.6), (Color.FromArgb(0x1D, 0xFF, 0xFF, 0xFF), 1)),
        };
    }

    static LinearGradientBrush Stops(Point end, params (Color C, double At)[] stops)
    {
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = end };
        foreach (var (c, at) in stops) b.GradientStops.Add(new GradientStop { Color = c, Offset = at });
        return b;
    }

    // Haku's accent (the theme's gradient: ThemeW, recoloured live)
    public static Brush Accent() => ThemeW.Grad;

    static LinearGradientBrush Gradient(Point end, Color a, Color b) => new()
    {
        StartPoint = new Point(0, 0), EndPoint = end,
        GradientStops = { new GradientStop { Color = a, Offset = 0 }, new GradientStop { Color = b, Offset = 1 } },
    };

    public static FontIcon Icon(string glyph, double size = 16) => new()
    {
        Glyph = glyph, FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = size, Foreground = new SolidColorBrush(Text),
    };

    public static TextBlock Label(string text, double size = 14, bool bold = false) => new()
    {
        Text = text, FontSize = size, Foreground = new SolidColorBrush(Text), VerticalAlignment = VerticalAlignment.Center,
        FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
    };

    // A flat button: no background, never takes keyboard focus (keys belong to mpv), named for UI Automation.
    public static Button Flat(object content, string name, Action click, double? size = null)
    {
        var b = Bare(content, name, click); // no chrome, as the WPF app's Flat
        b.Padding = new Thickness(8, 4, 8, 4);
        if (size is { } sz) { b.Width = sz; b.Height = sz; b.Padding = new Thickness(0); b.HorizontalContentAlignment = HorizontalAlignment.Center; b.VerticalContentAlignment = VerticalAlignment.Center; }
        ToolTipService.SetToolTip(b, name);
        return b;
    }
}

// The bar across the top of the video, as the WPF app's: Back, what's playing, the quality badge, pop-out and the
// window's own buttons. Its empty middle drags the window (MainWindow makes TitleArea the title bar while playing).
sealed class TopBar
{
    public readonly Border Panel;
    public readonly Grid TitleArea = new() { Background = new SolidColorBrush(Colors.Transparent) };
    public Action? Back, PopOut, Minimize, Maximize, CloseApp;
    readonly TextBlock title = new() { FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = GlassLook.TextB, VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(16, 0, 16, 0) };
    readonly Border quality = new() { CornerRadius = new CornerRadius(7), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center,
        BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), Background = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)), Visibility = Visibility.Collapsed };
    readonly TextBlock qualityText = new() { FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = GlassLook.TextB };
    readonly StackPanel winButtons = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    readonly Button popButton, maxButton;

    public TopBar()
    {
        quality.Child = qualityText;
        ToolTipService.SetToolTip(quality, "Playing the original file, no downscaling");
        popButton = GlassLook.Caption("\uE8A7", "Pop out", "Pop out: small player that stays on top", () => PopOut?.Invoke(), 13, color: GlassLook.TextB);
        popButton.Margin = new Thickness(0, 0, 10, 0);
        maxButton = GlassLook.Caption("\uE922", "Maximize", "Maximize", () => Maximize?.Invoke(), color: GlassLook.TextB);
        maxButton.Margin = new Thickness(4, 0, 4, 0);
        winButtons.Children.Add(popButton);
        winButtons.Children.Add(GlassLook.Caption("\uE921", "Minimize", "Minimize", () => Minimize?.Invoke(), color: GlassLook.TextB));
        winButtons.Children.Add(maxButton);
        winButtons.Children.Add(GlassLook.Caption("\uE8BB", "Close", "Close", () => CloseApp?.Invoke(), close: true, color: GlassLook.TextB));
        TitleArea.Children.Add(title);

        var row = new Grid { Margin = new Thickness(10, 0, 12, 0) };
        foreach (var w in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto }) row.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        var back = GlassLook.Back(() => Back?.Invoke());
        row.Children.Add(back);
        Grid.SetColumn(TitleArea, 1); row.Children.Add(TitleArea);
        Grid.SetColumn(quality, 2); row.Children.Add(quality);
        Grid.SetColumn(winButtons, 3); row.Children.Add(winButtons);
        Panel = GlassLook.VideoPanel(row, 28);
        Panel.Height = 56;
        Panel.VerticalAlignment = VerticalAlignment.Top;
        Panel.Margin = new Thickness(24, 20, 24, 0);
    }

    // the pop-out button: "back in" while popped out
    public void SetPopped(bool on)
    {
        ((FontIcon)popButton.Content).Glyph = on ? "\uE73F" : "\uE8A7";
        ToolTipService.SetToolTip(popButton, on ? "Back to the normal window" : "Pop out: small player that stays on top");
    }

    public void SetMaximized(bool on)
    {
        ((FontIcon)maxButton.Content).Glyph = on ? "\uE923" : "\uE922";
        ToolTipService.SetToolTip(maxButton, on ? "Restore" : "Maximize");
    }

    // no window to minimize in fullscreen
    public void SetFullscreen(bool on) => winButtons.Visibility = on ? Visibility.Collapsed : Visibility.Visible;

    public void SetTitle(string show, string ep) => title.Text = ep.Length > 0 ? $"{show}  ·  {ep}" : show;

    // "1080p HEVC", "1080p → 4K  H264  AI", or nothing
    public void SetBadge(string text)
    {
        qualityText.Text = text;
        quality.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(quality, text);
    }
}

// The card at the bottom right: "Skip opening" while one plays, "Up next" with a countdown as the ending starts.
sealed class PromptCard
{
    public readonly Border Panel;
    readonly StackPanel body = new();
    TextBlock? countdown;

    public PromptCard()
    {
        body.Margin = new Thickness(18, 12, 14, 12);
        Panel = GlassLook.VideoPanel(body, 20, Color.FromArgb(0x38, 0x08, 0x0A, 0x10));
        Panel.HorizontalAlignment = HorizontalAlignment.Right;
        Panel.VerticalAlignment = VerticalAlignment.Bottom;
        Panel.Margin = new Thickness(0, 0, 40, 112);
        Panel.Visibility = Visibility.Collapsed;
        Panel.Opacity = 0;
    }

    public void ShowSkip(string kindName, Action skip)
    {
        body.Children.Clear(); countdown = null;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(GlassLook.Label($"Skip {kindName.ToLowerInvariant()}", 16, bold: true));
        var g = GlassLook.Icon("", 14); g.Margin = new Thickness(12, 1, 2, 0);
        row.Children.Add(g);
        body.Children.Add(GlassLook.Flat(row, $"Skip {kindName}", skip)); // "Skip Opening": the name tests look for
    }

    public void ShowUpNext(string next, bool autoplay, Action playNow, Action cancel)
    {
        body.Children.Clear();
        var head = GlassLook.Label("UP NEXT", 11, bold: true); head.Foreground = GlassLook.Accent();
        body.Children.Add(head);
        var name = GlassLook.Label(next, 15, bold: true); name.MaxWidth = 340; name.TextTrimming = TextTrimming.CharacterEllipsis; name.Margin = new Thickness(0, 3, 0, 10);
        body.Children.Add(name);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var play = new StackPanel { Orientation = Orientation.Horizontal };
        play.Children.Add(GlassLook.Icon("", 13));
        var pl = GlassLook.Label(autoplay ? "Play now" : "Play next", 14, bold: true); pl.Margin = new Thickness(8, 0, 0, 0);
        play.Children.Add(pl);
        row.Children.Add(GlassLook.Flat(new Border { CornerRadius = new CornerRadius(16), Background = GlassLook.Accent(), Padding = new Thickness(14, 6, 16, 6), Child = play }, "Play next episode", playNow));
        countdown = GlassLook.Label("", 13); countdown.Opacity = 0.75; countdown.MinWidth = 34; countdown.Margin = new Thickness(10, 0, 14, 0);
        row.Children.Add(countdown);
        var c = GlassLook.Label("Cancel", 14); c.Opacity = 0.8;
        row.Children.Add(GlassLook.Flat(c, "Cancel up next", cancel));
        body.Children.Add(row);
    }

    public void SetCountdown(string text) { if (countdown != null) countdown.Text = text; }
}

// "Starting AI upscaling": a glass panel in the middle of the video while the AI gets ready, with the frames it has ready.
sealed class AiLoadingPanel
{
    public readonly Border Panel;
    readonly TextBlock text = GlassLook.Label("", 15.5, bold: true), sub = GlassLook.Label("", 13);
    readonly Border fill = new() { Height = 4, Width = 0, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x3D, 0xDC, 0x84)) };
    public bool On { get; private set; }

    public AiLoadingPanel()
    {
        var green = Color.FromArgb(0xFF, 0x3D, 0xDC, 0x84);
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        sub.FontSize = 12.5; sub.Foreground = GlassLook.MutedB; sub.Margin = new Thickness(0, 3, 0, 0);
        var col = new StackPanel { Width = 230, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        col.Children.Add(text); col.Children.Add(sub);
        col.Children.Add(new Grid { Height = 4, Margin = new Thickness(0, 9, 0, 0), Children = { new Border { CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)) }, fill } });
        var spin = new RotateTransform { CenterX = 27, CenterY = 27 };
        var arc = new Microsoft.UI.Xaml.Shapes.Ellipse { StrokeThickness = 3, Stroke = new SolidColorBrush(green), StrokeDashArray = { 9, 24 }, StrokeDashCap = PenLineCap.Round, RenderTransform = spin };
        var spark = new FontIcon { Glyph = "\uF4A5", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 22, Foreground = new SolidColorBrush(green), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var ring = new Grid { Width = 54, Height = 54, VerticalAlignment = VerticalAlignment.Center };
        ring.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { StrokeThickness = 3, Stroke = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)) });
        ring.Children.Add(arc); ring.Children.Add(spark);
        var turn = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation { From = 0, To = 360, Duration = TimeSpan.FromSeconds(1.2), RepeatBehavior = Microsoft.UI.Xaml.Media.Animation.RepeatBehavior.Forever };
        var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard(); Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(turn, spin); Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(turn, "Angle"); sb.Children.Add(turn);
        ring.Loaded += (_, _) => sb.Begin();
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(ring);
        row.Children.Add(col);
        Panel = GlassLook.Panel(row, 14, new Thickness(22, 18, 26, 18));
        Panel.HorizontalAlignment = HorizontalAlignment.Center;
        Panel.VerticalAlignment = VerticalAlignment.Center;
        Panel.Visibility = Visibility.Collapsed;
        Panel.IsHitTestVisible = false;
    }

    public void Show(string? what, int frames, int cap, string? note)
    {
        On = what != null;
        Panel.Visibility = On ? Visibility.Visible : Visibility.Collapsed;
        if (!On) return;
        text.Text = what!;
        sub.Text = note ?? (cap > 0 ? $"{frames} of {cap} frames ready" : "Getting the AI ready");
        fill.Width = cap > 0 ? Math.Round(230.0 * Math.Clamp((double)frames / cap, 0, 1)) : 0;
        AutomationProperties.SetName(Panel, what);
    }
}

// A short message at the bottom of the window ("AI upscaling on: Balanced"), gone after a few seconds.
sealed class ToastBox
{
    public readonly Border Panel;
    readonly TextBlock text = GlassLook.Label("", 14);
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer timer;

    public ToastBox(Microsoft.UI.Dispatching.DispatcherQueue queue)
    {
        text.TextWrapping = TextWrapping.Wrap; text.MaxWidth = 560; text.FontSize = 15;
        var icon = new FontIcon { Glyph = "\uE72C", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 14, Foreground = ThemeW.Accent, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 10, 0) };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(icon); row.Children.Add(text);
        Panel = GlassLook.Panel(row, 14, new Thickness(22, 11, 22, 11));
        Panel.HorizontalAlignment = HorizontalAlignment.Center;
        Panel.VerticalAlignment = VerticalAlignment.Bottom;
        Panel.Margin = new Thickness(0, 0, 0, 100);
        Panel.Visibility = Visibility.Collapsed;
        Panel.IsHitTestVisible = false;
        Panel.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(200) };
        timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(3.2);
        timer.Tick += (_, _) => { timer.Stop(); Panel.Opacity = 0; Hide(); };
    }

    async void Hide() { await System.Threading.Tasks.Task.Delay(220); if (Panel.Opacity == 0) Panel.Visibility = Visibility.Collapsed; }

    public void Show(string message)
    {
        text.Text = message;
        AutomationProperties.SetName(Panel, message);
        Panel.Visibility = Visibility.Visible;
        Panel.Opacity = 1;
        timer.Stop(); timer.Start();
    }
}
