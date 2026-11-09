using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.UI;

namespace AnimePlayer;

// Home, as the WPF app's (MainWindow.xaml "Home", HomeTiles.cs, ContinueRow.cs): the wordmark and + Library, one glass
// tile per library with a drifting wall of its covers, 4 to a page (arrows and dots when there are more), drag a tile to
// move it; under them "Continue watching".
public partial class MainWindow
{
    readonly StackPanel continueItems = new() { Orientation = Orientation.Horizontal };
    readonly ScrollViewer continueRow = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled, Margin = new Thickness(62, -18, 62, 40) };
    readonly Grid tiles = new() { Margin = new Thickness(40, 22, 40, 40) };
    readonly StackPanel pageDots = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 38) };
    Button pagePrev = null!, pageNext = null!;
    readonly Dictionary<string, Button> tileFor = new();
    int homePage;
    const int PerPage = 4;

    void BuildHome()
    {
        home.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        home.RowDefinitions.Add(new RowDefinition());
        home.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        // wordmark: "haku" Outfit SemiBold + "player" Outfit Light in the theme's light colour, with a soft glow
        var mark = new TextBlock { FontSize = 72, HorizontalAlignment = HorizontalAlignment.Center, FontFamily = Outfit, Foreground = TextBrush };
        mark.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "haku", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        mark.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "player", FontWeight = Microsoft.UI.Text.FontWeights.Light, Foreground = ThemeW.Light });
        AutomationProperties.SetName(mark, "Haku Player");
        home.Children.Add(GlassLook.WithGlow(mark, ThemeW.Light.Color, 40, 0.35f));
        // + Library: a new library tile
        var add = new StackPanel { Orientation = Orientation.Horizontal };
        var plus = GlassLook.Icon("", 12); plus.Margin = new Thickness(0, 0, 8, 0); plus.Foreground = TextBrush;
        add.Children.Add(plus);
        add.Children.Add(new TextBlock { Text = "Library", FontSize = 14, Foreground = TextBrush, VerticalAlignment = VerticalAlignment.Center });
        var addBtn = GlassLook.Glass(add, "Add library tile", AddTile, new Thickness(14, 7, 16, 7));
        ToolTipService.SetToolTip(addBtn, "Make a new library: its own tile here");
        addBtn.HorizontalAlignment = HorizontalAlignment.Right; addBtn.VerticalAlignment = VerticalAlignment.Bottom; addBtn.Margin = new Thickness(0, 0, 62, 0);
        home.Children.Add(addBtn);

        var middle = new Grid();
        middle.Children.Add(tiles);
        pagePrev = PageArrow("", "Previous libraries", () => GoPage(homePage - 1), HorizontalAlignment.Left);
        pageNext = PageArrow("", "Next libraries", () => GoPage(homePage + 1), HorizontalAlignment.Right);
        middle.Children.Add(pagePrev); middle.Children.Add(pageNext); middle.Children.Add(pageDots);
        Grid.SetRow(middle, 1); home.Children.Add(middle);
        continueRow.Content = continueItems;
        continueRow.PointerWheelChanged += (_, e) => { continueRow.ChangeView(continueRow.HorizontalOffset - e.GetCurrentPoint(continueRow).Properties.MouseWheelDelta, null, null); e.Handled = true; };
        continueRow.SizeChanged += (_, _) => { if (continueItems.Children.Count == 1 && continueItems.Children[0] is FrameworkElement one) one.Width = Math.Max(400, continueRow.ActualWidth - 2); };
        Grid.SetRow(continueRow, 2); home.Children.Add(continueRow);
        SetupTileDrag();
    }

    Button PageArrow(string glyph, string name, Action go, HorizontalAlignment side)
    {
        var icon = GlassLook.Icon(glyph, 15); icon.Foreground = TextBrush;
        var b = GlassLook.Glass(icon, name, go, new Thickness(0));
        ((Border)b.Content).Width = 44; ((Border)b.Content).Height = 44;
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        ToolTipService.SetToolTip(b, name == "Next libraries" ? "More libraries" : "Previous libraries");
        b.HorizontalAlignment = side; b.VerticalAlignment = VerticalAlignment.Center;
        b.Margin = side == HorizontalAlignment.Left ? new Thickness(10, 0, 0, 18) : new Thickness(0, 0, 10, 18);
        b.Visibility = Visibility.Collapsed;
        return b;
    }

    List<string> HomeOrder() => dragOrder ?? Libs.Where(l => l.Home).Select(l => l.Id).ToList();
    static int PageCount(List<string> order) => Math.Max(1, (order.Count + PerPage - 1) / PerPage);

    // fresh library data: new tiles for every library on home
    void BuildTiles()
    {
        if (dragId != null) { tilesStale = true; return; } // rebuilt when the drag ends
        tilesStale = false;
        tileFor.Clear();
        foreach (var l in Libs.Where(l => l.Home)) tileFor[l.Id] = MakeTile(l, lib.GetValueOrDefault(l.Id) ?? new());
        PlaceTiles(0);
    }

    // This page's tiles in the grid (2 x 2 once there's more than one page); slide: the direction of a page change
    void PlaceTiles(int slide)
    {
        var order = HomeOrder();
        int pages = PageCount(order);
        homePage = Math.Clamp(homePage, 0, pages - 1);
        var ids = order.Skip(homePage * PerPage).Take(PerPage).Where(tileFor.ContainsKey).ToList();
        int n = pages > 1 ? PerPage : ids.Count;
        int cols = n == 4 ? 2 : Math.Max(1, n), rows = n == 4 ? 2 : 1;
        tiles.Children.Clear(); tiles.RowDefinitions.Clear(); tiles.ColumnDefinitions.Clear();
        for (int i = 0; i < cols; i++) tiles.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < rows; i++) tiles.RowDefinitions.Add(new RowDefinition());
        for (int i = 0; i < ids.Count; i++)
        {
            var b = tileFor[ids[i]];
            b.Margin = n == 4 ? new Thickness(22, 10, 22, 10) : new Thickness(22, 0, 22, 0);
            if (ids[i] != dragId) { b.Translation = Vector3.Zero; b.Scale = Vector3.One; b.Rotation = 0; }
            Grid.SetColumn(b, i % cols); Grid.SetRow(b, i / cols);
            Canvas.SetZIndex(b, ids[i] == dragId ? 10 : 0);
            tiles.Children.Add(b);
        }
        if (ids.Count == 0)
        {
            var none = GlassLook.Label("Nothing yet · add your folders in Settings", 18); none.Opacity = 0.7; none.HorizontalAlignment = HorizontalAlignment.Center;
            tiles.Children.Add(none);
        }
        pagePrev.Visibility = pageNext.Visibility = pages > 1 ? Visibility.Visible : Visibility.Collapsed;
        pagePrev.IsEnabled = homePage > 0; pagePrev.Opacity = homePage > 0 ? 1 : 0.35;
        pageNext.IsEnabled = homePage < pages - 1; pageNext.Opacity = homePage < pages - 1 ? 1 : 0.35;
        pageDots.Children.Clear();
        for (int i = 0; pages > 1 && i < pages; i++)
        {
            int page = i;
            var dot = GlassLook.Bare(new Border { Width = i == homePage ? 22 : 8, Height = 8, CornerRadius = new CornerRadius(4),
                Background = i == homePage ? ThemeW.Grad : new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)) }, $"Libraries page {i + 1}", () => GoPage(page));
            dot.Margin = new Thickness(4, 0, 4, 0);
            pageDots.Children.Add(dot);
        }
        if (slide != 0)
        {
            tiles.OpacityTransition = null; tiles.TranslationTransition = null;
            tiles.Opacity = 0; tiles.Translation = new Vector3(slide * 70, 0, 0);
            DispatcherQueue.TryEnqueue(() =>
            {
                tiles.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(260) };
                tiles.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(340) };
                tiles.Opacity = 1; tiles.Translation = Vector3.Zero;
            });
        }
    }

    void GoPage(int page)
    {
        page = Math.Clamp(page, 0, PageCount(HomeOrder()) - 1);
        if (page == homePage) return;
        int dir = page > homePage ? 1 : -1;
        homePage = page;
        PlaceTiles(dir);
    }

    // + Library: a new, empty library (on the last page), opened in Settings to name it and add its folders.
    void AddTile()
    {
        Libs.Add(new MediaLibrary());
        Settings.Current.Save();
        lib[Libs[^1].Id] = new();
        homePage = int.MaxValue; // clamped to the last page
        BuildTiles();
        settingsPage = "Library";
        OpenSettings();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => settingsScroll.ChangeView(null, settingsScroll.ScrollableHeight, null));
    }

    // A library's tile: its covers drifting in tilted rows behind a scrim, its name and what's in it. Click: its page.
    Button MakeTile(MediaLibrary l, List<Entry> entries)
    {
        var wall = new Canvas { IsHitTestVisible = false };
        var body = new Grid { CornerRadius = new CornerRadius(28) };
        body.Children.Add(wall);
        body.Children.Add(new Border
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
                GradientStops = { new GradientStop { Color = Color.FromArgb(0x40, 0x0A, 0x0B, 0x10), Offset = 0 }, new GradientStop { Color = Color.FromArgb(0x60, 0x0A, 0x0B, 0x10), Offset = 0.45 }, new GradientStop { Color = Color.FromArgb(0xEE, 0x0A, 0x0B, 0x10), Offset = 1 } },
            },
        });
        var dock = new Grid { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(32, 0, 30, 28) };
        dock.ColumnDefinitions.Add(new ColumnDefinition()); dock.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = l.Name, FontSize = 42, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = TextBrush, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = TileCount(entries), FontSize = 16, Foreground = Muted, Margin = new Thickness(0, 2, 0, 0) });
        dock.Children.Add(text);
        var arrow = new FontIcon { Glyph = "", FontFamily = Icons, FontSize = 22, Foreground = TextBrush, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 8), Opacity = 0.85 };
        Grid.SetColumn(arrow, 1); dock.Children.Add(arrow);
        body.Children.Add(dock);
        var edge = new Border { CornerRadius = new CornerRadius(28), BorderThickness = new Thickness(1), BorderBrush = GlassLook.GlassStroke(), Opacity = 0.6, IsHitTestVisible = false,
            OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(250) } };
        body.Children.Add(edge);
        var b = GlassLook.Bare(body, l.Name, null);
        b.Tag = l.Id;
        b.HorizontalAlignment = HorizontalAlignment.Stretch; b.VerticalAlignment = VerticalAlignment.Stretch;
        b.HorizontalContentAlignment = HorizontalAlignment.Stretch; b.VerticalContentAlignment = VerticalAlignment.Stretch;
        b.Click += (_, _) => { if (tileDragged) { tileDragged = false; return; } OpenCategory(l.Id, l.Name); };
        b.PointerEntered += (_, _) => edge.Opacity = 1;
        b.PointerExited += (_, _) => edge.Opacity = 0.6;
        GlassLook.HoverScale(b, 1.015f, 350);
        HookTileDrag(b);
        wall.SizeChanged += (_, _) => { int rows = WallRows(wall); if (rows > (int)(wall.Tag ?? 0)) BuildWall(wall, entries, rows); };
        return b;
    }

    const double WallW = 132, WallH = 198, WallG = 14;
    static int WallRows(Canvas c) => Math.Max(4, (int)Math.Ceiling((c.ActualHeight + 300) / (WallH + WallG)));

    // rows of covers, tilted, each sliding sideways forever (alternate rows the other way); two copies back to back so it loops
    void BuildWall(Canvas c, List<Entry> entries, int rows)
    {
        c.Tag = rows;
        c.Children.Clear();
        var covers = entries.Select(e => e.Cover).OfType<CoverImage>().OrderBy(_ => Random.Shared.Next()).ToList();
        if (covers.Count == 0) return;
        const double Span = 1900;
        var pics = new Dictionary<CoverImage, ImageSource?>();
        ImageSource? Pic(CoverImage ci) => pics.TryGetValue(ci, out var p) ? p : pics[ci] = Picture(ci, 200);
        var wall = new Canvas { RenderTransform = new RotateTransform { Angle = -8 } };
        for (int r = 0; r < rows; r++)
        {
            var seq = new List<CoverImage>();
            for (int k = r * 3; seq.Count * (WallW + WallG) < Span; k++) seq.Add(covers[k % covers.Count]);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var ci in seq.Concat(seq))
                row.Children.Add(new Border { Width = WallW, Height = WallH, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, WallG, 0), Background = new ImageBrush { ImageSource = Pic(ci), Stretch = Stretch.UniformToFill } });
            double aw = seq.Count * (WallW + WallG);
            Canvas.SetTop(row, r * (WallH + WallG));
            var shift = new TranslateTransform();
            row.RenderTransform = shift;
            bool left = r % 2 == 0;
            var anim = new DoubleAnimation { From = left ? 0 : -aw, To = left ? -aw : 0, Duration = TimeSpan.FromSeconds(aw / 10), RepeatBehavior = RepeatBehavior.Forever };
            var sb = new Storyboard(); Storyboard.SetTarget(anim, shift); Storyboard.SetTargetProperty(anim, "X"); sb.Children.Add(anim);
            row.Loaded += (_, _) => sb.Begin();
            row.Unloaded += (_, _) => sb.Stop();
            wall.Children.Add(row);
        }
        Canvas.SetLeft(wall, -160); Canvas.SetTop(wall, -90);
        c.Children.Add(wall);
    }

    // ---------- dragging a tile: it lifts off and follows the pointer, the others slide out of its way; held over an
    // arrow it goes to the next page. Done with the pointer (the tiles are buttons: the test pipe presses them). ----------

    List<string>? dragOrder;
    string? dragId;
    bool tileDragged, tilesStale;
    Button? pressedTile;
    Point pressAt, grab;
    DateTime reorderAt;
    Microsoft.UI.Dispatching.DispatcherQueueTimer? pageFlip;
    int flipDir;

    void SetupTileDrag()
    {
        pageFlip = DispatcherQueue.CreateTimer();
        pageFlip.Interval = TimeSpan.FromMilliseconds(550);
        pageFlip.Tick += (_, _) =>
        {
            if (dragId == null || dragOrder == null) { pageFlip.Stop(); return; }
            int next = homePage + flipDir;
            if (next < 0 || next >= PageCount(dragOrder)) { pageFlip.Stop(); return; }
            dragOrder.Remove(dragId);
            dragOrder.Insert(Math.Min(flipDir > 0 ? next * PerPage : next * PerPage + PerPage - 1, dragOrder.Count), dragId);
            homePage = next;
            PlaceTiles(flipDir);
        };
    }

    void HookTileDrag(Button b)
    {
        b.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (!e.GetCurrentPoint(home).Properties.IsLeftButtonPressed) return;
            pressedTile = b; pressAt = e.GetCurrentPoint(home).Position; tileDragged = false;
        }), true);
        b.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, e) =>
        {
            if (pressedTile != b) return;
            var p = e.GetCurrentPoint(home).Position;
            if (dragId == null)
            {
                if (!e.GetCurrentPoint(home).Properties.IsLeftButtonPressed) { pressedTile = null; return; }
                if (Math.Abs(p.X - pressAt.X) + Math.Abs(p.Y - pressAt.Y) < 12) return;
                StartTileDrag(b, e);
            }
            FollowPointer(b, p);
        }), true);
        b.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, e) => { if (pressedTile == b) EndTileDrag(b, e); }), true);
        b.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, e) => { if (pressedTile == b && dragId != null) EndTileDrag(b, e); }), true);
    }

    void StartTileDrag(Button b, PointerRoutedEventArgs e)
    {
        tileDragged = true;
        dragId = (string)b.Tag;
        dragOrder = HomeOrder();
        var origin = b.TransformToVisual(home).TransformPoint(new Point(0, 0));
        grab = new Point(pressAt.X - origin.X, pressAt.Y - origin.Y);
        b.CapturePointer(e.Pointer);
        b.ScaleTransition = null;
        b.CenterPoint = new Vector3((float)grab.X, (float)grab.Y, 0); // it shrinks around where it was picked up
        b.Scale = new Vector3(0.82f, 0.82f, 1);
        b.Rotation = -1.5f;
        b.Opacity = 0.94;
        Canvas.SetZIndex(b, 10);
    }

    void FollowPointer(Button b, Point p)
    {
        var slot = SlotOf(b);
        b.Translation = new Vector3((float)(p.X - grab.X - slot.X), (float)(p.Y - grab.Y - slot.Y), 0);
        // over another tile on this page: take its place, the rest slide along
        foreach (var other in tiles.Children.OfType<Button>())
        {
            if (other == b || DateTime.UtcNow < reorderAt) continue;
            var o = other.TransformToVisual(home).TransformPoint(new Point(0, 0));
            if (p.X < o.X || p.Y < o.Y || p.X > o.X + other.ActualWidth || p.Y > o.Y + other.ActualHeight) continue;
            int to = homePage * PerPage + tiles.Children.IndexOf(other);
            if (dragOrder!.IndexOf(dragId!) == to) break;
            dragOrder.Remove(dragId!);
            dragOrder.Insert(Math.Min(to, dragOrder.Count), dragId!);
            reorderAt = DateTime.UtcNow.AddMilliseconds(300);
            PlaceTiles(0);
            var s2 = SlotOf(b);
            b.Translation = new Vector3((float)(p.X - grab.X - s2.X), (float)(p.Y - grab.Y - s2.Y), 0);
            break;
        }
        // held over an arrow: carry it to the next (or previous) page
        int dir = Over(pageNext, p) ? 1 : Over(pagePrev, p) ? -1 : 0;
        if (dir != 0 && (!pageFlip!.IsRunning || dir != flipDir)) { flipDir = dir; pageFlip.Stop(); pageFlip.Start(); }
        else if (dir == 0) pageFlip!.Stop();
    }

    // where the tile's own slot in the grid is (its layout position, without the drag)
    Point SlotOf(Button b)
    {
        var t = b.Translation; b.Translation = Vector3.Zero;
        var at = b.TransformToVisual(home).TransformPoint(new Point(0, 0));
        b.Translation = t;
        return at;
    }

    bool Over(FrameworkElement el, Point p)
    {
        if (el.Visibility != Visibility.Visible) return false;
        var o = el.TransformToVisual(home).TransformPoint(new Point(0, 0));
        return p.X >= o.X - 20 && p.Y >= o.Y - 20 && p.X <= o.X + el.ActualWidth + 20 && p.Y <= o.Y + el.ActualHeight + 20;
    }

    void EndTileDrag(Button b, PointerRoutedEventArgs e)
    {
        pressedTile = null;
        if (dragId == null) return;
        pageFlip?.Stop();
        b.ReleasePointerCapture(e.Pointer);
        var order = dragOrder!;
        dragOrder = null; dragId = null;
        SaveTileOrder(order);
        // settle into its slot
        b.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(220) };
        b.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(220) };
        b.RotationTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(220) };
        b.Translation = Vector3.Zero; b.Scale = Vector3.One; b.Rotation = 0; b.Opacity = 1;
        DispatcherQueue.TryEnqueue(() => b.CenterPoint = new Vector3((float)b.ActualWidth / 2, (float)b.ActualHeight / 2, 0));
        if (tilesStale) BuildTiles(); else PlaceTiles(0);
    }

    // Home tiles in their new order; libraries hidden from home keep their place after them.
    void SaveTileOrder(List<string> order)
    {
        var all = Libs;
        var moved = order.Select(id => all.FirstOrDefault(l => l.Id == id)).OfType<MediaLibrary>().Concat(all.Where(l => !order.Contains(l.Id))).ToList();
        all.Clear();
        all.AddRange(moved);
        Settings.Current.Save();
    }

    // ---------- Continue watching: every show you're part-way through, or onto the next episode of, newest first ----------

    void UpdateContinue()
    {
        continueItems.Children.Clear();
        var items = lib == null ? new() : ContinueList();
        continueRow.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (e, i, rec, _) in items) continueItems.Children.Add(ContinueCard(e, i, rec, items.Count == 1));
        continueRow.ChangeView(0, null, null, true);
    }

    // one card: banner (or cover), show, episode, progress, Resume / Play
    Button ContinueCard(Entry e, int index, WatchRec? rec, bool single)
    {
        var v = e.Videos[index];
        bool started = rec is { Pos: > 5 };
        var art = PictureOf(InfoService.Cached(e.InfoKey)?.BannerFile, 1200) ?? Picture(e.Cover, 600);
        var body = new Grid { CornerRadius = new CornerRadius(28) };
        body.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)) });
        if (art != null) body.Children.Add(new Image { Source = art, Stretch = Stretch.UniformToFill });
        body.Children.Add(new Border
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0), EndPoint = new Point(1, 0),
                GradientStops = { new GradientStop { Color = Color.FromArgb(0xF0, 0x0A, 0x0B, 0x10), Offset = 0 }, new GradientStop { Color = Color.FromArgb(0xC0, 0x0A, 0x0B, 0x10), Offset = 0.45 }, new GradientStop { Color = Color.FromArgb(0x30, 0x0A, 0x0B, 0x10), Offset = 1 } },
            },
        });
        var dock = new Grid { Margin = new Thickness(single ? 34 : 28, 0, single ? 30 : 24, 0) };
        dock.ColumnDefinitions.Add(new ColumnDefinition()); dock.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var pillRow = new StackPanel { Orientation = Orientation.Horizontal };
        var playIcon = GlassLook.Icon("", 15); playIcon.Margin = new Thickness(0, 0, 10, 0); playIcon.Foreground = TextBrush;
        pillRow.Children.Add(playIcon);
        pillRow.Children.Add(new TextBlock { Text = started ? "Resume" : "Play", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = TextBrush, VerticalAlignment = VerticalAlignment.Center });
        var pill = new Border { CornerRadius = new CornerRadius(24), Background = ThemeW.Grad, Padding = new Thickness(single ? 24 : 18, 10, single ? 24 : 18, 10), VerticalAlignment = VerticalAlignment.Center, Child = pillRow };
        Grid.SetColumn(pill, 1); dock.Children.Add(pill);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        text.Children.Add(new TextBlock { Text = started ? "CONTINUE WATCHING" : "UP NEXT", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ThemeW.Accent });
        text.Children.Add(new TextBlock { Text = e.Title, FontSize = single ? 32 : 26, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = TextBrush, Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = e.Kind == "movie" ? v.Title : v.Label, FontSize = 16, Opacity = 0.8, Foreground = TextBrush, Margin = new Thickness(0, 2, 0, 12), TextTrimming = TextTrimming.CharacterEllipsis });
        var progress = new StackPanel { Orientation = Orientation.Horizontal };
        bool hasDur = started && rec!.Dur > 0;
        if (hasDur)
        {
            double barW = single ? 340 : 200, frac = Math.Clamp(rec!.Pos / rec.Dur, 0, 1);
            var bar = new Grid { Width = barW, Height = 5, VerticalAlignment = VerticalAlignment.Center };
            bar.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)) });
            bar.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = ThemeW.Grad, HorizontalAlignment = HorizontalAlignment.Left, Width = barW * frac });
            progress.Children.Add(bar);
        }
        progress.Children.Add(new TextBlock
        {
            Text = started ? hasDur ? $"{Math.Ceiling((rec!.Dur - rec.Pos) / 60)} min left" : "Resume from " + PlayerBar.Clock(rec!.Pos) : "Next episode",
            FontSize = 14, Foreground = Muted, Margin = new Thickness(hasDur ? 14 : 0, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        });
        text.Children.Add(progress);
        dock.Children.Add(text);
        body.Children.Add(dock);

        // small round glass ✕: off Continue watching until you watch it again
        var x = GlassLook.Icon("", 11); x.Foreground = TextBrush;
        var dotFace = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = GlassLook.GlassFill(), BorderBrush = GlassLook.GlassStroke(), BorderThickness = new Thickness(1), Opacity = 0.8, Child = x };
        x.HorizontalAlignment = HorizontalAlignment.Center;
        var close = GlassLook.Bare(dotFace, $"Remove {e.Title} from Continue watching", () =>
        {
            Watch.Data.Hidden[e.Folder] = DateTime.Now; // comes back once you watch it again
            Watch.Save();
            UpdateContinue();
        });
        ToolTipService.SetToolTip(close, "Remove from Continue watching");
        close.HorizontalAlignment = HorizontalAlignment.Right; close.VerticalAlignment = VerticalAlignment.Top; close.Margin = new Thickness(0, 12, 14, 0);
        close.PointerEntered += (_, _) => dotFace.Opacity = 1; close.PointerExited += (_, _) => dotFace.Opacity = 0.8;
        body.Children.Add(close);
        var edge = new Border { CornerRadius = new CornerRadius(28), BorderThickness = new Thickness(1), BorderBrush = GlassLook.GlassStroke(), Opacity = 0.6, IsHitTestVisible = false };
        body.Children.Add(edge);

        var card = GlassLook.Bare(body, $"Continue watching {e.Title}", () => { returnTo = home; PlayEntry(e, index); }); // mpv's resume point takes it to where you left off
        card.Height = 188;
        card.Margin = new Thickness(0, 4, single ? 0 : 18, 4);
        card.Width = single ? Math.Max(400, continueRow.ActualWidth - 2) : 600;
        card.HorizontalContentAlignment = HorizontalAlignment.Stretch; card.VerticalContentAlignment = VerticalAlignment.Stretch;
        card.PointerEntered += (_, _) => edge.Opacity = 1; card.PointerExited += (_, _) => edge.Opacity = 0.6;
        GlassLook.HoverScale(card, 1.015f, 350);
        card.ContextFlyout = CardMenu(e);
        return card;
    }
}
