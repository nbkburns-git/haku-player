using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace AnimePlayer;

// Home page: one glass tile per library (with a drifting wall of its covers), in your order, 4 to a page.
// Drag a tile: it lifts off and follows the cursor, the others slide out of its way; hold it over an arrow to
// carry it to the next page. The + Library button makes a new library.
public partial class MainWindow
{
    const int PerPage = 4;
    readonly Dictionary<string, Button> tileFor = new(); // library id -> its tile (kept across pages)
    int homePage;
    bool tileDragged, tilesStale;
    Point? tileDown;
    List<string>? dragOrder;   // home order while a drag is going on
    string? dragId, landingId; // the tile being dragged / flying back into its slot
    TileGhost? ghost;
    Vector grab;               // where on the tile it was picked up
    DateTime reorderAt;        // tiles are sliding: no new swap until then (stops them swapping back and forth)
    DispatcherTimer? pageFlip;
    int flipDir;

    List<string> HomeOrder() => dragOrder ?? Libs.Where(l => l.Home).Select(l => l.Id).ToList();
    static int PageCount(List<string> order) => Math.Max(1, (order.Count + PerPage - 1) / PerPage);
    static Canvas WallOf(Button b) => (Canvas)((Grid)b.Content).Children[0];

    // fresh library data: new tiles for every library on home
    void BuildTiles()
    {
        if (dragId != null) { tilesStale = true; return; } // rebuilt when the drag ends
        tilesStale = false;
        foreach (var b in tileFor.Values)
        {
            var c = WallOf(b);
            if (wallClocks.Remove(c, out var clocks)) foreach (var k in clocks) k.End();
            walls.Remove(c);
        }
        tileFor.Clear();
        foreach (var l in Libs.Where(l => l.Home)) tileFor[l.Id] = MakeTile(l, lib.GetValueOrDefault(l.Id) ?? new());
        PlaceTiles(0, false);
    }

    // Puts this page's tiles in the grid. slide: page change direction (they slide in); flip: tiles glide from where they were.
    void PlaceTiles(int slide, bool flip)
    {
        var order = HomeOrder();
        int pages = PageCount(order);
        homePage = Math.Clamp(homePage, 0, pages - 1);
        var ids = order.Skip(homePage * PerPage).Take(PerPage).Where(tileFor.ContainsKey).ToList();
        var before = flip ? Tiles.Children.OfType<Button>().ToDictionary(b => (string)b.Tag, b => b.TranslatePoint(new Point(), Tiles)) : null;

        int n = pages > 1 ? PerPage : ids.Count; // every page keeps the 2 x 2 layout once there's more than one
        Tiles.Columns = n == 4 ? 2 : Math.Max(1, n);
        Tiles.Rows = n == 4 ? 2 : 1;
        Tiles.Children.Clear();
        foreach (var id in ids)
        {
            var b = tileFor[id];
            b.RenderTransform = null;
            b.Margin = n == 4 ? new Thickness(22, 10, 22, 10) : new Thickness(22, 0, 22, 0);
            b.BeginAnimation(OpacityProperty, null);
            b.Opacity = id == dragId || id == landingId ? 0.18 : 1; // its empty slot
            Tiles.Children.Add(b);
        }

        PagePrev.Visibility = PageNext.Visibility = pages > 1 ? Visibility.Visible : Visibility.Collapsed;
        PagePrev.IsEnabled = homePage > 0; PagePrev.Opacity = homePage > 0 ? 1 : 0.35;
        PageNext.IsEnabled = homePage < pages - 1; PageNext.Opacity = homePage < pages - 1 ? 1 : 0.35;
        PageDots.Children.Clear();
        for (int i = 0; pages > 1 && i < pages; i++)
        {
            int page = i;
            var dot = new Button { Style = (Style)FindResource("Flat"), Margin = new Thickness(4, 0, 4, 0), Cursor = Cursors.Hand, Focusable = false,
                Content = new Border { Width = i == homePage ? 22 : 8, Height = 8, CornerRadius = new CornerRadius(4),
                    Background = i == homePage ? (Brush)FindResource("AccentGrad") : new SolidColorBrush(Color.FromArgb(0x55, 255, 255, 255)) } };
            System.Windows.Automation.AutomationProperties.SetName(dot, $"Libraries page {i + 1}");
            dot.Click += (_, _) => GoPage(page);
            PageDots.Children.Add(dot);
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        if (slide != 0)
        {
            TilesShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(slide * 70, 0, TimeSpan.FromMilliseconds(340)) { EasingFunction = ease });
            Tiles.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)));
        }
        if (before != null)
        {
            Tiles.UpdateLayout();
            foreach (var b in Tiles.Children.OfType<Button>())
            {
                if (!before.TryGetValue((string)b.Tag, out var was)) continue;
                var d = was - b.TranslatePoint(new Point(), Tiles);
                if (d.Length < 1) continue;
                var tt = new TranslateTransform();
                b.RenderTransform = tt;
                tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(d.X, 0, TimeSpan.FromMilliseconds(280)) { EasingFunction = ease });
                tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(d.Y, 0, TimeSpan.FromMilliseconds(280)) { EasingFunction = ease });
            }
        }
        Dispatcher.BeginInvoke(UpdateBlobs, DispatcherPriority.Loaded); // only this page's walls drift
    }

    void GoPage(int page)
    {
        page = Math.Clamp(page, 0, PageCount(HomeOrder()) - 1);
        if (page == homePage) return;
        int dir = page > homePage ? 1 : -1;
        homePage = page;
        PlaceTiles(dir, false);
    }
    void OnPagePrev(object s, RoutedEventArgs e) => GoPage(homePage - 1);
    void OnPageNext(object s, RoutedEventArgs e) => GoPage(homePage + 1);

    Button MakeTile(MediaLibrary l, List<Entry> entries)
    {
        var wall = new Canvas { ClipToBounds = true };
        var body = new Grid();
        body.Children.Add(wall);
        body.Children.Add(new Rectangle { Fill = (Brush)FindResource("PanelScrim") });
        var dock = new DockPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(32, 0, 30, 28) };
        var arrow = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 22, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 8), Opacity = 0.85 };
        DockPanel.SetDock(arrow, Dock.Right);
        dock.Children.Add(arrow);
        dock.Children.Add(new StackPanel { Children =
        {
            new TextBlock { Text = l.Name, FontSize = 42, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis },
            new TextBlock { Text = TileCount(entries), FontSize = 16, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 2, 0, 0) },
        } });
        body.Children.Add(dock);
        RoundClip(body, 28);
        var b = new Button { Template = (ControlTemplate)FindResource("PanelTemplate"), Content = body, Tag = l.Id,
            Cursor = Cursors.Hand, Focusable = false, Foreground = (Brush)FindResource("Text"), AllowDrop = true };
        System.Windows.Automation.AutomationProperties.SetName(b, l.Name);
        b.Click += OnTile;
        b.PreviewMouseLeftButtonDown += (_, e) => { tileDown = e.GetPosition(Tiles); tileDragged = false; };
        b.PreviewMouseMove += (_, e) =>
        {
            if (tileDown is not Point from || e.LeftButton != MouseButtonState.Pressed || (e.GetPosition(Tiles) - from).Length < 12) return;
            tileDown = null;
            DragTile(b, e);
        };
        b.DragOver += (_, _) => HoverTile(b);
        b.Drop += (_, e) => { e.Effects = DragDropEffects.Move; e.Handled = true; };
        b.GiveFeedback += (_, e) => { e.UseDefaultCursors = false; Mouse.SetCursor(Cursors.Hand); e.Handled = true; };
        BuildWall(wall, entries);
        return b;
    }

    void DragTile(Button b, MouseEventArgs e)
    {
        tileDragged = true;
        b.ReleaseMouseCapture(); // the button let go: no click when the drag ends
        var id = (string)b.Tag;
        var origin = b.TranslatePoint(new Point(), Home);
        grab = e.GetPosition(Home) - origin;
        var layer = AdornerLayer.GetAdornerLayer(Home);
        ghost = new TileGhost(Home, Snapshot(b), b.RenderSize, origin, grab);
        layer?.Add(ghost);
        dragOrder = HomeOrder();
        dragId = id;
        b.BeginAnimation(OpacityProperty, new DoubleAnimation(0.18, TimeSpan.FromMilliseconds(150)));
        Home.AllowDrop = true;
        Home.Background = Brushes.Transparent; // so the copy follows the cursor over empty parts of the page too
        Home.PreviewDragOver += OnHomeDragOver;
        pageFlip ??= MakePageFlip();
        PagePrev.DragEnter += OnArrowDragEnter; PageNext.DragEnter += OnArrowDragEnter;
        PagePrev.DragLeave += OnArrowDragLeave; PageNext.DragLeave += OnArrowDragLeave;

        var result = DragDrop.DoDragDrop(b, new DataObject("haku-tile", id), DragDropEffects.Move);

        pageFlip.Stop();
        PagePrev.DragEnter -= OnArrowDragEnter; PageNext.DragEnter -= OnArrowDragEnter;
        PagePrev.DragLeave -= OnArrowDragLeave; PageNext.DragLeave -= OnArrowDragLeave;
        Home.PreviewDragOver -= OnHomeDragOver;
        Home.AllowDrop = false;
        Home.Background = null;
        if (result != DragDropEffects.None) SaveTileOrder(dragOrder); // Esc: everything goes back where it was
        dragOrder = null; dragId = null;
        landingId = id;
        if (tilesStale) BuildTiles(); else PlaceTiles(0, true);
        // the lifted copy flies into the tile's slot, then the tile shows again
        var flying = ghost; ghost = null;
        var tile = tileFor.GetValueOrDefault(id);
        void Landed()
        {
            layer?.Remove(flying);
            landingId = null;
            if (tile == null) return;
            tile.BeginAnimation(OpacityProperty, null);
            tile.Opacity = 1;
        }
        if (tile != null && tile.Parent == Tiles) flying.Land(Tiles.TranslatePoint((Point)VisualTreeHelper.GetOffset(tile), Home), Landed);
        else Landed();
    }

    void OnHomeDragOver(object s, DragEventArgs e)
    {
        ghost?.MoveTo(e.GetPosition(Home) - grab);
        e.Effects = DragDropEffects.Move;
    }

    // dragging over another tile on this page: the dragged one takes its place, the rest slide along
    void HoverTile(Button over)
    {
        if (dragId == null || dragOrder == null || (string)over.Tag == dragId || DateTime.UtcNow < reorderAt) return;
        int to = homePage * PerPage + Tiles.Children.IndexOf(over);
        if (to < 0 || dragOrder.IndexOf(dragId) == to) return;
        dragOrder.Remove(dragId);
        dragOrder.Insert(Math.Min(to, dragOrder.Count), dragId);
        reorderAt = DateTime.UtcNow.AddMilliseconds(300);
        PlaceTiles(0, true);
    }

    // hold a dragged tile over an arrow: it goes to the next (or previous) page, one page every half second
    DispatcherTimer MakePageFlip()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(550) };
        t.Tick += (_, _) =>
        {
            if (dragId == null || dragOrder == null) { t.Stop(); return; }
            int next = homePage + flipDir;
            if (next < 0 || next >= PageCount(dragOrder)) { t.Stop(); return; }
            dragOrder.Remove(dragId);
            dragOrder.Insert(Math.Min(flipDir > 0 ? next * PerPage : next * PerPage + PerPage - 1, dragOrder.Count), dragId);
            homePage = next;
            PlaceTiles(flipDir, false);
        };
        return t;
    }
    void OnArrowDragEnter(object s, DragEventArgs e) { flipDir = s == PageNext ? 1 : -1; pageFlip!.Stop(); pageFlip.Start(); }
    void OnArrowDragLeave(object s, DragEventArgs e) => pageFlip?.Stop();

    // a still picture of the tile, for the copy that follows the cursor
    static BitmapSource Snapshot(FrameworkElement el)
    {
        var dpi = VisualTreeHelper.GetDpi(el);
        double w = el.ActualWidth, h = el.ActualHeight;
        var rtb = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(w * dpi.DpiScaleX)), Math.Max(1, (int)Math.Ceiling(h * dpi.DpiScaleY)), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        // the brush's own default framing (the tile's drawn bounds): a fixed 0,0 box only lined up for the leftmost tile
        using (var dc = dv.RenderOpen())
            dc.DrawRectangle(new VisualBrush(el) { Stretch = Stretch.Fill }, null, new Rect(0, 0, w, h));
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
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

    // "15 shows · 19 movies", "93 series · 265 themes", or both for a mixed library
    static string TileCount(List<Entry> entries)
    {
        static string N(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";
        int shows = entries.Count(x => x.Unit != "themes" && x.Kind != "movie"), movies = entries.Count(x => x.Kind == "movie");
        var themes = entries.Where(x => x.Unit == "themes").ToList();
        var parts = new List<string>();
        if (shows > 0) parts.Add(N(shows, "show", "shows"));
        if (movies > 0) parts.Add(N(movies, "movie", "movies"));
        if (themes.Count > 0) parts.Add($"{themes.Count} series · {N(themes.Sum(x => x.Videos.Count), "theme", "themes")}");
        return parts.Count > 0 ? string.Join(" · ", parts) : "Nothing yet · add folders in Settings";
    }

    // + Library: a new, empty library (on the last page), opened in Settings to name it and add its folders.
    void OnAddTile(object s, RoutedEventArgs e)
    {
        Libs.Add(new MediaLibrary());
        Settings.Current.Save();
        lib[Libs[^1].Id] = new();
        homePage = int.MaxValue; // clamped to the last page
        BuildTiles();
        settingsPage = "Library";
        OnOpenSettings(s, e);
        Dispatcher.BeginInvoke(() => SettingsScroll.ScrollToEnd(), DispatcherPriority.Loaded);
    }
}

// The lifted tile while dragging: a smaller picture of it, tilted, with a deep shadow, following the cursor.
sealed class TileGhost : Adorner
{
    const double Lifted = 0.82;
    readonly Image img;
    readonly ScaleTransform scale = new(1, 1);
    readonly RotateTransform turn = new();
    readonly TranslateTransform fly = new();
    Point at;

    // grab: where on the tile it was picked up; it shrinks around that point, so it stays under the cursor
    public TileGhost(UIElement page, BitmapSource shot, Size size, Point at, Vector grab) : base(page)
    {
        IsHitTestVisible = false;
        this.at = at;
        img = new Image { Source = shot, Width = size.Width, Height = size.Height, Opacity = 0.94,
            RenderTransformOrigin = new Point(Math.Clamp(grab.X / size.Width, 0, 1), Math.Clamp(grab.Y / size.Height, 0, 1)),
            RenderTransform = new TransformGroup { Children = { scale, turn, fly } },
            Effect = new DropShadowEffect { BlurRadius = 40, ShadowDepth = 14, Direction = 270, Opacity = 0.6 } };
        AddVisualChild(img);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var lift = new DoubleAnimation(1, Lifted, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, lift);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, lift);
        turn.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, -1.5, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
    }

    public void MoveTo(Point p) { at = p; InvalidateArrange(); }

    // settle into the tile's slot: back to normal size and straight, then done()
    public void Land(Point slot, Action done)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var d = TimeSpan.FromMilliseconds(220);
        var x = new DoubleAnimation(slot.X - at.X, d) { EasingFunction = ease };
        x.Completed += (_, _) => done();
        fly.BeginAnimation(TranslateTransform.XProperty, x);
        fly.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(slot.Y - at.Y, d) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, d) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, d) { EasingFunction = ease });
        turn.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, d) { EasingFunction = ease });
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => img;
    protected override Size MeasureOverride(Size constraint) { img.Measure(new Size(img.Width, img.Height)); return AdornedElement.RenderSize; }
    protected override Size ArrangeOverride(Size finalSize) { img.Arrange(new Rect(at, new Size(img.Width, img.Height))); return finalSize; }
}
