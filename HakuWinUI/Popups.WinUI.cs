using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AnimePlayer;

// The WPF app's glass pop-ups, for WinUI: the right-click menu of a show (CardMenu.cs), the rename box, "Find the right
// show" (FixMatch.cs), and the show page banner's fade (WPF did it with an opacity mask; here composition masks it).
static partial class GlassLook
{
    // a flyout that is only our glass panel (no WinUI chrome, no shadow)
    public static Flyout GlassFlyout(UIElement content, Thickness padding, double minWidth = 0)
    {
        var style = new Style(typeof(FlyoutPresenter));
        style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Colors.Transparent)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(FlyoutPresenter.IsDefaultShadowEnabledProperty, false));
        style.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 2000.0));
        style.Setters.Add(new Setter(FrameworkElement.MaxHeightProperty, 2000.0));
        var panel = Panel(content, 14, padding);
        if (minWidth > 0) panel.MinWidth = minWidth;
        return new Flyout { Content = panel, FlyoutPresenterStyle = style, ShouldConstrainToRootBounds = true };
    }
}

public partial class MainWindow
{
    // ---------- right-click a show: play, open, rename, favourite, mark watched, fix its details, its folder ----------

    FlyoutBase CardMenu(Entry entry)
    {
        var items = new StackPanel { MinWidth = 250 };
        var f = GlassLook.GlassFlyout(items, new Thickness(6));
        f.Opening += (_, _) =>
        {
            items.Children.Clear();
            if (entry.IsRandom) { f.Hide(); return; }
            void Item(string icon, string text, Action act, string? hint = null)
            {
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new FontIcon { Glyph = icon, FontFamily = Icons, FontSize = 14, Opacity = 0.85, Foreground = TextBrush, HorizontalAlignment = HorizontalAlignment.Left });
                var t = new TextBlock { Text = text, FontSize = 14, Foreground = TextBrush, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(t, 1); row.Children.Add(t);
                if (hint != null) { var h = new TextBlock { Text = hint, FontSize = 12.5, Opacity = 0.55, Foreground = TextBrush, Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(h, 2); row.Children.Add(h); }
                var b = GlassLook.Glass(row, text, () => { f.Hide(); act(); }, new Thickness(12, 8, 12, 8));
                ((Border)b.Content).HorizontalAlignment = HorizontalAlignment.Stretch;
                b.HorizontalAlignment = HorizontalAlignment.Stretch; b.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                b.Margin = new Thickness(0, 1, 0, 1);
                items.Children.Add(b);
            }
            void Line() => items.Children.Add(new Border { Height = 1, Margin = new Thickness(10, 5, 10, 5), Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)) });

            int at = ResumeIndex(entry);
            bool mid = Watch.Get(entry.Videos.ElementAtOrDefault(at)?.Path ?? "") is { Done: false, Pos: > 5 };
            string what = entry.Kind == "movie" || entry.Videos.Count <= at ? "" : entry.Videos[at].Label;
            Item("\uE768", mid ? "Resume" : "Play", () => { returnTo = view ?? home; PlayEntry(entry, at); }, what.Length > 18 ? what[..17] + "…" : what);
            if (view != detail || detailEntry?.Folder != entry.Folder) Item("\uE8A7", "Open", () => OpenShow(entry));
            Line();
            Item("\uE8AC", "Rename…", () => DispatcherQueue.TryEnqueue(() => ShowRename(entry)), "in the app only");
            if (entry.Title != entry.Name) Item("\uE7A7", "Use the folder's name", () => SetRename(entry, null));
            Item(IsFav(entry) ? "\uE735" : "\uE734", IsFav(entry) ? "Remove from favourites" : "Add to favourites", () => { ToggleFavData(entry); RefreshWatchViews(); });
            if (entry.Unit != "themes")
            {
                var eps = entry.Videos.Where(v => v.Season >= 0).ToList();
                bool all = eps.Count > 0 && eps.All(v => Watch.IsDone(v.Path));
                if (eps.Count > 0) Item(all ? "\uE7A7" : "\uE73E", all ? "Mark as unwatched" : "Mark all watched", () => Watch.SetDone(eps.Select(v => v.Path), !all));
                Item("\uE8AB", "Wrong show info?", () => { OpenShow(entry); OpenFix(); });
            }
            Line();
            Item("\uE838", "Show in File Explorer", () => { try { Process.Start("explorer.exe", $"/select,\"{entry.Folder.TrimEnd('\\')}\""); } catch { } });
        };
        return f;
    }

    // a small glass box: type a name, Enter saves, Esc cancels, empty goes back to the folder's name
    void ShowRename(Entry entry)
    {
        var box = new TextBox { Text = entry.Title, FontSize = 16, MinWidth = 320, Padding = new Thickness(10, 7, 10, 7), Foreground = TextBrush,
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1) };
        AutomationProperties.SetName(box, "New name");
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Rename", FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = TextBrush, Margin = new Thickness(2, 0, 0, 8) });
        panel.Children.Add(box);
        panel.Children.Add(new TextBlock { Text = $"Only in Haku Player: the folder stays \"{System.IO.Path.GetFileName(entry.Folder.TrimEnd('\\'))}\". Enter to save, Esc to cancel, empty for the folder's name.",
            FontSize = 12, Opacity = 0.6, Foreground = TextBrush, TextWrapping = TextWrapping.Wrap, MaxWidth = 340, Margin = new Thickness(2, 8, 0, 0) });
        var f = GlassLook.GlassFlyout(panel, new Thickness(16, 14, 16, 14));
        box.KeyDown += (_, k) =>
        {
            if (k.Key == Windows.System.VirtualKey.Enter) { f.Hide(); SetRename(entry, box.Text); k.Handled = true; }
            else if (k.Key == Windows.System.VirtualKey.Escape) { f.Hide(); k.Handled = true; }
        };
        f.Opened += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
        f.ShowAt(root, new FlyoutShowOptions { Position = lastPointer, ShowMode = FlyoutShowMode.Standard });
    }

    Windows.Foundation.Point lastPointer;

    void SetRename(Entry entry, string? name)
    {
        name = name?.Trim() ?? "";
        var s = Settings.Current;
        if (name.Length == 0 || name == entry.Name) s.Renames.Remove(entry.Folder); else s.Renames[entry.Folder] = name;
        s.Save();
        RefreshWatchViews();
        if (view == detail && detailEntry?.Folder == entry.Folder) OpenShow(detailEntry, keepSeason: true);
        Toast(name.Length == 0 || name == entry.Name ? $"Back to the folder's name · {entry.Name}" : $"Renamed to {name} (the folder is unchanged)");
    }

    // ---------- "Wrong show info?": search AniList (anime) or Cinemeta (TV, movies) and pick the right entry. The pick is
    // remembered per folder (Settings.InfoOverrides / MediaOverrides) and always wins over automatic matching. ----------

    readonly Grid fixLayer = new() { Visibility = Visibility.Collapsed, Background = new AcrylicBrush { TintColor = Color.FromArgb(0xFF, 0x05, 0x06, 0x08), TintOpacity = 0.2, TintLuminosityOpacity = 0.0, FallbackColor = Color.FromArgb(0xCC, 0x0A, 0x0B, 0x10) } };
    readonly TextBox fixSearch = new() { FontSize = 15, Padding = new Thickness(42, 11, 16, 11), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock fixStatus = new() { FontSize = 13, Margin = new Thickness(4, 0, 0, 8), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
    readonly StackPanel fixResults = new();
    Entry? fixing;
    int fixSeq;

    void SetupFix()
    {
        var body = new Grid();
        for (int i = 0; i < 4; i++) body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition());
        var top = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        top.ColumnDefinitions.Add(new ColumnDefinition()); top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(new TextBlock { Text = "Find the right show", FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = TextBrush, VerticalAlignment = VerticalAlignment.Center });
        var close = GlassLook.Caption("\uE8BB", "Close fix match", "Close (Esc)", CloseFix, close: true);
        Grid.SetColumn(close, 1); top.Children.Add(close);
        body.Children.Add(top);
        var about = new TextBlock { Text = "Search and pick the correct entry (AniList for anime, IMDb via Cinemeta for TV and movies). Haku Player remembers your choice for this folder.",
            FontSize = 13, Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 8, 14) };
        Grid.SetRow(about, 1); body.Children.Add(about);
        var search = new Grid { Height = 44, Margin = new Thickness(0, 0, 8, 12) };
        search.Children.Add(new Border { CornerRadius = new CornerRadius(22), Background = GlassLook.GlassFill(), BorderThickness = new Thickness(1), BorderBrush = GlassLook.GlassStroke() });
        var lens = GlassLook.Icon("\uE721", 15); lens.Foreground = Muted; lens.HorizontalAlignment = HorizontalAlignment.Left; lens.Margin = new Thickness(17, 0, 0, 0);
        search.Children.Add(lens);
        foreach (var k in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused" })
            fixSearch.Resources[k] = new SolidColorBrush(Colors.Transparent);
        foreach (var k in new[] { "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused" }) fixSearch.Resources[k] = TextBrush;
        AutomationProperties.SetName(fixSearch, "Fix match search");
        fixSearch.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { _ = RunFixSearch(); e.Handled = true; } else if (e.Key == Windows.System.VirtualKey.Escape) { CloseFix(); e.Handled = true; } };
        search.Children.Add(fixSearch);
        Grid.SetRow(search, 2); body.Children.Add(search);
        fixStatus.Foreground = Muted;
        Grid.SetRow(fixStatus, 3); body.Children.Add(fixStatus);
        var scroll = new ScrollViewer { Content = fixResults, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 8, 0) };
        Grid.SetRow(scroll, 4); body.Children.Add(scroll);
        var card = GlassLook.Panel(body, 14, new Thickness(26, 22, 18, 22));
        card.Width = 640; card.MaxHeight = 660; card.Margin = new Thickness(40, 70, 40, 40);
        fixLayer.Children.Add(card);
        fixLayer.PointerPressed += (_, e) => { if (e.OriginalSource == fixLayer) CloseFix(); };
        fixLayer.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(160) };
        root.Children.Add(fixLayer);
        root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) => lastPointer = e.GetCurrentPoint(root).Position), true);
    }

    void OpenFix()
    {
        if (detailEntry is not { } entry) return;
        fixing = entry;
        fixSearch.Text = entry.Name;
        fixLayer.Opacity = 0;
        fixLayer.Visibility = Visibility.Visible;
        DispatcherQueue.TryEnqueue(() => { fixLayer.Opacity = 1; fixSearch.Focus(FocusState.Programmatic); fixSearch.SelectAll(); });
        _ = RunFixSearch();
    }

    void CloseFix()
    {
        fixing = null;
        fixSeq++;
        fixLayer.Visibility = Visibility.Collapsed;
        fixResults.Children.Clear();
    }

    bool FixOpen => fixLayer.Visibility == Visibility.Visible;

    async Task RunFixSearch()
    {
        var text = fixSearch.Text.Trim();
        if (text.Length == 0 || fixing is not Entry entry) return;
        int my = ++fixSeq;
        FixStatusText("Searching AniList…");
        System.Collections.Generic.List<ShowChoice> results;
        try { results = await InfoService.Choices(text, entry.Kind); }
        catch { results = new(); }
        if (my != fixSeq) return; // a newer search started
        fixResults.Children.Clear();
        if (results.Count == 0) { FixStatusText("Nothing found. Try the Japanese (romaji) name, or fewer words."); return; }
        fixStatus.Visibility = Visibility.Collapsed;
        var now = InfoService.Cached(entry.InfoKey);
        string? current = now?.AniListId?.ToString() ?? now?.ImdbId;
        foreach (var r in results) fixResults.Children.Add(ChoiceRow(r, r.Id == current));
    }

    void FixStatusText(string text) { fixStatus.Text = text; fixStatus.Visibility = Visibility.Visible; }

    Button ChoiceRow(ShowChoice r, bool isCurrent)
    {
        var cover = new Border { Width = 46, Height = 66, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 16, 0), Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)) };
        if (r.Cover != null) try { cover.Background = new ImageBrush { ImageSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(r.Cover)), Stretch = Stretch.UniformToFill }; } catch { }
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = r.Title, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = TextBrush, TextTrimming = TextTrimming.CharacterEllipsis });
        if (r.Other != null) text.Children.Add(new TextBlock { Text = r.Other, FontSize = 13, Opacity = 0.7, Foreground = TextBrush, TextTrimming = TextTrimming.CharacterEllipsis });
        var facts = string.Join(" · ", new[] { r.Format, r.Year?.ToString(), r.Episodes is int n ? $"{n} episode{(n == 1 ? "" : "s")}" : null }.Where(x => x != null));
        text.Children.Add(new TextBlock { Text = facts, FontSize = 12.5, Foreground = Muted, Margin = new Thickness(0, 3, 0, 0) });
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(cover);
        Grid.SetColumn(text, 1); row.Children.Add(text);
        if (isCurrent)
        {
            var tag = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 3, 10, 3), VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), Child = new TextBlock { Text = "Current", FontSize = 12, Foreground = TextBrush } };
            Grid.SetColumn(tag, 2); row.Children.Add(tag);
        }
        var b = GlassLook.Row(row, $"Pick {r.Title} ({r.Year})", () => _ = Pick(r), new Thickness(12, 8, 12, 8));
        b.Margin = new Thickness(0, 2, 0, 2);
        return b;
    }

    async Task Pick(ShowChoice r)
    {
        if (fixing is not Entry entry) return;
        if (entry.Kind == "anime") Settings.Current.InfoOverrides[entry.Folder] = int.Parse(r.Id);
        else Settings.Current.MediaOverrides[entry.Folder] = r.Id;
        Settings.Current.Save();
        fixResults.Children.Clear();
        FixStatusText($"Getting details for {r.Title}…");
        var info = await InfoService.Refresh(entry);
        CloseFix();
        if (detailEntry == entry) await LoadDetailInfo(entry);
        bool got = (info?.AniListId?.ToString() ?? info?.ImdbId) == r.Id;
        if (got && entry.Kind != "anime" && info?.CoverUrl != null && await SaveCover(entry, info.CoverUrl, replace: true)) await SyncLibrary(false); // new poster too
        Toast(got ? $"Details updated · {r.Title}" : "Couldn't reach the details service; your pick is saved and will be used next time");
    }

    // ---------- the show page's banner: faded out by half way down, as the WPF app's opacity mask did ----------

    readonly Grid bannerHost = new() { IsHitTestVisible = false, Margin = new Thickness(0, -52, 0, 0) };
    readonly Grid bannerShade = new() { IsHitTestVisible = false, Margin = new Thickness(0, -52, 0, 0) };
    SpriteVisual? bannerSprite;
    int bannerSeq;

    static CompositionLinearGradientBrush Fade(Compositor c, Vector2 end, params (byte A, float At)[] stops)
    {
        var g = c.CreateLinearGradientBrush();
        g.StartPoint = Vector2.Zero; g.EndPoint = end;
        foreach (var (a, at) in stops) g.ColorStops.Add(c.CreateColorGradientStop(at, Color.FromArgb(a, 0, 0, 0)));
        return g;
    }

    void SetupBanner()
    {
        // the dark wash from the top-left (under the title), fading out down the page; always there
        var c = ElementCompositionPreview.GetElementVisual(bannerShade).Compositor;
        var wash = c.CreateLinearGradientBrush();
        wash.StartPoint = Vector2.Zero; wash.EndPoint = new Vector2(1, 0.6f);
        wash.ColorStops.Add(c.CreateColorGradientStop(0, Color.FromArgb(0xC0, 0x0A, 0x0B, 0x10)));
        wash.ColorStops.Add(c.CreateColorGradientStop(0.5f, Color.FromArgb(0x70, 0x0A, 0x0B, 0x10)));
        wash.ColorStops.Add(c.CreateColorGradientStop(1, Color.FromArgb(0x30, 0x0A, 0x0B, 0x10)));
        var mask = c.CreateMaskBrush();
        mask.Source = wash;
        mask.Mask = Fade(c, new Vector2(0, 1), (0xFF, 0.3f), (0x00, 0.55f));
        var shade = c.CreateSpriteVisual();
        shade.Brush = mask;
        ElementCompositionPreview.SetElementChildVisual(bannerShade, shade);
        bannerShade.SizeChanged += (_, _) => shade.Size = new Vector2((float)bannerShade.ActualWidth, (float)bannerShade.ActualHeight);
        bannerHost.SizeChanged += (_, _) => { if (bannerSprite != null) bannerSprite.Size = new Vector2((float)bannerHost.ActualWidth, (float)bannerHost.ActualHeight); };
    }

    void ShowBanner(string? file)
    {
        int my = ++bannerSeq;
        var c = ElementCompositionPreview.GetElementVisual(bannerHost).Compositor;
        if (bannerSprite != null) { ElementCompositionPreview.SetElementChildVisual(bannerHost, null); bannerSprite = null; }
        if (file == null || !System.IO.File.Exists(file)) return;
        var surface = LoadedImageSurface.StartLoadFromUri(new Uri(file));
        surface.LoadCompleted += (_, e) =>
        {
            if (my != bannerSeq || e.Status != LoadedImageSourceLoadStatus.Success) return;
            var img = c.CreateSurfaceBrush(surface);
            img.Stretch = CompositionStretch.UniformToFill;
            img.HorizontalAlignmentRatio = 0.5f; img.VerticalAlignmentRatio = 0.5f;
            var mask = c.CreateMaskBrush();
            mask.Source = img;
            mask.Mask = Fade(c, new Vector2(0, 1), (0xC0, 0), (0x78, 0.2f), (0x30, 0.34f), (0x0C, 0.44f), (0x00, 0.52f));
            var sprite = c.CreateSpriteVisual();
            sprite.Brush = mask;
            sprite.Size = new Vector2((float)bannerHost.ActualWidth, (float)bannerHost.ActualHeight);
            sprite.Opacity = 0;
            var fade = c.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(1, 0.55f);
            fade.Duration = TimeSpan.FromMilliseconds(500);
            ElementCompositionPreview.SetElementChildVisual(bannerHost, sprite);
            bannerSprite = sprite;
            sprite.StartAnimation("Opacity", fade);
        };
    }
}
