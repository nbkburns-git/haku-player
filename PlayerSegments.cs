using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace AnimePlayer;

// Chapters and openings/endings in the player: marks on the timeline, "Skip opening", and the "Up next" countdown.
public partial class PlayerControls
{
    List<Chapter> chapters = new();
    List<Segment> segments = new();
    public string? NextTitle; // the next video's name, or null on the last one (set by the main window for each file)
    string promptKey = "";
    double shownLeft = double.NaN; // the countdown number on screen
    bool upNextDismissed, upNextFired;
    double? upNextFrom;     // playback time the Up next countdown started at (null: card not up)
    double lastPromptTime;  // last position seen, to spot seeks
    readonly HashSet<Segment> autoSkipped = new();
    GlassSurface? promptGlass;
    TextBlock? countdownText;

    // where this video counts as watched (the ending's start), when known
    public double? EndingStart => segments.FirstOrDefault(s => s.Kind == "ed")?.Start;

    public void SetChapters(List<Chapter> c) { chapters = c; DrawMarks(); }
    public void SetSegments(List<Segment> s) { segments = s; DrawMarks(); UpdatePrompt(); }

    void ResetSegments()
    {
        chapters = new(); segments = new();
        upNextDismissed = upNextFired = false;
        upNextFrom = null;
        autoSkipped.Clear();
        SetPrompt("", null);
        DrawMarks();
    }

    void DrawMarks()
    {
        Marks.Children.Clear();
        double w = Timeline.ActualWidth, h = Timeline.ActualHeight;
        if (w <= 0 || duration <= 0) return;
        double X(double t) => Math.Clamp(t / duration, 0, 1) * w;
        foreach (var s in segments)
        {
            var b = new Border { Height = 5, CornerRadius = new CornerRadius(3), Width = Math.Max(3, X(s.End) - X(s.Start)),
                Background = new SolidColorBrush(Color.FromArgb(0x70, 255, 255, 255)) };
            Canvas.SetLeft(b, X(s.Start)); Canvas.SetTop(b, (h - 5) / 2);
            Marks.Children.Add(b);
        }
        foreach (var c in chapters.Where(c => c.Time > 1 && c.Time < duration - 1))
        {
            var tick = new Rectangle { Width = 2, Height = 11, RadiusX = 1, RadiusY = 1, Fill = new SolidColorBrush(Color.FromArgb(0xC8, 255, 255, 255)) };
            Canvas.SetLeft(tick, X(c.Time) - 1); Canvas.SetTop(tick, (h - 11) / 2);
            Marks.Children.Add(tick);
        }
    }

    static string KindName(string k) => k switch { "op" => "Opening", "ed" => "Ending", _ => "Recap" };

    // for the hover preview: "Opening", or the chapter's own name
    string MarkLabel(double t)
    {
        if (segments.FirstOrDefault(s => t >= s.Start && t < s.End) is { } seg) return KindName(seg.Kind);
        var c = chapters.LastOrDefault(x => x.Time <= t);
        return c != null && c.Title.Length > 0 && !Regex.IsMatch(c.Title, @"^(Chapter\s*)?\d+$", RegexOptions.IgnoreCase) ? c.Title : "";
    }

    // Runs with every position update (also while the controls are hidden).
    void UpdatePrompt()
    {
        if (duration <= 0) { SetPrompt("", null); return; }
        var seg = segments.FirstOrDefault(s => time >= s.Start && time < s.End - 0.5);
        if (S.AutoSkip && seg is { Kind: "op" or "recap" } && autoSkipped.Add(seg)) { Seek(seg.End, "absolute+exact"); return; }

        // Up next: from the start of the ending (or the last 20 s when there's no ending known), counting down with playback
        double at = EndingStart ?? duration - 20;
        bool jumped = Math.Abs(time - lastPromptTime) > 1.5; // a seek, not playback
        lastPromptTime = time;
        if (S.UpNext && NextTitle != null && !upNextDismissed && duration > 120 && time >= at && (S.Autoplay || shown || time - at < 8))
        {
            SetPrompt("next", null);
            // 10 s from when the card came up (or from the latest seek), so seeking deep into the ending still gives you time to choose
            if (upNextFrom == null || jumped || time < upNextFrom) upNextFrom = time;
            double left = Math.Ceiling(10 - (time - upNextFrom.Value));
            if (countdownText != null && left != shownLeft) { shownLeft = left; countdownText.Text = S.Autoplay ? $"in {Math.Max(0, left)}" : ""; } // once a second, not every frame
            if (S.Autoplay && left <= 0 && !upNextFired) { upNextFired = true; Send(new object[] { "playlist-next" }); }
            return;
        }
        upNextFrom = null;
        // an ending that runs to the very end has nothing to skip to
        if (S.SkipButtons && seg != null && !(seg.Kind == "ed" && seg.End >= duration - 3) && (shown || time - seg.Start < 8))
        { SetPrompt($"skip:{seg.Kind}:{seg.Start}", seg); return; }
        SetPrompt("", null);
    }

    void SetPrompt(string key, Segment? seg)
    {
        if (key == promptKey) return;
        promptKey = key;
        PromptBody.Children.Clear();
        countdownText = null;
        shownLeft = double.NaN;
        WatchGlass();
        if (key == "")
        {
            if (promptGlass != null) GlassFade(Prompt, promptGlass, null, 0, TimeSpan.FromMilliseconds(200), null, () => { if (promptKey == "") Prompt.Visibility = Visibility.Collapsed; });
            return;
        }
        if (promptGlass == null) { promptGlass = new GlassSurface(Prompt, 20) { Live = LiveGlass }; UpdateFrostViewbox(); }
        if (key == "next") BuildUpNext(); else BuildSkip(seg!);
        if (lastFrame != null) promptGlass.SetFrame(lastFrame);
        Prompt.Visibility = Visibility.Visible;
        GlassFade(Prompt, promptGlass, null, 1, TimeSpan.FromMilliseconds(200), null);
    }

    static TextBlock Glyph(string g, double size) => new() { Text = g, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = size, VerticalAlignment = VerticalAlignment.Center };

    void BuildSkip(Segment seg)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = $"Skip {KindName(seg.Kind).ToLowerInvariant()}", FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var g = Glyph("", 14); g.Margin = new Thickness(12, 1, 4, 0);
        row.Children.Add(g);
        var b = new Button { Style = (Style)Application.Current.Resources["Flat"], Content = row, Foreground = (Brush)Application.Current.Resources["Text"] };
        System.Windows.Automation.AutomationProperties.SetName(b, $"Skip {KindName(seg.Kind)}");
        b.Click += (_, _) => { Seek(seg.End, "absolute+exact"); Activity(); };
        PromptBody.Children.Add(b);
    }

    void BuildUpNext()
    {
        PromptBody.Children.Add(new TextBlock { Text = "UP NEXT", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = (Brush)Application.Current.Resources["Accent"] });
        PromptBody.Children.Add(new TextBlock { Text = NextTitle, FontSize = 15, FontWeight = FontWeights.SemiBold, MaxWidth = 340, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 10) });
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var play = new StackPanel { Orientation = Orientation.Horizontal };
        play.Children.Add(Glyph("", 13));
        play.Children.Add(new TextBlock { Text = S.Autoplay ? "Play now" : "Play next", FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var playBtn = new Button { Style = (Style)Application.Current.Resources["Flat"], Foreground = (Brush)Application.Current.Resources["Text"],
            Content = new Border { CornerRadius = new CornerRadius(16), Background = (Brush)Application.Current.Resources["AccentGrad"], Padding = new Thickness(14, 6, 16, 6), Child = play } };
        System.Windows.Automation.AutomationProperties.SetName(playBtn, "Play next episode");
        playBtn.Click += (_, _) => { upNextFired = true; Send(new object[] { "playlist-next" }); };
        row.Children.Add(playBtn);
        countdownText = new TextBlock { FontSize = 13, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 14, 0), MinWidth = 34 };
        row.Children.Add(countdownText);
        var cancel = new Button { Style = (Style)Application.Current.Resources["Flat"], Foreground = (Brush)Application.Current.Resources["Text"],
            Content = new TextBlock { Text = "Cancel", FontSize = 14, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center } };
        System.Windows.Automation.AutomationProperties.SetName(cancel, "Cancel up next");
        cancel.Click += (_, _) => { upNextDismissed = true; SetPrompt("", null); };
        row.Children.Add(cancel);
        PromptBody.Children.Add(row);
    }
}
