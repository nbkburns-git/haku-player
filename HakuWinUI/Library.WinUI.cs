using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;

namespace AnimePlayer;

// The WinUI library (phase 3): header, home (wordmark, Continue watching, a tile per library with a drifting wall of its
// covers), the library page (search, filter chips, cover cards) and navigation. The logic is shared with the WPF app
// (Library\LibraryLogic.cs, Scan.cs); this is only its UI. Automation names match the WPF app's, for the tests.
public partial class MainWindow
{
    static readonly SolidColorBrush Muted = new(Color.FromArgb(0xFF, 0xA3, 0xA8, 0xB5));
    static readonly SolidColorBrush TextBrush = new(Color.FromArgb(0xFF, 0xF2, 0xF3, 0xF7));
    static readonly FontFamily Icons = new("Segoe Fluent Icons");
    static readonly FontFamily Outfit = new("ms-appx:///Assets/Fonts/Outfit-SemiBold.ttf#Outfit");

    // pages
    readonly Grid pages = new();                 // header + the page below it
    readonly Grid header = new() { Height = 52, Margin = new Thickness(18, 0, 10, 0) };
    readonly Grid headerDrag = new() { Background = new SolidColorBrush(Colors.Transparent) };
    readonly TextBlock headerTitle = new() { Text = "Haku Player", FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Foreground = Muted };
    Button backBtn = null!;
    readonly Grid home = new(), libraryPage = new(), detail = new();
    UIElement? view;                             // the page showing (or videoHost while playing)
    UIElement returnTo = null!;                  // where playback ends up

    // ---------- building the pages ----------

    void SetupLibraryParts()
    {
        pages.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        pages.RowDefinitions.Add(new RowDefinition());
        BuildHeader();
        Grid.SetRow(header, 0); pages.Children.Add(header);
        Canvas.SetZIndex(header, 1); // over the show page's banner, which reaches up under it
        foreach (var p in new[] { home, libraryPage, detail })
        {
            Grid.SetRow(p, 1); p.Visibility = Visibility.Collapsed;
            p.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(260) };
            p.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(340) };
            pages.Children.Add(p);
        }
        BuildHome();
        BuildLibraryPage();
        BuildDetail();
        returnTo = home;
    }

    // The header as the WPF app's: Back (off home), the logo (home), the page's name, then search, sync, settings and the
    // window's own minimize / maximize / close. The empty middle drags the window.
    readonly Border headerLogo = new() { Margin = new Thickness(2, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
    Button headerMax = null!;

    void BuildHeader()
    {
        foreach (var w in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        backBtn = GlassLook.Back(GoBack);
        backBtn.Margin = new Thickness(0, 0, 16, 0);
        backBtn.Visibility = Visibility.Collapsed;
        header.Children.Add(backBtn);
        headerLogo.Child = ThemeW.Logo(24);
        Grid.SetColumn(headerLogo, 1); header.Children.Add(headerLogo);
        headerTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(headerTitle, 2); header.Children.Add(headerTitle);
        Grid.SetColumn(headerDrag, 3); header.Children.Add(headerDrag); // the window drags from here (SetTitleBar)
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        HandButton Cap(string glyph, string name, string tip, Action a, double size = 11, double right = 0, bool close = false)
        {
            var b = GlassLook.Caption(glyph, name, tip, a, size, close);
            b.Margin = new Thickness(0, 0, right, 0);
            return b;
        }
        right.Children.Add(Cap("\uE721", "Search everything", "Search everything", SearchAll, 14, 4));
        right.Children.Add(Cap("\uE72C", "Sync library", "Sync library (rescan for new shows and openings)", () => _ = SyncLibrary(true), 14, 4));
        right.Children.Add(Cap("\uE713", "App settings", "Settings", OpenSettings, 14, 10));
        right.Children.Add(Cap("\uE921", "Minimize", "Minimize", () => (AppWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter)?.Minimize()));
        headerMax = Cap("\uE922", "Header maximize", "Maximize", ToggleMaximize);
        headerMax.Margin = new Thickness(4, 0, 4, 0);
        right.Children.Add(headerMax);
        right.Children.Add(Cap("\uE8BB", "Close", "Close", Close, close: true));
        Grid.SetColumn(right, 4); header.Children.Add(right);
    }

    void ToggleMaximize()
    {
        if (fullscreen || AppWindow.Presenter is not Microsoft.UI.Windowing.OverlappedPresenter op) return;
        if (op.State == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized) op.Restore(); else op.Maximize();
    }

    // the maximize buttons show restore while maximized
    void ShowMaximized()
    {
        bool max = AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Maximized };
        ((FontIcon)headerMax.Content).Glyph = max ? "\uE923" : "\uE922";
        ToolTipService.SetToolTip(headerMax, max ? "Restore" : "Maximize");
        topBar.SetMaximized(max);
    }

    // the logo follows the theme colour
    void UpdateLogos() { headerLogo.Child = ThemeW.Logo(24); settingsLogo.Child = ThemeW.Logo(30); MakeBlobs(); }

    // ---------- navigation ----------

    void Show(UIElement page)
    {
        view = page;
        bool playing = page == videoHost;
        pages.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
        foreach (var p in new UIElement[] { home, libraryPage, detail })
        {
            bool on = p == page;
            p.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (on) { p.Opacity = 0; p.Translation = new Vector3(0, 24, 0); DispatcherQueue.TryEnqueue(() => { p.Opacity = 1; p.Translation = Vector3.Zero; }); }
        }
        SetTitleBar(playing ? topBar.TitleArea : headerDrag);
        backBtn.Visibility = page == home ? Visibility.Collapsed : Visibility.Visible;
        headerLogo.Visibility = page == home ? Visibility.Visible : Visibility.Collapsed;
        tint.Background = new SolidColorBrush(playing ? Colors.Black : Color.FromArgb(GlassAlpha, 0x0A, 0x0B, 0x10)); // solid black behind the player
        blobs.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
        UpdateBlobs();
        ShowAmbient(page == detail && Settings.Current.CoverGlow ? detailEntry : null);
        headerTitle.Text = page == home ? "Haku Player" : libraryTitle.Text;
        if (page == home) UpdateContinue();
        if (page == detail && detailEntry != null) ShowEpisodes(detailEntry, openSeason); // progress from what you just watched
        if (page == libraryPage) ApplyFilter();
    }

    void GoBack()
    {
        if (view == detail) Show(libraryPage);
        else if (view == libraryPage) Show(home);
    }

    // ---------- the library: loaded at start, re-scanned on sync ----------

    async Task LoadLibrary(Task<Dictionary<string, List<Entry>>> scan)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        lib = await scan;
        long scanned = sw.ElapsedMilliseconds;
        Watch.Seed(Everything(lib).Where(e => e.Unit != "themes").SelectMany(e => e.Videos)); // first run: resume points mpv already saved
        ApplyLibrary();
        StartWatcher();
        libraryLoaded = true;
        HideSplash();
        if (pendingPlay is { } open) { pendingPlay = null; returnTo = home; Play(open.Files, open.Index); }
        else if (core == null) Show(home);
        MpvCore.Note($"startup: library scan {scanned} ms, home built {sw.ElapsedMilliseconds - scanned} ms, " +
                      $"{(DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds:0} ms since launch, {Everything(lib).Count()} shows");
        _ = PrefetchDetails(); // details, banners and missing covers for anything new
    }

    // Push the (re)scanned library into whatever is on screen.
    void ApplyLibrary()
    {
        BuildTiles();
        UpdateContinue();
        if (view == libraryPage && currentCategory != "") ApplyFilter();
        if (view == detail && detailEntry is { } cur && lib.Values.SelectMany(x => x).FirstOrDefault(x => x.Folder == cur.Folder) is { } now)
            OpenShow(now, keepSeason: true);
    }

    bool syncing;
    public async Task SyncLibrary(bool manual)
    {
        if (syncing) return;
        syncing = true;
        try
        {
            var old = lib;
            var fresh = await Task.Run(ScanAll);
            lib = fresh;
            ApplyLibrary();
            _ = PrefetchDetails();
            var msg = SyncMessage(old, fresh);
            if (msg.Length > 0) Toast($"Library synced · {msg}");
            else if (manual) Toast("Library is up to date");
        }
        catch { if (manual) Toast("Couldn't read the library folder"); }
        finally { syncing = false; }
    }

    // a show's details came in (PrefetchDetails): show them if you're looking at it
    void OnDetailsFetched(Entry e) { if (view == detail && detailEntry == e) OpenShow(e, keepSeason: true); }
    void OnDetailsDone() { if (view == libraryPage) ApplyFilter(); }

    // Auto-sync: any video or cover added, removed or renamed under the libraries re-syncs a few seconds later.
    readonly List<FileSystemWatcher> watchers = new();
    Microsoft.UI.Dispatching.DispatcherQueueTimer? syncDebounce;
    static readonly HashSet<string> WatchedExt = new(new[] { ".mp4", ".mkv", ".webm", ".avi", ".mov", ".m4v", ".png", ".jpg", ".jpeg", ".webp", ".bmp" }, StringComparer.OrdinalIgnoreCase);

    void StartWatcher()
    {
        foreach (var w in watchers) w.Dispose();
        watchers.Clear();
        if (syncDebounce == null)
        {
            syncDebounce = DispatcherQueue.CreateTimer();
            syncDebounce.Interval = TimeSpan.FromSeconds(3);
            syncDebounce.Tick += (_, _) => { syncDebounce.Stop(); _ = SyncLibrary(false); };
        }
        FileSystemEventHandler changed = (_, e) =>
        {
            var ext = Path.GetExtension(e.FullPath);
            if (ext.Length > 0 && !WatchedExt.Contains(ext)) return; // temp files etc. (folders have no extension)
            DispatcherQueue.TryEnqueue(() => { syncDebounce!.Stop(); syncDebounce.Start(); });
        };
        foreach (var dir in Libs.SelectMany(l => l.Folders).Select(f => f.Path.TrimEnd('\\')).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                var w = new FileSystemWatcher(dir) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName };
                w.Created += changed; w.Deleted += changed; w.Renamed += (s, e) => changed(s, e);
                w.EnableRaisingEvents = true;
                watchers.Add(w);
            }
            catch { } // drives without change notifications: manual sync still works
        }
    }

    // Hover: a touch bigger (and up), easing back on leave.
    static void Lift(FrameworkElement el, float scale, float up)
    {
        el.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(280) };
        el.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(280) };
        el.SizeChanged += (_, _) => el.CenterPoint = new Vector3((float)el.ActualWidth / 2, (float)el.ActualHeight / 2, 0);
        el.PointerEntered += (_, _) => { el.Scale = new Vector3(scale, scale, 1); el.Translation = new Vector3(0, -up, 0); };
        el.PointerExited += (_, _) => { el.Scale = Vector3.One; el.Translation = Vector3.Zero; };
    }

    // ---------- the library page: search, filter chips, cover cards ----------

    readonly TextBlock libraryTitle = new() { FontSize = 40, FontWeight = Microsoft.UI.Text.FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Foreground = TextBrush };
    // the WPF search box: a glass pill, a search icon, the hint until you type (a plain TextBox inside, its chrome off)
    readonly TextBox searchBox = new() { FontSize = 15, Padding = new Thickness(42, 11, 16, 11), VerticalAlignment = VerticalAlignment.Center, Background = new SolidColorBrush(Colors.Transparent),
        BorderThickness = new Thickness(0), Foreground = TextBrush };
    readonly TextBlock searchHint = new() { Text = "Search shows, openings, episodes", FontSize = 15, Margin = new Thickness(46, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    readonly WrapPanel filterBar = new() { Margin = new Thickness(62, 0, 48, 6) };
    readonly ItemsRepeater cards = new();
    // reaches 12 px up into the empty space under the filter chips (padded back down, so the cards sit as in the WPF
    // app): room for a top-row card's hover lift, which the scroll area would otherwise clip
    readonly ScrollViewer cardScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(40, 12, 26, 20), Margin = new Thickness(0, -12, 0, 0) };
    Microsoft.UI.Dispatching.DispatcherQueueTimer? searchDelay;

    void BuildLibraryPage()
    {
        libraryPage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        libraryPage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        libraryPage.RowDefinitions.Add(new RowDefinition());
        var top = new Grid { Margin = new Thickness(64, 10, 48, 10) };
        top.ColumnDefinitions.Add(new ColumnDefinition()); top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(libraryTitle);
        AutomationProperties.SetName(searchBox, "Search box");
        searchBox.TextChanged += (_, _) =>
        {
            // filtering rebuilds the cards, so it waits for a short pause in typing
            searchDelay ??= MakeSearchDelay();
            searchDelay.Stop(); searchDelay.Start();
        };
        var search = new Grid { Width = 400, VerticalAlignment = VerticalAlignment.Center };
        search.Children.Add(new Border { CornerRadius = new CornerRadius(21), Background = GlassLook.GlassFill(), BorderThickness = new Thickness(1), BorderBrush = GlassLook.GlassStroke() });
        var lens = GlassLook.Icon("\uE721", 15); lens.Foreground = Muted; lens.HorizontalAlignment = HorizontalAlignment.Left; lens.Margin = new Thickness(17, 0, 0, 0);
        search.Children.Add(lens);
        searchHint.Foreground = Muted;
        search.Children.Add(searchHint);
        // no WinUI chrome on the box: its own backgrounds and underline are off in every state
        foreach (var k in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused" })
            searchBox.Resources[k] = new SolidColorBrush(Colors.Transparent);
        foreach (var k in new[] { "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused" }) searchBox.Resources[k] = TextBrush;
        searchBox.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
        searchBox.TextChanged += (_, _) => searchHint.Visibility = searchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        search.Children.Add(searchBox);
        Grid.SetColumn(search, 1); top.Children.Add(search);
        libraryPage.Children.Add(top);
        Grid.SetRow(filterBar, 1); libraryPage.Children.Add(filterBar);
        // WPF's WrapPanel of 242 x ~410 cards (each 210 wide with its 16 / 22 margins): no extra spacing
        cards.Layout = new UniformGridLayout { MinItemWidth = 242, MinRowSpacing = 0, MinColumnSpacing = 0, ItemsStretch = UniformGridLayoutItemsStretch.None };
        cards.ItemTemplate = new CardFactory(this);
        cardScroll.Content = cards;
        SmoothScroll.Attach(cardScroll, on => { blobsScrolling = on; UpdateBlobs(); }); // the blobs rest while it scrolls
        Grid.SetRow(cardScroll, 2); libraryPage.Children.Add(cardScroll);
    }

    Microsoft.UI.Dispatching.DispatcherQueueTimer MakeSearchDelay()
    {
        var t = DispatcherQueue.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(150);
        t.Tick += (_, _) => { t.Stop(); if (view == libraryPage) ApplyFilter(); };
        return t;
    }

    void OpenCategory(string key, string name)
    {
        currentCategory = key;
        libraryTitle.Text = name;
        headerTitle.Text = name;
        ResetFilterState(); searchBox.Text = "";
        Show(libraryPage);
    }

    // Header search: everything at once, the cursor in the search box.
    void SearchAll()
    {
        OpenCategory("All", "Everything");
        searchBox.Focus(FocusState.Programmatic);
    }

    void ApplyFilter()
    {
        var all = LibraryBase();
        cards.ItemsSource = Filtered(all, searchBox.Text.Trim());
        BuildFilterBar(all);
    }

    void BuildFilterBar(List<Entry> all)
    {
        filterBar.Children.Clear();
        void Chip(string text, bool on, Action click)
        {
            var b = GlassLook.Chip(text, on, () => { click(); ApplyFilter(); }, 14, $"Filter {text}");
            b.Margin = new Thickness(0, 0, 8, 8);
            filterBar.Children.Add(b);
        }
        void Gap() => filterBar.Children.Add(new Border { Width = 1, Height = 22, Margin = new Thickness(6, 3, 14, 11), Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)) });
        Chip("All", !filterFav, () => filterFav = false);
        Chip("★ Favourites", filterFav, () => filterFav = true);
        var tags = FilterTags(all);
        if (tags.Count > 1) { Gap(); foreach (var t in tags) Chip(t, kindFilter == t, () => kindFilter = kindFilter == t ? null : t); }
        Gap();
        Chip("A–Z", sortMode == "az", () => sortMode = "az");
        Chip("Newest", sortMode == "new", () => sortMode = "new");
        Chip("Top rated", sortMode == "top", () => sortMode = "top");
        var genres = FilterGenres(all);
        if (genres.Count > 0) { Gap(); foreach (var g in genres) Chip(g, genre == g, () => genre = genre == g ? null : g); }
    }

    // A cover card for the repeater: cover (or its name on a gradient), favourite star, title and how much you've watched.
    sealed class CardFactory : IElementFactory
    {
        readonly MainWindow w;
        public CardFactory(MainWindow w) { this.w = w; }
        public UIElement GetElement(ElementFactoryGetArgs args) => w.Card((Entry)args.Data);
        public void RecycleElement(ElementFactoryRecycleArgs args) { }
    }

    UIElement Card(Entry e)
    {
        var face = new Grid { Width = 210, Height = 315, CornerRadius = new CornerRadius(18) };
        face.Children.Add(new Border
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 1),
                GradientStops = { new GradientStop { Color = Color.FromArgb(0xFF, 0x40, 0x46, 0x5A), Offset = 0 }, new GradientStop { Color = Color.FromArgb(0xFF, 0x1A, 0x1C, 0x26), Offset = 1 } },
            },
            Child = new TextBlock { Text = e.Title, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16), FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Muted },
        });
        if (Picture(e.Cover) is { } pic) face.Children.Add(new Border { Background = new ImageBrush { ImageSource = pic, Stretch = Stretch.UniformToFill } });
        if (e.IsRandom)
        {
            var r = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            r.Children.Add(new FontIcon { Glyph = "", FontFamily = Icons, FontSize = 64, Foreground = TextBrush });
            var rt = GlassLook.Label("Random", 26, bold: true); rt.HorizontalAlignment = HorizontalAlignment.Center; rt.Margin = new Thickness(0, 18, 0, 0);
            r.Children.Add(rt);
            var rs = GlassLook.Label("Next = another random one", 13); rs.HorizontalAlignment = HorizontalAlignment.Center; rs.Opacity = 0.85; rs.Margin = new Thickness(0, 4, 0, 0);
            r.Children.Add(rs);
            face.Children.Add(new Border { Background = GlassLook.Accent(), Child = r });
        }
        // hover: a sheen sweeps across the cover (WPF CardTemplate)
        var sweep = new TranslateTransform { X = -420 };
        var sheen = new Microsoft.UI.Xaml.Shapes.Rectangle { Width = 420, Height = 315, HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false, Opacity = 0, RenderTransform = sweep,
            Fill = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 1),
                GradientStops = { new GradientStop { Color = Color.FromArgb(0, 0xFF, 0xFF, 0xFF), Offset = 0.3 }, new GradientStop { Color = Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF), Offset = 0.5 }, new GradientStop { Color = Color.FromArgb(0, 0xFF, 0xFF, 0xFF), Offset = 0.7 } } } };
        face.Children.Add(sheen);
        var sheenRun = new Storyboard();
        var run = new DoubleAnimation { From = -420, To = 210, Duration = TimeSpan.FromSeconds(0.7), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut } };
        Storyboard.SetTarget(run, sweep); Storyboard.SetTargetProperty(run, "X"); sheenRun.Children.Add(run);
        face.PointerEntered += (_, _) => { sheen.Opacity = 1; sheenRun.Begin(); };
        face.PointerExited += (_, _) => sheen.Opacity = 0;
        face.Children.Add(new Border { CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(1), BorderBrush = GlassLook.GlassStroke(), IsHitTestVisible = false });
        if (!e.IsRandom)
        {
            var star = StarButton(e, 27);
            star.HorizontalAlignment = HorizontalAlignment.Right; star.VerticalAlignment = VerticalAlignment.Top; star.Margin = new Thickness(0, 8, 8, 0);
            AutomationProperties.SetName(star, "Star");
            face.Children.Add(star);
        }
        var col = new StackPanel { Width = 210 };
        col.Children.Add(face);
        var title = GlassLook.Label(e.Title, 16, bold: true); title.Margin = new Thickness(6, 14, 6, 0); title.TextTrimming = TextTrimming.CharacterEllipsis;
        var sub = GlassLook.Label(CardSubtitle(e), 13); sub.Margin = new Thickness(6, 3, 6, 0); sub.Foreground = Muted; sub.TextTrimming = TextTrimming.CharacterEllipsis;
        col.Children.Add(title); col.Children.Add(sub);
        var b = GlassLook.Bare(col, e.Title, null);
        b.Margin = new Thickness(16, 16, 16, 22);
        b.VerticalAlignment = VerticalAlignment.Top;
        Lift(face, 1.04f, 10);
        b.Click += (_, _) => OpenEntry(e);
        b.ContextFlyout = CardMenu(e);
        return b;
    }

    void OpenEntry(Entry e)
    {
        if (e.IsRandom)
        {
            // a shuffled playlist of every opening: Next just goes to another random one
            returnTo = libraryPage;
            PlayEntry(e with { Name = "Random", Videos = e.Videos.OrderBy(_ => Random.Shared.Next()).ToList() }, 0);
            return;
        }
        OpenShow(e);
    }

    // a show's or a video's favourite star (WPF FavConverter): on a card the glass star (frosted body, bright outline;
    // gold when a favourite), in a list a single star, gold or soft white
    static readonly Color Gold = Color.FromArgb(0xFF, 0xFF, 0xC8, 0x3D);
    Button StarButton(object item, double size)
    {
        bool glass = size >= 27;
        var body = new FontIcon { Glyph = glass ? "\uE735" : IsFav(item) ? "\uE735" : "\uE734", FontFamily = Icons, FontSize = size };
        var outline = new FontIcon { Glyph = "\uE734", FontFamily = Icons, FontSize = size, Foreground = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)) };
        void Paint()
        {
            bool on = IsFav(item);
            if (glass) body.Foreground = new SolidColorBrush(on ? Gold : Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
            else { body.Glyph = on ? "\uE735" : "\uE734"; body.Foreground = new SolidColorBrush(on ? Gold : Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)); }
        }
        Paint();
        var g = new Grid();
        g.Children.Add(body);
        if (glass) g.Children.Add(outline);
        var b = GlassLook.Bare(g, "Favourite", null);
        if (glass) { b.Width = 46; b.Height = 46; } else { b.Width = 34; b.Height = 30; }
        b.HorizontalContentAlignment = HorizontalAlignment.Center; b.VerticalContentAlignment = VerticalAlignment.Center;
        ToolTipService.SetToolTip(b, "Favourite");
        GlassLook.HoverScale(g, 1.15f);
        b.Click += (_, _) =>
        {
            if (!ToggleFavData(item)) return;
            Paint();
            if (filterFav && view == libraryPage) ApplyFilter();
        };
        return b;
    }

    // What you watched changed (ticks, Mark all, coming back from the player): redraw what shows it.
    void RefreshWatchViews()
    {
        if (view == libraryPage) ApplyFilter();
        if (view == home) UpdateContinue();
        if (view == detail && detailEntry != null) ShowEpisodes(detailEntry, openSeason);
    }
}
