using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace AnimePlayer;

// Home page "Continue watching": a row of every show you're part-way through, or onto the next episode of, newest first.
public partial class MainWindow
{
    const int ContinueMax = 10;
    Button? singleCard; // with only one show, its card spans the row
    bool continueSized;
    readonly Dictionary<string, ImageSource?> continueArt = new(StringComparer.OrdinalIgnoreCase);

    // (show, episode to play, resume record if mid-episode, when you last watched it)
    List<(Entry E, int Index, WatchRec? Rec, DateTime Last)> ContinueList()
    {
        var list = new List<(Entry, int, WatchRec?, DateTime)>();
        foreach (var e in Everything(lib).Where(x => x.Unit != "themes"))
        {
            int bi = -1; WatchRec? best = null;
            for (int i = 0; i < e.Videos.Count; i++)
                if (Watch.Get(e.Videos[i].Path) is { } r && (best == null || r.Last > best.Last)) { best = r; bi = i; }
            if (best == null) continue;
            if (Watch.Data.Hidden.TryGetValue(e.Folder, out var hid) && best.Last <= hid) continue; // closed, and not watched since
            if (!best.Done && best.Pos > 5) list.Add((e, bi, best, best.Last)); // mid-episode: resume it
            else if (best.Done && e.Kind != "movie")
            {
                int next = e.Videos.FindIndex(bi + 1, v => v.Season >= 0 && !Watch.IsDone(v.Path)); // finished one: the next unwatched episode
                if (next >= 0) list.Add((e, next, null, best.Last));
            }
        }
        return list.OrderByDescending(x => x.Item4).Take(ContinueMax).ToList();
    }

    void UpdateContinue()
    {
        var items = lib == null ? new() : ContinueList();
        ContinueItems.Children.Clear();
        ContinueRow.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (e, i, rec, _) in items) ContinueItems.Children.Add(ContinueCardFor(e, i, rec, single: items.Count == 1));
        ContinueRow.ScrollToHorizontalOffset(0);
    }

    // one card: banner (or cover), show, episode, progress, Resume / Play
    Button ContinueCardFor(Entry e, int index, WatchRec? rec, bool single)
    {
        var v = e.Videos[index];
        bool started = rec is { Pos: > 5 };
        var body = new Grid();
        body.Children.Add(new System.Windows.Shapes.Rectangle { Fill = new SolidColorBrush(Color.FromArgb(0x22, 255, 255, 255)) });
        body.Children.Add(new Image { Source = ArtFor(e), Stretch = Stretch.UniformToFill });
        body.Children.Add(new System.Windows.Shapes.Rectangle
        {
            Fill = new LinearGradientBrush(new GradientStopCollection
            {
                new(Color.FromArgb(0xF0, 0x0A, 0x0B, 0x10), 0), new(Color.FromArgb(0xC0, 0x0A, 0x0B, 0x10), 0.45), new(Color.FromArgb(0x30, 0x0A, 0x0B, 0x10), 1),
            }, new Point(0, 0), new Point(1, 0)),
        });

        var dock = new DockPanel { Margin = new Thickness(single ? 34 : 28, 0, single ? 30 : 24, 0) };
        var pill = new Border
        {
            CornerRadius = new CornerRadius(24), Background = (Brush)FindResource("AccentGrad"), Padding = new Thickness(single ? 24 : 18, 10, single ? 24 : 18, 10),
            VerticalAlignment = VerticalAlignment.Center, Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = 0.4 },
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) },
                    new TextBlock { Text = started ? "Resume" : "Play", FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        DockPanel.SetDock(pill, Dock.Right);
        dock.Children.Add(pill);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        text.Children.Add(new TextBlock { Text = started ? "CONTINUE WATCHING" : "UP NEXT", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource("Accent") });
        text.Children.Add(new TextBlock { Text = e.Title, FontSize = single ? 32 : 26, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = e.Kind == "movie" ? v.Title : v.Label, FontSize = 16, Opacity = 0.8, Margin = new Thickness(0, 2, 0, 12), TextTrimming = TextTrimming.CharacterEllipsis });
        var progress = new StackPanel { Orientation = Orientation.Horizontal };
        double frac = started && rec!.Dur > 0 ? Math.Clamp(rec.Pos / rec.Dur, 0, 1) : 0;
        double barW = single ? 340 : 200;
        var bar = new Grid { Width = barW, Height = 5, VerticalAlignment = VerticalAlignment.Center };
        bar.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromArgb(0x38, 255, 255, 255)) });
        bar.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = (Brush)FindResource("AccentGrad"), HorizontalAlignment = HorizontalAlignment.Left, Width = barW * frac });
        if (started && rec!.Dur > 0) progress.Children.Add(bar);
        progress.Children.Add(new TextBlock
        {
            Text = started ? rec!.Dur > 0 ? $"{Math.Ceiling((rec.Dur - rec.Pos) / 60)} min left" : "Resume from " + PlayerControls.Fmt(rec.Pos) : "Next episode",
            FontSize = 14, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(started && rec!.Dur > 0 ? 14 : 0, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        });
        text.Children.Add(progress);
        dock.Children.Add(text);
        body.Children.Add(dock);
        var close = new Button
        {
            Template = (ControlTemplate)FindResource("CloseDotTemplate"), Content = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 11 }, Width = 30, Height = 30, Focusable = false, Cursor = Cursors.Hand, Foreground = (Brush)FindResource("Text"),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 12, 14, 0), ToolTip = "Remove from Continue watching",
        };
        System.Windows.Automation.AutomationProperties.SetName(close, $"Remove {e.Title} from Continue watching");
        close.Click += (_, a) =>
        {
            a.Handled = true; // not a click on the card
            Watch.Data.Hidden[e.Folder] = DateTime.Now; // comes back once you watch it again
            Watch.Save();
            UpdateContinue();
        };
        body.Children.Add(close);
        RoundClip(body, 28);

        var card = new Button
        {
            Template = (ControlTemplate)FindResource("PanelTemplate"), Content = body, Height = 188, Cursor = Cursors.Hand, Focusable = false,
            Foreground = (Brush)FindResource("Text"), Margin = new Thickness(0, 4, single ? 0 : 18, 4),
        };
        if (single)
        {
            if (!continueSized) { continueSized = true; ContinueRow.SizeChanged += (_, _) => { if (singleCard != null) singleCard.Width = Math.Max(400, ContinueRow.ActualWidth - 2); }; }
            card.Width = Math.Max(400, ContinueRow.ActualWidth - 2);
        }
        else card.Width = 600;
        singleCard = single ? card : null;
        System.Windows.Automation.AutomationProperties.SetName(card, $"Continue watching {e.Title}");
        card.Click += (_, _) => { returnTo = Home; Play(e, index); }; // mpv's resume point takes it to where you left off
        card.MouseRightButtonUp += (_, ev) => { ShowCardMenu(e); ev.Handled = true; };
        return card;
    }

    // wide banner if we have one, else the cover; decoded once
    ImageSource? ArtFor(Entry e)
    {
        var art = InfoService.Cached(e.InfoKey)?.BannerFile;
        if (art == null || !File.Exists(art)) return e.Cover;
        if (continueArt.TryGetValue(art, out var hit)) return hit ?? e.Cover;
        ImageSource? img = null;
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit(); bi.UriSource = new Uri(art); bi.DecodePixelWidth = 1200; bi.CacheOption = BitmapCacheOption.OnLoad; bi.EndInit(); bi.Freeze();
            img = bi;
        }
        catch { }
        continueArt[art] = img;
        return img ?? e.Cover;
    }

    // mouse wheel scrolls the row sideways
    void OnContinueWheel(object s, MouseWheelEventArgs e)
    {
        ContinueRow.ScrollToHorizontalOffset(ContinueRow.HorizontalOffset - e.Delta);
        e.Handled = true;
    }
}
