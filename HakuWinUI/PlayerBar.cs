using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace AnimePlayer;

// The player's bottom glass pill, laid out as the WPF app's (PlayerControls.xaml): previous / play / next, the time,
// the timeline (cache, AI buffer, opening/ending and chapter marks, hover preview), the length, volume, then audio &
// subtitles, speed, AI upscaling, autoplay, settings and fullscreen. The tint and rim are drawn here (GlassLook); the
// blur under it is mpv's glass shader (MainWindow.SendGlass gives it this rectangle).
sealed class PlayerBar
{
    public readonly Border Pill;
    public readonly Border Preview;          // the hover preview: added to the window above everything (MainWindow)
    public readonly Image PreviewImage = new() { Stretch = Stretch.UniformToFill };
    public readonly Button TracksButton, SpeedButton, MenuButton; // MainWindow opens its menus over these
    public Action? PlayPause, ToggleMute, ToggleFullscreen, Previous, Next, ToggleAi, SeekStarted, ToggleAutoplay;
    public Action<string, FrameworkElement>? OpenMenu; // "tracks" / "speed" / "settings", and the button it belongs to
    public Action<double, bool>? Seek;       // fraction 0..1, final (released)
    public Action<double>? Volume;           // 0..100
    public Action<double?>? Hover;           // seconds under the pointer on the timeline, or null when it leaves

    static readonly Color AiGreen = Color.FromArgb(0xFF, 0x3D, 0xDC, 0x84);
    readonly FontIcon playIcon = Glyph("", 26), muteIcon = Glyph(""), fullIcon = Glyph(""), aiIcon = Glyph("");
    readonly Button playButton, aiButton, autoButton;
    readonly TextBlock current = new() { FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = GlassLook.TextB, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 14, 0) };
    readonly TextBlock length = new() { FontSize = 15, Foreground = GlassLook.TextB, Opacity = 0.85, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
    readonly TextBlock speedText = new() { Text = "1×", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = GlassLook.TextB };
    readonly TextBlock previewText = new() { FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = GlassLook.TextB, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 2) };

    // timeline
    readonly Grid track = new() { Height = 36, VerticalAlignment = VerticalAlignment.Center, Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) };
    readonly Border cached = Bar(Color.FromArgb(0xB0, 0x3D, 0x8B, 0xFF), 5);
    readonly Border aiFill = Bar(AiGreen, 5);
    readonly Border hoverFill = Bar(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF), 5);
    readonly Border fill = Bar(Colors.White, 5);
    readonly Canvas marks = new() { IsHitTestVisible = false };
    readonly Ellipse knob = new() { Width = 16, Height = 16, Fill = new SolidColorBrush(Colors.White), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };

    // volume
    readonly Grid volTrack = new() { Width = 110, Height = 36, Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), Margin = new Thickness(2, 0, 0, 0) };
    readonly Border volFill = Bar(Colors.White, 4);
    readonly Ellipse volKnob = new() { Width = 12, Height = 12, Fill = new SolidColorBrush(Colors.White), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock volText = new() { Width = 36, TextAlignment = TextAlignment.Right, FontSize = 13, Foreground = GlassLook.TextB, Opacity = 0.85, VerticalAlignment = VerticalAlignment.Center };

    double aiUntil = -1, pos, dur, cache, volume = 100;
    bool dragging, volDragging, muted;
    List<Segment> segments = new();
    List<Chapter> chapters = new();

    static FontIcon Glyph(string g, double size = 18) => new() { Glyph = g, FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = size, Foreground = GlassLook.TextB };
    static Border Bar(Color c, double h) => new() { Height = h, CornerRadius = new CornerRadius(h / 2), Background = new SolidColorBrush(c), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Width = 0 };

    public PlayerBar()
    {
        // ---- timeline ----
        var rail = Bar(Color.FromArgb(0x5A, 0x8A, 0x8A, 0x8A), 5); rail.HorizontalAlignment = HorizontalAlignment.Stretch; rail.Width = double.NaN;
        fill.Background = ThemeW.Grad;
        knob.RenderTransform = new ScaleTransform { ScaleX = 0.6, ScaleY = 0.6, CenterX = 8, CenterY = 8 };
        foreach (var e in new UIElement[] { rail, cached, aiFill, marks, hoverFill, fill, knob }) track.Children.Add(e);
        track.SizeChanged += (_, _) => { Redraw(); DrawMarks(); };
        track.PointerEntered += (_, _) => Knob(1);
        track.PointerExited += (_, _) => { Knob(0.6); hoverFill.Width = 0; if (!dragging) { Preview.Visibility = Visibility.Collapsed; Hover?.Invoke(null); } };
        track.PointerPressed += (_, e) => { dragging = true; SeekStarted?.Invoke(); track.CapturePointer(e.Pointer); SeekTo(e, false); e.Handled = true; };
        track.PointerMoved += (_, e) => { if (dragging) SeekTo(e, false); HoverAt(e); };
        track.PointerReleased += (_, e) => { if (!dragging) return; SeekTo(e, true); dragging = false; track.ReleasePointerCapture(e.Pointer); };
        track.PointerCaptureLost += (_, _) => dragging = false;
        AutomationProperties.SetName(track, "Timeline");

        // ---- volume ----
        var volRail = Bar(Color.FromArgb(0x5A, 0x8A, 0x8A, 0x8A), 4); volRail.HorizontalAlignment = HorizontalAlignment.Stretch; volRail.Width = double.NaN;
        volTrack.Children.Add(volRail); volTrack.Children.Add(volFill); volTrack.Children.Add(volKnob);
        volTrack.SizeChanged += (_, _) => RedrawVolume();
        volTrack.PointerPressed += (_, e) => { volDragging = true; volTrack.CapturePointer(e.Pointer); VolumeAt(e); e.Handled = true; };
        volTrack.PointerMoved += (_, e) => { if (volDragging) VolumeAt(e); };
        volTrack.PointerReleased += (_, e) => { volDragging = false; volTrack.ReleasePointerCapture(e.Pointer); };
        volTrack.PointerCaptureLost += (_, _) => volDragging = false;
        AutomationProperties.SetName(volTrack, "Volume");

        // ---- buttons ----
        playButton = GlassLook.IconButton(playIcon, "Pause", "Play / Pause (Space)", () => PlayPause?.Invoke(), 52);
        var prev = GlassLook.IconButton(Glyph(""), "Previous", "Previous", () => Previous?.Invoke());
        var next = GlassLook.IconButton(Glyph(""), "Next", "Next", () => Next?.Invoke());
        var mute = GlassLook.IconButton(muteIcon, "Mute", "Mute (M)", () => ToggleMute?.Invoke());
        TracksButton = GlassLook.IconButton(Glyph(""), "Tracks", "Audio & subtitles", () => OpenMenu?.Invoke("tracks", TracksButton!));
        TracksButton.Margin = new Thickness(8, 0, 0, 0);
        SpeedButton = GlassLook.IconButton(speedText, "Speed", "Playback speed", () => OpenMenu?.Invoke("speed", SpeedButton!));
        SpeedButton.Width = 52; SpeedButton.Margin = new Thickness(2, 0, 0, 0);
        aiButton = GlassLook.IconButton(aiIcon, "AI upscaling", "AI upscaling (Ctrl+U)", () => ToggleAi?.Invoke());
        aiButton.Margin = new Thickness(2, 0, 0, 0);
        autoButton = GlassLook.IconButton(Glyph(""), "Autoplay", "Autoplay: on", () => ToggleAutoplay?.Invoke());
        autoButton.Margin = new Thickness(2, 0, 0, 0);
        MenuButton = GlassLook.IconButton(Glyph(""), "Settings", "Settings", () => OpenMenu?.Invoke("settings", MenuButton!));
        MenuButton.Margin = new Thickness(2, 0, 0, 0);
        var full = GlassLook.IconButton(fullIcon, "Fullscreen", "Fullscreen (F)", () => ToggleFullscreen?.Invoke());
        full.Margin = new Thickness(4, 0, 0, 0);

        var row = new Grid { Margin = new Thickness(14, 0, 16, 0) };
        var parts = new FrameworkElement[] { prev, playButton, next, current, track, length, mute, volTrack, volText, TracksButton, SpeedButton, aiButton, autoButton, MenuButton, full };
        for (int i = 0; i < parts.Length; i++)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = parts[i] == track ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            Grid.SetColumn(parts[i], i); row.Children.Add(parts[i]);
        }
        mute.Margin = new Thickness(10, 0, 0, 0);

        Pill = GlassLook.VideoPanel(row, 34);
        Pill.Height = 68;
        Pill.Margin = new Thickness(40, 0, 40, 28);
        Pill.VerticalAlignment = VerticalAlignment.Bottom;
        // compact (pop-out) size: no volume slider, the speaker button stays
        Pill.SizeChanged += (_, _) => volTrack.Visibility = volText.Visibility = Pill.ActualWidth < 880 ? Visibility.Collapsed : Visibility.Visible;

        // the hover preview: the frame at that second, and its time (and "Opening" / the chapter)
        var frame = new Border { Width = 320, Height = 180, CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)), Child = PreviewImage };
        var pv = new StackPanel();
        pv.Children.Add(frame); pv.Children.Add(previewText);
        Preview = GlassLook.Panel(pv, 16, new Thickness(8));
        Preview.Width = 336;
        Preview.HorizontalAlignment = HorizontalAlignment.Left;
        Preview.VerticalAlignment = VerticalAlignment.Top;
        Preview.IsHitTestVisible = false;
        Preview.Visibility = Visibility.Collapsed;
        RedrawVolume();
    }

    void Knob(double s) { var t = (ScaleTransform)knob.RenderTransform; t.ScaleX = t.ScaleY = s; }

    void SeekTo(PointerRoutedEventArgs e, bool final)
    {
        if (track.ActualWidth <= 0 || dur <= 0) return;
        double f = Math.Clamp(e.GetCurrentPoint(track).Position.X / track.ActualWidth, 0, 1);
        pos = f * dur;
        Redraw();
        Seek?.Invoke(f, final);
    }

    // the preview follows the pointer along the timeline, just above the pill
    void HoverAt(PointerRoutedEventArgs e)
    {
        if (track.ActualWidth <= 0 || dur <= 0) return;
        double x = Math.Clamp(e.GetCurrentPoint(track).Position.X, 0, track.ActualWidth);
        hoverFill.Width = x;
        double t = x / track.ActualWidth * dur;
        if (!Settings.Current.Previews || Preview.Parent is not FrameworkElement host) { Hover?.Invoke(t); return; }
        var at = track.TransformToVisual(host).TransformPoint(new Windows.Foundation.Point(x, 0));
        double pillTop = Pill.TransformToVisual(host).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
        double h = Preview.ActualHeight > 0 ? Preview.ActualHeight : 236;
        Preview.Margin = new Thickness(Math.Clamp(at.X - 168, 8, Math.Max(8, host.ActualWidth - 336 - 8)), pillTop - h - 14, 0, 0);
        var label = MarkLabel(t);
        previewText.Text = label.Length > 0 ? $"{Clock(t)}  ·  {label}" : Clock(t);
        Preview.Visibility = Visibility.Visible;
        Hover?.Invoke(t);
    }

    void VolumeAt(PointerRoutedEventArgs e)
    {
        double w = volTrack.ActualWidth - volKnob.Width;
        if (w <= 0) return;
        double v = Math.Round(Math.Clamp((e.GetCurrentPoint(volTrack).Position.X - volKnob.Width / 2) / w, 0, 1) * 100);
        volume = v; RedrawVolume();
        Volume?.Invoke(v);
    }

    void RedrawVolume()
    {
        double w = volTrack.ActualWidth > 0 ? volTrack.ActualWidth : 110, f = muted ? 0 : Math.Clamp(volume / 100, 0, 1);
        double x = f * Math.Max(0, w - volKnob.Width);
        volFill.Width = x + volKnob.Width / 2;
        volKnob.Margin = new Thickness(x, 0, 0, 0);
        volFill.Opacity = muted ? 0.4 : 1;
        volText.Text = muted ? "Off" : $"{Math.Round(volume)}";
        muteIcon.Glyph = muted || volume <= 0 ? "" : volume < 34 ? "" : volume < 67 ? "" : "";
    }

    public void SetTime(double t) { if (!dragging) { pos = t; Redraw(); } }
    public void SetDuration(double d) { dur = d; Redraw(); DrawMarks(); }
    public void SetCache(double t) { cache = t; Redraw(); }
    public void SetPause(bool p) { playIcon.Glyph = p ? "" : ""; AutomationProperties.SetName(playButton, p ? "Play" : "Pause"); }
    public void SetMute(bool m) { muted = m; RedrawVolume(); }
    public void SetFullscreen(bool f) => fullIcon.Glyph = f ? "" : "";
    public void SetVolume(double v) { if (!volDragging) { volume = v; RedrawVolume(); } }
    public void SetSpeed(double v) => speedText.Text = $"{v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}×";
    public void SetMarks(List<Segment> s, List<Chapter> c) { segments = s; chapters = c; DrawMarks(); }

    // The AI button: green while upscaling, bright when on but not upscaling this video, dimmed when off.
    public void SetAiButton(bool on, bool active, string tip)
    {
        aiButton.Opacity = on ? 1 : 0.4;
        aiIcon.Foreground = active ? new SolidColorBrush(AiGreen) : GlassLook.TextB;
        ToolTipService.SetToolTip(aiButton, tip);
        AutomationProperties.SetHelpText(aiButton, tip); // what tests read
    }
    public void SetAiTip(string tip) => ToolTipService.SetToolTip(aiButton, tip); // the tooltip only (HelpText stays the state)
    public void SetAiBusy(bool busy) => aiIcon.Foreground = busy ? new SolidColorBrush(AiGreen) : GlassLook.TextB;

    public void SetAutoplay(bool on)
    {
        autoButton.Opacity = on ? 1 : 0.4;
        ToolTipService.SetToolTip(autoButton, on ? "Autoplay: on" : "Autoplay: off");
        AutomationProperties.SetHelpText(autoButton, on ? "on" : "off");
    }

    // the frames the AI has ready ahead, in green on the timeline (until: the last one's time, -1 none)
    public void SetAiBuffer(int frames, int cap, double until) { aiFrames = frames; aiCap = cap; aiUntil = frames > 0 ? until : -1; Redraw(); }
    int aiFrames, aiCap;

    void Redraw()
    {
        // as the WPF app (PlayerControls.Redraw): the knob travels inside the track, whole pixels
        double w = track.ActualWidth, f = dur > 0 ? Math.Clamp(pos / dur, 0, 1) : 0;
        double x = Math.Round(f * Math.Max(0, w - knob.Width));
        fill.Width = x + knob.Width / 2;
        cached.Width = dur > 0 ? Math.Round(Math.Clamp(cache / dur, 0, 1) * w) : 0;
        // a second of frames is about a pixel on a long video, so past the knob the green also grows with how full the
        // AI's queue is (up to 24 px when full): it shows filling up and running down
        double knobEdge = x + knob.Width, tip = aiCap > 0 ? 24.0 * aiFrames / aiCap : 0;
        aiFill.Width = aiUntil >= 0 && dur > 0 ? Math.Min(w, Math.Max(Math.Round(Math.Clamp(aiUntil / dur, 0, 1) * w), Math.Round(knobEdge + tip))) : 0;
        knob.Margin = new Thickness(x, 0, 0, 0);
        current.Text = Clock(pos);
        length.Text = Clock(dur);
    }

    // openings / endings as brighter stretches of the rail, chapters as ticks (as the WPF app)
    void DrawMarks()
    {
        marks.Children.Clear();
        double w = track.ActualWidth, h = track.ActualHeight;
        if (w <= 0 || dur <= 0) return;
        double X(double t) => Math.Clamp(t / dur, 0, 1) * w;
        foreach (var s in segments)
        {
            var b = new Border { Height = 5, Width = Math.Max(3, X(s.End) - X(s.Start)), CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)) };
            Canvas.SetLeft(b, X(s.Start)); Canvas.SetTop(b, (h - 5) / 2);
            marks.Children.Add(b);
        }
        foreach (var c in chapters)
        {
            if (c.Time <= 1 || c.Time >= dur - 1) continue;
            var tick = new Rectangle { Width = 2, Height = 11, RadiusX = 1, RadiusY = 1, Fill = new SolidColorBrush(Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF)) };
            Canvas.SetLeft(tick, X(c.Time) - 1); Canvas.SetTop(tick, (h - 11) / 2);
            marks.Children.Add(tick);
        }
    }

    // "Opening", or the chapter's own name (not "Chapter 3")
    string MarkLabel(double t)
    {
        foreach (var s in segments) if (t >= s.Start && t < s.End) return s.Kind switch { "op" => "Opening", "ed" => "Ending", _ => "Recap" };
        Chapter? at = null;
        foreach (var c in chapters) if (c.Time <= t) at = c;
        return at is { Title.Length: > 0 } && !System.Text.RegularExpressions.Regex.IsMatch(at.Title, @"^(Chapter\s*)?\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? at.Title : "";
    }

    public static string Clock(double s)
    {
        if (s < 0 || double.IsNaN(s)) s = 0;
        var t = TimeSpan.FromSeconds(Math.Floor(s));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }
}
