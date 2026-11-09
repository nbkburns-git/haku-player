using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AnimePlayer;

// The show page (WinUI): banner, big cover and facts, title, other names, genres, description, then its seasons or
// episodes (watched ticks, favourites, progress), what's missing and Mark all watched. Same logic as the WPF app's
// (LibraryLogic.cs: Seasons, Missing, Ranges, WatchedSuffix, ResumeIndex).
public partial class MainWindow
{
    Entry? detailEntry;
    int? openSeason; // the season being shown, or null for the season list / a show without seasons
    readonly Border detailCover = new() { Width = 380, Height = 570, CornerRadius = new CornerRadius(26) };
    readonly TextBlock detailCoverName = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24), FontSize = 34, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    readonly StackPanel facts = new() { Margin = new Thickness(6, 26, 6, 0), Spacing = 10 };
    readonly TextBlock detailTitle = new() { FontSize = 48, FontWeight = Microsoft.UI.Text.FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock detailAlt = new() { FontSize = 17, Margin = new Thickness(2, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    readonly StackPanel detailGenres = new() { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 14, 0, 0) };
    readonly TextBlock detailDesc = new() { FontSize = 15.5, LineHeight = 24, TextWrapping = TextWrapping.Wrap, MaxLines = 5, TextTrimming = TextTrimming.WordEllipsis, MaxWidth = 1100, HorizontalAlignment = HorizontalAlignment.Left };
    readonly TextBlock detailCount = new() { FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center };
    readonly StackPanel episodeList = new();
    readonly ScrollViewer episodeScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 8, 0) };
    Button seasonBack = null!, missingBtn = null!, markAllBtn = null!, descMore = null!;
    readonly ContentPresenter starSlot = new();

    void BuildDetail()
    {
        // the banner across the top, faded out by half way (Popups.WinUI.cs), and the dark wash under the title
        SetupBanner();
        detail.Children.Add(bannerHost);
        detail.Children.Add(bannerShade);
        var grid = new Grid { Margin = new Thickness(56, 6, 48, 36) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        // left: big cover (shrinks on small windows) + facts
        detailCoverName.Foreground = Muted;
        var coverGrid = new Grid { Width = 380, Height = 570 };
        coverGrid.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(26), Child = detailCoverName,
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 1),
                GradientStops = { new GradientStop { Color = Color.FromArgb(0xFF, 0x40, 0x46, 0x5A), Offset = 0 }, new GradientStop { Color = Color.FromArgb(0xFF, 0x1A, 0x1C, 0x26), Offset = 1 } },
            },
        });
        detailCover.BorderThickness = new Thickness(1);
        detailCover.BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF));
        coverGrid.Children.Add(detailCover);
        var left = new StackPanel { Width = 380 };
        left.Children.Add(coverGrid); left.Children.Add(facts);
        grid.Children.Add(new Viewbox { Child = left, Width = 380, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, VerticalAlignment = VerticalAlignment.Top });

        // right: title (+ star), other names, genres, description, the list
        var right = new Grid { Margin = new Thickness(46, 10, 0, 0) };
        for (int i = 0; i < 5; i++) right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition());
        // title (right-click: the show's menu), then "Wrong show info?" and the star at the right
        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition()); titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        detailTitle.Foreground = TextBrush;
        ToolTipService.SetToolTip(detailTitle, "Right-click to rename, mark watched and more");
        titleRow.Children.Add(detailTitle);
        var fixRow = new StackPanel { Orientation = Orientation.Horizontal, Opacity = 0.7 };
        var fixIcon = GlassLook.Icon("\uE8AB", 13); fixIcon.Margin = new Thickness(0, 0, 6, 0); fixIcon.Foreground = TextBrush;
        fixRow.Children.Add(fixIcon);
        fixRow.Children.Add(new TextBlock { Text = "Wrong show info?", FontSize = 13.5, Foreground = TextBrush, VerticalAlignment = VerticalAlignment.Center });
        var fixBtn = GlassLook.Bare(fixRow, "Wrong show info", OpenFix);
        ToolTipService.SetToolTip(fixBtn, "Details are for a different show? Pick the right one");
        fixBtn.Margin = new Thickness(18, 8, 0, 0);
        Grid.SetColumn(fixBtn, 1); titleRow.Children.Add(fixBtn);
        starSlot.Margin = new Thickness(18, 6, 0, 0); starSlot.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(starSlot, 2); titleRow.Children.Add(starSlot);
        right.Children.Add(titleRow);
        detailAlt.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xC8, 0xCC, 0xD6));
        Grid.SetRow(detailAlt, 1); right.Children.Add(detailAlt);
        Grid.SetRow(detailGenres, 2); right.Children.Add(detailGenres);
        var desc = new StackPanel { Margin = new Thickness(2, 16, 0, 0) };
        detailDesc.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xD8, 0xDC, 0xE4));
        desc.Children.Add(detailDesc);
        var moreText = GlassLook.Label("More", 14, bold: true); moreText.Foreground = ThemeW.Accent;
        descMore = GlassLook.Flat(moreText, "Description more", () => { bool open = detailDesc.MaxLines != 0; detailDesc.MaxLines = open ? 0 : 5; moreText.Text = open ? "Less" : "More"; });
        descMore.HorizontalAlignment = HorizontalAlignment.Left; descMore.Padding = new Thickness(0, 6, 0, 0);
        desc.Children.Add(descMore);
        Grid.SetRow(desc, 3); right.Children.Add(desc);

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Thickness(4, 22, 0, 10) };
        var sbText = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var sbIcon = GlassLook.Icon("", 12); sbIcon.Foreground = ThemeW.Accent;
        var sbLabel = GlassLook.Label("All seasons", 13, bold: true); sbLabel.Foreground = ThemeW.Accent;
        sbText.Children.Add(sbIcon); sbText.Children.Add(sbLabel);
        seasonBack = GlassLook.Flat(sbText, "All seasons", () => { if (detailEntry != null) ShowEpisodes(detailEntry, null); });
        bar.Children.Add(seasonBack);
        bar.Children.Add(detailCount);
        missingBtn = GlassLook.Flat(IconText("", "What's missing"), "What's missing", () => { });
        missingBtn.Flyout = MissingFlyout();
        ToolTipService.SetToolTip(missingBtn, "See which seasons or episodes you don't have");
        bar.Children.Add(missingBtn);
        markAllBtn = GlassLook.Flat(IconText("", "Mark all watched"), "Mark all watched", MarkAll);
        bar.Children.Add(markAllBtn);
        Grid.SetRow(bar, 4); right.Children.Add(bar);

        episodeScroll.Content = episodeList;
        SmoothScroll.Attach(episodeScroll);
        var listPanel = new Border
        {
            CornerRadius = new CornerRadius(26), Padding = new Thickness(20, 18, 10, 18), Child = episodeScroll, BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
        };
        listPanel.VerticalAlignment = VerticalAlignment.Top; // as tall as its rows, scrolling once it fills the page
        Grid.SetRow(listPanel, 5); right.Children.Add(listPanel);
        Grid.SetColumn(right, 1); grid.Children.Add(right);
        detail.Children.Add(grid);
    }

    static StackPanel IconText(string glyph, string text)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Opacity = 0.75 };
        p.Children.Add(GlassLook.Icon(glyph, 12));
        p.Children.Add(GlassLook.Label(text, 12.5, bold: true));
        return p;
    }

    void OpenShow(Entry entry, bool keepSeason = false)
    {
        var sw = MpvCore.Trace != null ? System.Diagnostics.Stopwatch.StartNew() : null;
        bool same = detailEntry?.Folder == entry.Folder;
        detailEntry = entry;
        detailTitle.Text = entry.Title;
        detailCoverName.Text = entry.Title;
        detailCover.Background = Picture(entry.Cover, 760) is { } pic ? new ImageBrush { ImageSource = pic, Stretch = Stretch.UniformToFill } : null;
        starSlot.Content = DetailStar(entry);
        detailTitle.ContextFlyout = CardMenu(entry);
        headerTitle.Text = libraryTitle.Text; // as the WPF app: the header keeps the library's name
        ShowEpisodes(entry, keepSeason && same ? openSeason : null);
        if (view != detail) Show(detail);
        _ = LoadDetailInfo(entry);
        if (sw != null) DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => MpvCore.Note($"show page: {sw.ElapsedMilliseconds} ms for '{entry.Title}' ({entry.Videos.Count} videos)"));
    }

    async Task LoadDetailInfo(Entry entry)
    {
        detailAlt.Text = ""; detailAlt.Visibility = Visibility.Collapsed; detailDesc.Text = ""; detailGenres.Children.Clear(); facts.Children.Clear();
        descMore.Visibility = Visibility.Collapsed; detailDesc.MaxLines = 5;
        ShowBanner(null);
        var info = await InfoService.Get(entry);
        if (detailEntry != entry || info == null) return;
        static string N(string? t) => NotLetterOrDigitRx().Replace((t ?? "").ToLowerInvariant(), "");
        detailAlt.Text = string.Join("  ·  ", new[] { info.Title, info.Native }.Where(x => !string.IsNullOrEmpty(x) && N(x) != N(entry.Name)).Distinct());
        detailAlt.Visibility = detailAlt.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var g in info.Genres.Take(6))
            detailGenres.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 4, 12, 5), BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                Child = GlassLook.Label(g, 13),
            });
        detailDesc.Text = info.Description ?? "";
        descMore.Visibility = detailDesc.Text.Length > 300 ? Visibility.Visible : Visibility.Collapsed;
        void Fact(string label, string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var l = GlassLook.Label(label, 14); l.Foreground = Muted;
            var v = GlassLook.Label(value, 15, bold: true); v.TextAlignment = TextAlignment.Right;
            Grid.SetColumn(v, 1);
            row.Children.Add(l); row.Children.Add(v);
            facts.Children.Add(row);
        }
        Fact("Format", info.Format);
        Fact("Year", info.Year?.ToString());
        Fact("Episodes", info.Episodes?.ToString());
        Fact(info.Source == "cinemeta" ? "Director" : "Studio", info.Studio);
        Fact("Score", info.Score is double sc ? $"★ {sc:0.0}" : null);
        ShowBanner(info.BannerFile);
        if (detailEntry == entry) UpdateMissingButton(entry);
    }

    // ---------- seasons and episodes ----------

    // A show with several seasons opens on its seasons; one season (or none known) goes straight to the episodes.
    void ShowEpisodes(Entry entry, int? season)
    {
        var seasons = Seasons(entry);
        bool picker = seasons.Count(g => g.Key >= 0) > 1;
        if (!picker) season = null;
        openSeason = season;
        episodeList.Children.Clear();
        detailCount.Text = entry.Count;
        UpdateMissingButton(entry);
        List<Video> shown;
        if (picker && season == null)
        {
            seasonBack.Visibility = Visibility.Collapsed;
            int real = seasons.Count(g => g.Key >= 0);
            detailCount.Text = $"{real} season{(real == 1 ? "" : "s")} · {entry.Videos.Count(v => v.Season >= 0)} episodes{WatchedSuffix(entry.Videos.Where(v => v.Season >= 0))}";
            foreach (var g in seasons) episodeList.Children.Add(SeasonRow(entry, g));
            shown = entry.Videos.Where(v => v.Season >= 0).ToList();
        }
        else
        {
            seasonBack.Visibility = picker ? Visibility.Visible : Visibility.Collapsed;
            shown = picker ? entry.Videos.Where(v => v.Season == season).ToList() : entry.Videos;
            if (picker) detailCount.Text = $"{SeasonName(season!.Value)} · {shown.Count} {(season < 0 ? "video" : "episode")}{(shown.Count == 1 ? "" : "s")}";
            if (entry.Unit == "episodes") detailCount.Text += WatchedSuffix(shown);
            foreach (var v in shown) episodeList.Children.Add(EpisodeRow(entry, v));
        }
        markShown = shown;
        bool canMark = entry.Unit == "episodes" || entry.Kind == "movie";
        markAllBtn.Visibility = canMark && shown.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        bool all = shown.Count > 0 && shown.All(v => Watch.IsDone(v.Path));
        ((TextBlock)((StackPanel)markAllBtn.Content).Children[1]).Text = all ? "Mark as unwatched" : picker && season != null ? "Mark season watched" : "Mark all watched";
        episodeScroll.ChangeView(null, 0, null, true);
    }

    List<Video> markShown = new();
    void MarkAll()
    {
        if (markShown.Count == 0) return;
        Watch.SetDone(markShown.Select(v => v.Path), !markShown.All(v => Watch.IsDone(v.Path)));
    }

    Button SeasonRow(Entry entry, IGrouping<int, Video> g)
    {
        int n = g.Count();
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var num = GlassLook.Label(g.Key > 0 ? g.Key.ToString("00") : "•", 16, bold: true); num.Foreground = ThemeW.Accent;
        var name = GlassLook.Label(SeasonName(g.Key), 17);
        var count = GlassLook.Label($"{n} {(g.Key < 0 ? "video" : "episode")}{(n == 1 ? "" : "s")}{WatchedSuffix(g)}", 14); count.Foreground = Muted; count.Margin = new Thickness(0, 0, 16, 0);
        var chevron = GlassLook.Icon("", 14); chevron.Foreground = Muted;
        Grid.SetColumn(name, 1); Grid.SetColumn(count, 2); Grid.SetColumn(chevron, 3);
        row.Children.Add(num); row.Children.Add(name); row.Children.Add(count); row.Children.Add(chevron);
        var b = RowButton(row, SeasonName(g.Key));
        b.Click += (_, _) => ShowEpisodes(entry, g.Key);
        return b;
    }

    Button EpisodeRow(Entry entry, Video v)
    {
        bool done = Watch.IsDone(v.Path);
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var num = GlassLook.Label(v.Num, 16, bold: true); num.Foreground = ThemeW.Accent; num.Opacity = done ? 0.55 : 1;
        var title = GlassLook.Label(v.Title, 17); title.TextTrimming = TextTrimming.CharacterEllipsis; title.Opacity = done ? 0.55 : 1;
        // watched tick: accent when watched, faint when not; click to toggle
        var tickIcon = GlassLook.Icon("", 16);
        tickIcon.Foreground = done ? ThemeW.Accent : new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF));
        var tick = GlassLook.Flat(tickIcon, "Watched row", () => Watch.SetDone(new[] { v.Path }, !Watch.IsDone(v.Path)), 34);
        ToolTipService.SetToolTip(tick, done ? "Watched · click to mark as unwatched" : "Mark as watched");
        var star = StarButton(v, 17); star.Width = 34; star.Height = 30; star.Margin = new Thickness(0, 0, 10, 0);
        AutomationProperties.SetName(star, "Star row");
        var play = GlassLook.Icon("", 14); play.Foreground = Muted;
        Grid.SetColumn(title, 1); Grid.SetColumn(tick, 2); Grid.SetColumn(star, 3); Grid.SetColumn(play, 4);
        row.Children.Add(num); row.Children.Add(title); row.Children.Add(tick); row.Children.Add(star); row.Children.Add(play);
        var outer = new Grid();
        outer.Children.Add(row);
        // how far into this episode you are
        if (Watch.Progress(v.Path) is var p && p > 0)
        {
            var bar = new Grid { Height = 3, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(44, 0, 0, -9) };
            bar.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)) });
            var fill = new Border { CornerRadius = new CornerRadius(2), Background = GlassLook.Accent(), HorizontalAlignment = HorizontalAlignment.Left };
            bar.SizeChanged += (_, _) => fill.Width = bar.ActualWidth * p;
            bar.Children.Add(fill);
            outer.Children.Add(bar);
        }
        var b = RowButton(outer, v.Title);
        b.Click += (_, _) => { returnTo = detail; PlayEntry(entry, entry.Videos.IndexOf(v)); };
        return b;
    }

    // a list row (WPF "Row"): faint panel, hover lights an accent bar and nudges the content
    static Button RowButton(UIElement content, string name)
    {
        var b = GlassLook.Row(content, name, () => { }, new Thickness(18, 14, 18, 14));
        b.Margin = new Thickness(0, 4, 0, 4);
        return b;
    }

    // the show page's star (WPF "DetailStar"): gold, filled when a favourite
    Button DetailStar(Entry entry)
    {
        var icon = new FontIcon { Glyph = IsFav(entry) ? "\uE735" : "\uE734", FontFamily = Icons, FontSize = 30, Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xD5, 0x4F)) };
        var b = GlassLook.Bare(icon, "Favourite show", null);
        ToolTipService.SetToolTip(b, "Favourite");
        GlassLook.HoverScale(icon, 1.15f);
        b.Click += (_, _) => { if (ToggleFavData(entry)) icon.Glyph = IsFav(entry) ? "\uE735" : "\uE734"; };
        return b;
    }

    // ---------- what's missing ----------

    void UpdateMissingButton(Entry entry) =>
        missingBtn.Visibility = entry.Kind != "movie" && entry.Unit == "episodes" && InfoService.Cached(entry.InfoKey)?.EpisodeNames is { Count: > 0 }
            ? Visibility.Visible : Visibility.Collapsed;

    Flyout MissingFlyout()
    {
        var items = new StackPanel { Spacing = 10, MaxWidth = 460 };
        var f = GlassLook.GlassFlyout(items, new Thickness(20, 16, 20, 16));
        f.Placement = FlyoutPlacementMode.Bottom;
        f.Opening += (_, _) =>
        {
            items.Children.Clear();
            if (detailEntry is not { } entry || InfoService.Cached(entry.InfoKey)?.EpisodeNames is not { Count: > 0 } names) return;
            var missing = Missing(entry, names);
            int count = missing.Sum(m => m.Eps.Count);
            TextBlock Line(string text, double size = 14, bool bold = false) { var t = GlassLook.Label(text, size, bold); t.TextWrapping = TextWrapping.Wrap; return t; }
            if (count == 0)
            {
                items.Children.Add(Line("You have every episode", 16, true));
                var l = Line("Nothing missing from the aired seasons (specials not counted).", 13); l.Foreground = Muted; items.Children.Add(l);
            }
            else
            {
                items.Children.Add(Line($"Missing {count} episode{(count == 1 ? "" : "s")}", 16, true));
                foreach (var (season, eps, total) in missing)
                {
                    var row = new Grid();
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) }); row.ColumnDefinitions.Add(new ColumnDefinition());
                    string what = eps.Count == total ? $"All {total} episodes"
                        : eps.Count <= 3 ? string.Join("\n", eps.Select(ep => $"E{ep} · {names.GetValueOrDefault($"{season}:{ep}")}"))
                        : $"{Ranges(eps)}  ({eps.Count} of {total})";
                    var w = Line(what); Grid.SetColumn(w, 1);
                    row.Children.Add(Line($"Season {season}", 14, true)); row.Children.Add(w);
                    items.Children.Add(row);
                }
            }
            var note = Line("Compared with the show's official episode list. Episodes that haven't aired yet and specials aren't counted.", 12); note.Foreground = Muted;
            items.Children.Add(note);
        };
        return f;
    }
}
