using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AnimePlayer;

// Settings (WinUI): a glass card over the page, sections on the left, the chosen section's settings on the right.
// The same pages, wording and automation names as the WPF app's GlobalSettings.cs; both read and write the same Settings.
public partial class MainWindow
{
    static Settings S => Settings.Current;
    static readonly string ThumbDir = Path.Combine(Settings.DataDir, "thumbs");
    // the page behind blurs (in-app acrylic), as the WPF app blurs it while Settings is open
    readonly Grid settingsLayer = new() { Visibility = Visibility.Collapsed, Background = new AcrylicBrush { TintColor = Color.FromArgb(0xFF, 0x05, 0x06, 0x08), TintOpacity = 0.2, TintLuminosityOpacity = 0.0, FallbackColor = Color.FromArgb(0xCC, 0x0A, 0x0B, 0x10) } };
    readonly Border settingsLogo = new() { Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly Grid settingsCard = new() { MaxWidth = 940, MaxHeight = 700, Margin = new Thickness(40, 70, 40, 40) };
    readonly StackPanel settingsNav = new() { Margin = new Thickness(0, 0, 16, 0), Spacing = 4 };
    readonly StackPanel settingsItems = new();
    readonly ScrollViewer settingsScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 10, 0) };
    string settingsPage = "Library";
    bool upscalePreparing; string? upscaleDetecting;
    LibraryFolder? tagEditor; string newTagName = "", newTagKind = "tv";
    static readonly (string Key, string Icon, string Name)[] SettingsPages =
    {
        ("Library", "", "Library"), ("Appearance", "", "Appearance"), ("Playback", "", "Playback"), ("Upscale", "", "AI upscaling"),
        ("Audio", "", "Audio & subtitles"), ("Stats", "", "Stats"), ("Advanced", "", "Advanced"),
    };

    void SetupSettings()
    {
        var card = settingsCard;
        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition());
        var top = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        top.ColumnDefinitions.Add(new ColumnDefinition()); top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new StackPanel { Orientation = Orientation.Horizontal };
        settingsLogo.Child = ThemeW.Logo(30);
        name.Children.Add(settingsLogo);
        name.Children.Add(GlassLook.Label("Settings", 26, bold: true));
        top.Children.Add(name);
        var close = GlassLook.Caption("", "Close settings", "Close (Esc)", CloseSettings, close: true);
        Grid.SetColumn(close, 1); top.Children.Add(close);
        body.Children.Add(top);
        var cols = new Grid();
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) }); cols.ColumnDefinitions.Add(new ColumnDefinition());
        cols.Children.Add(settingsNav);
        settingsScroll.Content = settingsItems;
        SmoothScroll.Attach(settingsScroll);
        var right = new Border { BorderThickness = new Thickness(1, 0, 0, 0), BorderBrush = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)), Padding = new Thickness(24, 0, 0, 0), Child = settingsScroll };
        Grid.SetColumn(right, 1); cols.Children.Add(right);
        Grid.SetRow(cols, 1); body.Children.Add(cols);
        // the WPF card: a white sheen over the glass tint, specular rims
        var sheet = new Border { Padding = new Thickness(30, 24, 22, 24), Child = body, Background = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0.4, 1),
            GradientStops = { new GradientStop { Color = Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF), Offset = 0 }, new GradientStop { Color = Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF), Offset = 0.5 }, new GradientStop { Color = Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF), Offset = 1 } },
        } };
        var glass = GlassLook.VideoPanel(sheet, 28);
        card.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(260) };
        card.SizeChanged += (_, _) => card.CenterPoint = new System.Numerics.Vector3((float)card.ActualWidth / 2, (float)card.ActualHeight / 2, 0);
        card.Children.Add(glass);
        settingsLayer.Children.Add(card);
        settingsLayer.PointerPressed += (_, e) => { if (e.OriginalSource == settingsLayer) CloseSettings(); };
        settingsLayer.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(180) };
        root.Children.Add(settingsLayer);
    }

    void OpenSettings()
    {
        BuildGlobalSettings();
        settingsLayer.Opacity = 0;
        settingsLayer.IsHitTestVisible = true;
        settingsCard.Scale = new System.Numerics.Vector3(0.96f, 0.96f, 1);
        settingsLayer.Visibility = Visibility.Visible;
        DispatcherQueue.TryEnqueue(() => { settingsLayer.Opacity = 1; settingsCard.Scale = System.Numerics.Vector3.One; });
    }

    async void CloseSettings()
    {
        if (settingsLayer.Visibility != Visibility.Visible) return;
        settingsLayer.Opacity = 0; // OpacityTransition: a short fade
        settingsLayer.IsHitTestVisible = false;
        await System.Threading.Tasks.Task.Delay(160);
        if (settingsLayer.Opacity == 0) settingsLayer.Visibility = Visibility.Collapsed;
        settingsLayer.IsHitTestVisible = true;
    }
    bool SettingsOpen => settingsLayer.Visibility == Visibility.Visible;

    void BuildSettingsNav()
    {
        settingsNav.Children.Clear();
        foreach (var (key, icon, name) in SettingsPages)
        {
            bool on = key == settingsPage;
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var i = GlassLook.Icon(icon, 16); i.Width = 30; i.Foreground = on ? ThemeW.Accent : Muted;
            var t = GlassLook.Label(name, 15, bold: on); t.Opacity = on ? 1 : 0.8;
            row.Children.Add(i); row.Children.Add(t);
            var item = new Border { Child = row, CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 11, 14, 11), Background = on ? new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)) : new SolidColorBrush(Colors.Transparent) };
            var b = GlassLook.Flat(item, $"Settings: {name}", () => { settingsPage = key; settingsScroll.ChangeView(null, 0, null, true); BuildGlobalSettings(); });
            b.HorizontalAlignment = HorizontalAlignment.Stretch; b.HorizontalContentAlignment = HorizontalAlignment.Stretch; b.Padding = new Thickness(0);
            settingsNav.Children.Add(b);
        }
    }

    void BuildGlobalSettings()
    {
        BuildSettingsNav();
        var p = settingsItems;
        p.Children.Clear();
        void Change(Action a) { a(); S.Save(); BuildGlobalSettings(); }
        void Note(string text) { var t = GlassLook.Label(text, 12.5); t.Foreground = Muted; t.TextWrapping = TextWrapping.Wrap; t.LineHeight = 20; t.Margin = new Thickness(4, 8, 0, 0); p.Children.Add(t); }
        Button Glass(object content, string name, Action click)
        {
            var b = GlassLook.Glass(content is string t ? new TextBlock { Text = t, FontSize = 14, Foreground = TextBrush } : content, name, click, new Thickness(16, 8, 16, 8), 14);
            b.Margin = new Thickness(0, 6, 8, 0);
            return b;
        }
        switch (settingsPage)
        {
            case "Library": LibraryPage(); break;
            case "Appearance": AppearancePage(); break;
            case "Playback": PlaybackPage(); break;
            case "Upscale": UpscalePage(); break;
            case "Audio": AudioPage(); break;
            case "Stats": StatsPage(p); break;
            default: AdvancedPage(); break;
        }

        void LibraryPage()
        {
            async Task Rescan() { S.Save(); BuildGlobalSettings(); StartWatcher(); await SyncLibrary(true); }
            void Reorder() { S.Save(); BuildGlobalSettings(); BuildTiles(); }
            Section(p, "Libraries");
            for (int i = 0; i < S.Libs.Count; i++)
            {
                var l = S.Libs[i]; int at = i;
                var inner = new StackPanel();
                p.Children.Add(new Border
                {
                    Child = inner, CornerRadius = new CornerRadius(16), Padding = new Thickness(16, 12, 12, 12), Margin = new Thickness(0, 0, 0, 10), BorderThickness = new Thickness(1),
                    Background = new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                });
                var head = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                head.ColumnDefinitions.Add(new ColumnDefinition()); head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var name = new TextBox { Text = l.Name, FontSize = 19, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0) };
                AutomationProperties.SetName(name, "Library name");
                name.TextChanged += (_, _) => { l.Name = name.Text.Trim().Length > 0 ? name.Text.Trim() : "Library"; S.Save(); };
                name.LostFocus += (_, _) => BuildTiles();
                name.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { BuildTiles(); root.Focus(FocusState.Programmatic); e.Handled = true; } };
                ToolTipService.SetToolTip(name, "Library name: click to rename");
                head.Children.Add(name);
                var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                tools.Children.Add(Tip(Chip("On home", l.Home, $"{l.Name}: on home", () => { l.Home = !l.Home; Reorder(); }), "Show this library as a tile on the home page"));
                tools.Children.Add(Tip(IconBtn("", $"Move {l.Name} up", at > 0, () => { S.Libs.RemoveAt(at); S.Libs.Insert(at - 1, l); Reorder(); }), "Move up (earlier on the home page)"));
                tools.Children.Add(Tip(IconBtn("", $"Move {l.Name} down", at < S.Libs.Count - 1, () => { S.Libs.RemoveAt(at); S.Libs.Insert(at + 1, l); Reorder(); }), "Move down (later on the home page)"));
                tools.Children.Add(Tip(IconBtn("", $"Delete library {l.Name}", true, async () => { S.Libs.Remove(l); await Rescan(); }), "Delete this library (your files are not touched)"));
                Grid.SetColumn(tools, 1); head.Children.Add(tools);
                inner.Children.Add(head);
                if (l.Folders.Count == 0) { var t = GlassLook.Label("No folders yet. Add one, then choose what's in it.", 13); t.Foreground = Muted; t.Margin = new Thickness(0, 2, 0, 8); inner.Children.Add(t); }
                foreach (var f in l.Folders.ToList())
                {
                    var box = new StackPanel { Margin = new Thickness(0, 6, 0, 4) };
                    bool here = Directory.Exists(f.Path);
                    string what = f.Kind switch { "tv" => "A folder of TV show folders", "movie" => "A folder of movies", "themes" => "Opening and ending folders, one per show", _ => "A folder of anime show folders" };
                    box.Children.Add(Row(Tip(IconBtn("", $"Remove folder {f.Path}", true, async () => { l.Folders.Remove(f); await Rescan(); }), "Remove this folder from the library (your files are not touched)"),
                        Label(f.Path, !here ? "Not found: drive unplugged or folder moved" : f.Tag != null ? $"{what} · read like {LibraryFolder.KindName(f.Kind)}" : what)));
                    var tags = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
                    foreach (var (k, label) in LibraryFolder.Kinds)
                        tags.Children.Add(Chip(label, f.Tag == null && f.Kind == k, $"{f.Path}: {label}", async () => { f.Kind = k; f.Tag = null; await Rescan(); }));
                    foreach (var tg in S.Tags.ToList())
                        tags.Children.Add(Chip(tg.Name, f.Tag == tg.Name, $"{f.Path}: {tg.Name}", async () => { f.Kind = tg.Kind; f.Tag = tg.Name; await Rescan(); }));
                    tags.Children.Add(Chip("+ Tag", tagEditor == f, $"{f.Path}: new tag", () => { tagEditor = tagEditor == f ? null : f; BuildGlobalSettings(); }));
                    box.Children.Add(tags);
                    if (tagEditor == f) box.Children.Add(TagEditor(f));
                    inner.Children.Add(box);
                }
                inner.Children.Add(Glass(Plus("Add folder"), $"Add folder to {l.Name}", async () =>
                {
                    var picker = new Windows.Storage.Pickers.FolderPicker();
                    picker.FileTypeFilter.Add("*");
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                    var folder = await picker.PickSingleFolderAsync();
                    if (folder == null) return;
                    var path = folder.Path;
                    if (l.Folders.Any(x => string.Equals(x.Path.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) { Toast("That folder is already in this library"); return; }
                    l.Folders.Add(new LibraryFolder { Path = path, Kind = GuessKind(path) }); // best guess; change it with the chips
                    await Rescan();
                }));
            }
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(Glass(Plus("New library"), "New library", () => { S.Libs.Add(new MediaLibrary()); lib[S.Libs[^1].Id] = new(); Reorder(); }));
            buttons.Children.Add(Glass("Sync now", "Sync now", () => _ = SyncLibrary(true)));
            p.Children.Add(buttons);
            Note("Each library is a tile on the home page; drag the tiles there to move them. A library can hold any mix of folders, and their tags become filters inside it. Folders can be laid out any way: shows inside other folders (A–Z, genres…), season folders, or loose files. New files are picked up automatically while Haku Player is open.");
            Note("Details come from AniList for anime and Cinemeta (IMDb) for TV shows and movies. Your own tags get the details of the kind they're read like.");

            UIElement TagEditor(LibraryFolder f)
            {
                var sp = new StackPanel { Margin = new Thickness(12, 2, 0, 6) };
                var nameBox = new TextBox { Width = 220, Text = newTagName, PlaceholderText = "Tag name" };
                AutomationProperties.SetName(nameBox, "New tag name");
                nameBox.TextChanged += (_, _) => newTagName = nameBox.Text;
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(nameBox);
                row.Children.Add(Glass("Add tag", "Add tag", async () =>
                {
                    var n = newTagName.Trim();
                    if (n.Length == 0) { Toast("Give the tag a name"); return; }
                    if (LibraryFolder.Kinds.Any(k => k.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) || S.Tags.Any(t => t.Name.Equals(n, StringComparison.OrdinalIgnoreCase))) { Toast("There's already a tag with that name"); return; }
                    S.Tags.Add(new TagDef { Name = n, Kind = newTagKind });
                    f.Tag = n; f.Kind = newTagKind;
                    tagEditor = null; newTagName = "";
                    await Rescan();
                }));
                sp.Children.Add(row);
                var like = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
                var lt = GlassLook.Label("Read like", 13); lt.Foreground = Muted; lt.Margin = new Thickness(2, 0, 10, 0);
                like.Children.Add(lt);
                foreach (var (k, label) in LibraryFolder.Kinds)
                    like.Children.Add(Chip(label, newTagKind == k, $"New tag read like {label}", () => { newTagKind = k; BuildGlobalSettings(); }));
                sp.Children.Add(like);
                return sp;
            }
        }

        void AppearancePage()
        {
            Section(p, "Theme");
            var swatches = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 4) };
            foreach (var (key, name) in ThemeW.All)
            {
                var (a, b, c) = ThemeW.Colors(key);
                bool on = S.Theme == key;
                var dot = new Border
                {
                    Width = 44, Height = 44, CornerRadius = new CornerRadius(22), HorizontalAlignment = HorizontalAlignment.Center,
                    Background = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 1), GradientStops = { new GradientStop { Color = a, Offset = 0 }, new GradientStop { Color = b, Offset = 0.55 }, new GradientStop { Color = c, Offset = 1 } } },
                    BorderThickness = new Thickness(on ? 3 : 1), BorderBrush = new SolidColorBrush(on ? Colors.White : Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                };
                var sp = new StackPanel { Width = 92 };
                sp.Children.Add(dot);
                var t = GlassLook.Label(name, 13, bold: on); t.HorizontalAlignment = HorizontalAlignment.Center; t.Margin = new Thickness(0, 6, 0, 0); t.Opacity = on ? 1 : 0.75;
                sp.Children.Add(t);
                swatches.Children.Add(GlassLook.Flat(sp, $"Theme: {name}", () => Change(() => { S.Theme = key; ThemeW.Apply(key); UpdateLogos(); })));
            }
            p.Children.Add(swatches);
            if (S.Theme == "custom")
            {
                var hex = new TextBox { Text = S.CustomColor, Width = 140, PlaceholderText = "#2BA8FF" };
                AutomationProperties.SetName(hex, "Custom colour (hex)");
                void TypeHex()
                {
                    var t = hex.Text.Trim(); if (!t.StartsWith('#')) t = "#" + t;
                    if (t.Equals(S.CustomColor, StringComparison.OrdinalIgnoreCase)) return;
                    if (ThemeW.Parse(t) is not Color col) { Toast("Use a hex colour like #2BA8FF"); hex.Text = S.CustomColor; return; }
                    S.CustomColor = ThemeW.Hex(col); ThemeW.Apply("custom"); UpdateLogos(); S.Save(); BuildGlobalSettings();
                }
                hex.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { TypeHex(); e.Handled = true; } };
                hex.LostFocus += (_, _) => TypeHex();
                p.Children.Add(Row(HueBar(hex), Label("Custom colour", "Drag along the colours, or type a hex colour like #2BA8FF")));
            }
            Section(p, "Glass");
            Chips(p, "Glass clarity", new[] { (0, "Clear"), (1, "Balanced"), (2, "Frosted") }, S.Glass, v => Change(() => { S.Glass = v; ApplyGlassTint(); }));
            Section(p, "Background");
            Toggle(p, "Moving background", "Slow colour glows drifting behind the glass", S.MovingBackground, () => Change(() => { S.MovingBackground = !S.MovingBackground; MakeBlobs(); }));
            if (S.MovingBackground)
                Toggle(p, "Keep moving when not in focus", "While another app is in front. Off saves CPU", S.MovingAlways, () => Change(() => { S.MovingAlways = !S.MovingAlways; UpdateBlobs(); }), child: true);
            Toggle(p, "Cover glow on show pages", "Tints the page with the show's cover colours", S.CoverGlow, () => Change(() => S.CoverGlow = !S.CoverGlow));
        }

        void PlaybackPage()
        {
            Section(p, "Playing");
            Toggle(p, "Autoplay next video", "When an episode or theme ends, start the next one", S.Autoplay, () => Change(() => S.Autoplay = !S.Autoplay));
            Toggle(p, "Resume where I left off", "Shows only; openings and endings always start from the beginning", S.Resume, () => Change(() => S.Resume = !S.Resume));
            Toggle(p, "Auto pop-out", "Start videos in the small always-on-top player", S.AutoPopOut, () => Change(() => S.AutoPopOut = !S.AutoPopOut));
            Section(p, "Openings, endings & up next");
            Toggle(p, "Skip opening / ending buttons", "A Skip button while an opening, ending or recap plays", S.SkipButtons, () => Change(() => S.SkipButtons = !S.SkipButtons));
            if (S.SkipButtons)
                Toggle(p, "Skip openings automatically", "Openings and recaps are skipped without asking", S.AutoSkip, () => Change(() => S.AutoSkip = !S.AutoSkip), child: true);
            Toggle(p, "Up next countdown", "When the ending starts, the next episode plays after 10 seconds (with Autoplay on)", S.UpNext, () => Change(() => S.UpNext = !S.UpNext));
            Toggle(p, "Look up times online", "AniSkip for anime, TheIntroDB and IntroDB for TV shows and movies, when the file has no chapters for them", S.OnlineSegments, () => Change(() => S.OnlineSegments = !S.OnlineSegments));
            Toggle(p, "Find them by comparing episodes", "If nothing else knows: matches the opening and ending music between two episodes of a season (takes a few seconds, once)", S.DetectSegments, () => Change(() => S.DetectSegments = !S.DetectSegments));
            Section(p, "Player controls");
            Chips(p, "Hide controls after", new[] { (2.0, "2s"), (3.0, "3s"), (5.0, "5s") }, S.HideAfter, v => Change(() => { S.HideAfter = v; hideTimer.Interval = TimeSpan.FromSeconds(v); }));
            Toggle(p, "Timeline previews", "Picture preview when hovering the timeline", S.Previews, () => Change(() => S.Previews = !S.Previews));
        }

        void UpscalePage()
        {
            var gpu = Upscaler.Gpu.Replace("NVIDIA GeForce ", "");
            Section(p, "AI upscaling");
            Toggle(p, "AI upscaling", !Upscaler.Installed ? @"Needs the Haku Upscaler pack in %LOCALAPPDATA%\Haku Upscaler"
                    : Upscaler.Speed <= 0 ? "Needs an NVIDIA RTX graphics card"
                    : $"Redraws videos at twice the size with {(S.UpscaleModel == "adore" ? "Adore" : "AnimeJaNai")}, live on your {gpu}",
                S.Upscale, () => Change(() => { S.Upscale = !S.Upscale; up?.Apply(); }));
            if (!S.Upscale || !Upscaler.Installed || Upscaler.Speed <= 0) Note("Turn it on or off while watching with the sparkle button in the player, or Ctrl+U.");
            if (!Upscaler.Installed || Upscaler.Speed <= 0) { Credits(); return; }
            Section(p, "TV shows & movies");
            Chips(p, "Upscale with", new[] { ("rtx", "RTX Video"), ("anime", "Anime model"), ("off", "Off") }, S.UpscaleLiveAction, v => Change(() => { S.UpscaleLiveAction = v; up?.Apply(); }));
            Note(S.UpscaleLiveAction switch
            {
                "rtx" => "RTX Video Super Resolution is NVIDIA's own upscaler, made for real-life video: it cleans up compression and keeps skin and texture natural. Light on the graphics card. Cartoons (shows IMDb lists as Animation, like South Park) and anime use the anime model below.",
                "anime" => "The anime model on everything: great for cartoons in your TV library, but it makes live action look smooth and painted.",
                _ => "TV shows and movies play as they are; anime still uses the anime model below.",
            });
            Section(p, "Model");
            Chips(p, "Anime model", new[] { ("animejanai", "AnimeJaNai"), ("adore", "Adore") }, S.UpscaleModel, v => Change(() => { S.UpscaleModel = v; up?.Apply(); }));
            Note(S.UpscaleModel == "adore"
                ? "Adore by renarchi, made for 1080p anime: a little slower than Balanced. When your card can't keep up with a video, AnimeJaNai's faster tiers take over. Downloaded (3 MB) from renarchi's GitHub the first time."
                : "AnimeJaNai by the-database: three sizes, from Fast to Quality, in a sharp or soft look.");
            Chips(p, "Speed and quality", new[] { ("auto", "Auto") }.Concat(Upscaler.Tiers.Select(t => (t.Key, t.Name))).ToArray(), S.UpscaleTier, v => Change(() => { S.UpscaleTier = v; up?.Apply(); }));
            var detect = Glass(upscaleDetecting ?? (Upscaler.Measured(Upscaler.Tiers[0]) != null ? "Detect again" : "Detect best for this PC"), "Detect best AI upscaling for this PC", async () =>
            {
                upscaleDetecting = "Starting…";
                BuildGlobalSettings();
                string? err = await Upscaler.Detect(S.UpscaleLook, msg => { upscaleDetecting = msg; DispatcherQueue.TryEnqueue(() => { if (SettingsOpen) BuildGlobalSettings(); }); });
                upscaleDetecting = null;
                if (err == null)
                {
                    S.UpscaleTier = "auto"; S.Save();
                    var best = Upscaler.Auto(1920, 1080, 23.976);
                    Toast(best == null ? "Measured: no tier keeps up with 1080p on this PC" : $"Best for this PC: {best.Name} ({Upscaler.Fps1080(best):0} fps at 1080p)");
                    up?.Apply();
                }
                else Toast($"Couldn't detect: {err}");
                if (SettingsOpen) BuildGlobalSettings();
            });
            detect.IsEnabled = upscaleDetecting == null && !upscalePreparing;
            p.Children.Add(detect);
            Note(upscaleDetecting != null ? "Timing each tier on your graphics card. Pausing the video gives the most accurate result."
                : Upscaler.Measured(Upscaler.Tiers[0]) != null ? "Speeds below were measured on this PC. Auto picks the best tier that keeps up with each video."
                : "Times each tier on your graphics card (about 10 seconds, longer the first time) and sets Auto to use the results.");
            Chips(p, "Look", new[] { ("sharp", "Sharp"), ("soft", "Soft") }, S.UpscaleLook, v => Change(() => { S.UpscaleLook = v; up?.Apply(); }));
            Note(S.UpscaleLook == "soft" ? "Soft: AnimeJaNai V3, cleaner and closer to the source." : "Sharp: AnimeJaNai V3 Sharp1, crisper lines and more detail.");

            Section(p, $"On your {gpu}");
            var auto = Upscaler.Auto(1920, 1080, 23.976);
            var grid = new Grid { Margin = new Thickness(4, 4, 4, 0) };
            foreach (var wdt in new[] { 1.4, 1.6, 1.0, 1.1 }) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(wdt, GridUnitType.Star) });
            void Cell(int r, int c, string text, bool muted = false, bool accent = false, bool bold = false)
            {
                var t = GlassLook.Label(text, 13.5, bold); t.Margin = new Thickness(0, 6, 8, 6);
                if (accent) t.Foreground = ThemeW.Accent; else if (muted) t.Foreground = Muted;
                Grid.SetRow(t, r); Grid.SetColumn(t, c); grid.Children.Add(t);
            }
            grid.RowDefinitions.Add(new RowDefinition());
            Cell(0, 0, "Tier", muted: true); Cell(0, 1, "Model", muted: true); Cell(0, 2, "1080p speed", muted: true); Cell(0, 3, "Status", muted: true);
            int row = 1; bool missing = false; var slow = new List<string>();
            foreach (var t in Upscaler.Tiers)
            {
                grid.RowDefinitions.Add(new RowDefinition());
                double fps = Upscaler.Fps1080(t);
                bool ready = Upscaler.Prepared(t, S.UpscaleLook);
                missing |= !ready;
                Cell(row, 0, t.Name + (auto == t ? "  · Auto" : ""), bold: auto == t);
                Cell(row, 1, t.Size, muted: true);
                bool keepsUp = fps >= 23.976 * (t.Key == "adore" ? 1.05 : 1.15);
                Cell(row, 2, $"{(Upscaler.Measured(t) != null ? "" : "~")}{fps:0} fps", muted: !keepsUp);
                if (!keepsUp) slow.Add(t.Name);
                Cell(row, 3, ready ? "Ready" : "Not prepared", accent: ready, muted: !ready);
                row++;
            }
            p.Children.Add(new Border { Child = grid, CornerRadius = new CornerRadius(16), Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 2, 0, 6), BorderThickness = new Thickness(1), Background = new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)) });
            if (slow.Count > 0) Note($"{string.Join(" and ", slow)} can't keep up with 1080p at 24 fps on this card. Auto still uses {(slow.Count == 1 ? "it" : "them")} for smaller videos when {(slow.Count == 1 ? "it does" : "they do")}.");
            if (missing)
            {
                var prep = Glass(upscalePreparing ? "Preparing…" : "Prepare all now", "Prepare AI upscaling models", async () =>
                {
                    upscalePreparing = true; BuildGlobalSettings();
                    string? err = null;
                    foreach (var t in Upscaler.Tiers) err ??= await Upscaler.Prepare(Upscaler.ModelFile(t, S.UpscaleLook));
                    upscalePreparing = false;
                    Toast(err == null ? "AI upscaling models ready" : $"Couldn't prepare a model: {err}");
                    if (SettingsOpen) BuildGlobalSettings();
                });
                prep.IsEnabled = !upscalePreparing && upscaleDetecting == null;
                p.Children.Add(prep);
                Note(S.UpscaleModel == "adore" ? "Each model is prepared for your graphics card once (about 15 seconds; Adore a minute or two, after a 3 MB download). Otherwise it happens the first time it's used."
                    : "Each model is prepared for your graphics card once (about 15 seconds). Otherwise it happens the first time it's used.");
            }
            Section(p, "When it runs");
            Note("• Videos up to 2560×1728 are drawn at twice the size (a 4K file plays as it is).\n" +
                 "• SDR video, decoded on the graphics card. HDR plays without upscaling for now.\n" +
                 "• Subtitles, the controls and the glass are drawn after upscaling, so they stay sharp.\n" +
                 "• If frames start dropping, it turns itself off for that video. The sparkle button in the player shows what it's doing; Ctrl+U turns it on or off.");
            Credits();
            void Credits() => Note("Models: AnimeJaNai by the-database and Adore by renarchi (both CC BY-NC-SA 4.0). Runs on NVIDIA TensorRT.");
        }

        void AudioPage()
        {
            foreach (var (kind, title) in new[] { ("anime", "Anime"), ("tv", "TV shows & movies") })
            {
                var pref = S.LangFor(kind);
                Section(p, title);
                Chips(p, "Audio language", new[] { ("jpn", "Japanese"), ("eng", "English"), ("", "File default") }, pref.Audio, v => Change(() => S.SetLang(kind, audio: v)));
                Toggle(p, "Show subtitles", null, pref.On, () => Change(() => S.SetLang(kind, on: !pref.On)));
                if (pref.On) Chips(p, "Subtitle language", new[] { ("eng", "English"), ("jpn", "Japanese"), ("", "File default") }, pref.Subs, v => Change(() => S.SetLang(kind, subs: v)));
            }
            Section(p, "All subtitles");
            Toggle(p, "Prefer full subtitles", "Picks the full dialogue track over “Signs & Songs” or forced tracks", S.FullSubs, () => Change(() => S.FullSubs = !S.FullSubs));
            Chips(p, "Subtitle size", new[] { (0.8, "S"), (1.0, "M"), (1.3, "L") }, S.SubScale, v => Change(() => S.SubScale = v));
            Note("These are the defaults for each kind of video. You can still switch tracks, add your own subtitle files and fix their timing from the player.");
        }

        void AdvancedPage()
        {
            Section(p, "Troubleshooting");
            Toggle(p, "Debug info", "Shows live playback details (codec, frame rate, dropped frames…) over the video", S.Debug, () => Change(() => S.Debug = !S.Debug));
            Section(p, "Discord");
            Toggle(p, "Show what I'm watching on Discord", "Your profile shows the show, episode and time left while Haku Player plays", S.DiscordStatus, () => Change(() => { S.DiscordStatus = !S.DiscordStatus; if (!S.DiscordStatus) Discord.Clear(); }));
            if (S.DiscordStatus)
            {
                var box = new TextBox { Text = S.DiscordAppId, Width = 260 };
                AutomationProperties.SetName(box, "Discord application ID");
                box.TextChanged += (_, _) => { S.DiscordAppId = box.Text.Trim(); S.Save(); };
                p.Children.Add(Row(box, Label("Application ID", "From discord.com/developers/applications")));
                Note("One-time setup: on that page, click New Application, name it “Haku Player” (that name is what Discord shows), and paste its Application ID here. Discord must be open on this PC.");
            }
            Section(p, "Storage");
            long bytes = 0;
            try { bytes = Directory.Exists(ThumbDir) ? new DirectoryInfo(ThumbDir).EnumerateFiles().Sum(f => f.Length) : 0; } catch { }
            p.Children.Add(Row(Glass("Clear", "Clear preview cache", () => { try { foreach (var f in Directory.EnumerateFiles(ThumbDir)) File.Delete(f); } catch { } BuildGlobalSettings(); }),
                Label("Timeline preview cache", $"{bytes / 1048576.0:0.#} MB · rebuilt automatically when needed")));
            var about = GlassLook.Label("Haku Player · plays your files at their original quality", 12); about.Foreground = Muted; about.HorizontalAlignment = HorizontalAlignment.Center; about.Margin = new Thickness(4, 22, 0, 4);
            p.Children.Add(about);
        }
    }

    // how much of the desktop shows through the window (Glass clarity); solid black while playing
    void ApplyGlassTint() => tint.Background = new SolidColorBrush(core != null ? Colors.Black : Color.FromArgb(GlassAlpha, 0x0A, 0x0B, 0x10));

    // what a newly added folder probably holds (you can change it with the chips), as the WPF app
    static string GuessKind(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('\\'));
        bool Has(string sub) => Directory.Exists(Path.Combine(path, sub));
        bool Like(string pattern) => System.Text.RegularExpressions.Regex.IsMatch(name, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (Like("opening|ending|themes") || (Has("Anime Openings") && !Has("Anime Shows"))) return "themes";
        if (Has("Anime Shows") || Like("anime")) return "anime";
        return Like("movie|film") ? "movie" : "tv";
    }

    // ---------- the stats page ----------

    void StatsPage(StackPanel p)
    {
        var shows = Everything(lib).Where(e => e.Unit != "themes").ToList();
        var ops = Everything(lib).Where(e => e.Unit == "themes").ToList();
        var files = shows.Concat(ops).SelectMany(e => e.Videos).Select(v => v.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        MeasureLibrary(files);
        var cache = Durations();
        double runtime = 0; long bytes = 0;
        foreach (var f in files) if (cache.TryGetValue(f, out var pr)) { runtime += pr.Dur; bytes += pr.Size; }
        string Pending(string s) => probing ? $"{s} so far · measuring {probeDone} of {probeTotal}…" : s;
        void Stat(string label, string value) => p.Children.Add(Row(GlassLook.Label(value, 15, bold: true), Label(label, null)));
        Section(p, "Library");
        int anime = shows.Count(e => e.Kind == "anime"), tv = shows.Count(e => e.Kind == "tv"), movies = shows.Count(e => e.Kind == "movie");
        Stat("Anime", $"{anime} show{(anime == 1 ? "" : "s")} · {shows.Where(e => e.Kind == "anime").Sum(e => e.Videos.Count)} episodes");
        Stat("TV shows", $"{tv} show{(tv == 1 ? "" : "s")} · {shows.Where(e => e.Kind == "tv").Sum(e => e.Videos.Count(v => v.Season >= 0))} episodes");
        Stat("Movies", $"{movies}");
        Stat("Openings & endings", $"{ops.Sum(e => e.Videos.Count)} from {ops.Count} series");
        Stat("Total running time", Pending(Hours(runtime)));
        Stat("Size on disk", Pending(Size(bytes)));
        Section(p, "Watching");
        var recs = Watch.Data.Files;
        double total = Watch.Data.Days.Values.Sum();
        double week = Enumerable.Range(0, 7).Sum(i => Watch.Data.Days.GetValueOrDefault(DateTime.Now.AddDays(-i).ToString("yyyy-MM-dd")));
        var movieFiles = shows.Where(e => e.Kind == "movie").SelectMany(e => e.Videos).Select(v => v.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Stat("Time watched", total > 0 ? Hours(total) : "Nothing yet");
        Stat("This week", Hours(week));
        Stat("Episodes watched", $"{recs.Count(r => r.Value.Done && !movieFiles.Contains(r.Key))}");
        Stat("Movies watched", $"{recs.Count(r => r.Value.Done && movieFiles.Contains(r.Key))}");
        Stat("Favourites", $"{S.Favorites.Count} shows · {S.FavoriteVideos.Count} videos");
        Section(p, "Last 14 days");
        var days = Enumerable.Range(0, 14).Select(i => DateTime.Now.Date.AddDays(i - 13)).ToList();
        double max = Math.Max(1800, days.Max(d => Watch.Data.Days.GetValueOrDefault(d.ToString("yyyy-MM-dd"))));
        var chart = new Grid { Height = 120, Margin = new Thickness(4, 4, 4, 0) };
        for (int i = 0; i < days.Count; i++)
        {
            chart.ColumnDefinitions.Add(new ColumnDefinition());
            double secs = Watch.Data.Days.GetValueOrDefault(days[i].ToString("yyyy-MM-dd"));
            var col = new Grid { Margin = new Thickness(3, 0, 3, 0) };
            col.RowDefinitions.Add(new RowDefinition()); col.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            ToolTipService.SetToolTip(col, $"{days[i]:ddd d MMM} · {(secs > 0 ? Hours(secs) : "nothing")}");
            col.Children.Add(new Border { VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = new CornerRadius(5), Height = Math.Max(3, 96 * secs / max), Background = secs > 0 ? ThemeW.Grad : new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)) });
            var day = GlassLook.Label(days[i].ToString("ddd")[..2], 11); day.Foreground = Muted; day.HorizontalAlignment = HorizontalAlignment.Center; day.Margin = new Thickness(0, 4, 0, 0);
            Grid.SetRow(day, 1); col.Children.Add(day);
            Grid.SetColumn(col, i); chart.Children.Add(col);
        }
        p.Children.Add(chart);
        var byShow = shows.Select(e => (Name: e.Title, Secs: e.Videos.Sum(v => Watch.Get(v.Path)?.Seconds ?? 0))).Where(x => x.Secs > 60).OrderByDescending(x => x.Secs).Take(5).ToList();
        if (byShow.Count > 0)
        {
            Section(p, "Most watched");
            double top = byShow[0].Secs;
            foreach (var (name, secs) in byShow)
            {
                var row = new Grid { Margin = new Thickness(4, 4, 4, 6) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
                var n = GlassLook.Label(name, 14); n.TextTrimming = TextTrimming.CharacterEllipsis;
                row.Children.Add(n);
                var bar = new Border { Height = 8, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left, Background = ThemeW.Grad, Margin = new Thickness(10, 0, 10, 0) };
                double share = secs / top;
                row.SizeChanged += (_, _) => bar.Width = Math.Max(6, (row.ActualWidth - 290) * share);
                Grid.SetColumn(bar, 1); row.Children.Add(bar);
                var t = GlassLook.Label(Hours(secs), 13); t.Foreground = Muted; t.TextAlignment = TextAlignment.Right; t.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetColumn(t, 2); row.Children.Add(t);
                p.Children.Add(row);
            }
        }
        var note = GlassLook.Label("Watch time is counted from now on (earlier viewing wasn't recorded). Running times are read from the files once and remembered.", 12.5);
        note.Foreground = Muted; note.TextWrapping = TextWrapping.Wrap; note.Margin = new Thickness(4, 16, 0, 0);
        p.Children.Add(note);
    }

    // Measures whatever isn't cached yet, in the background; the stats page refreshes as it goes.
    async void MeasureLibrary(List<string> files)
    {
        if (probing) return;
        var cache = Durations();
        var todo = new List<(string Path, long Size, long Stamp)>();
        foreach (var f in files)
        {
            try { var fi = new FileInfo(f); if (!cache.TryGetValue(f, out var pr) || pr.Size != fi.Length || pr.Stamp != fi.LastWriteTimeUtc.Ticks) todo.Add((f, fi.Length, fi.LastWriteTimeUtc.Ticks)); }
            catch { }
        }
        if (todo.Count == 0) return;
        probing = true; probeDone = 0; probeTotal = todo.Count;
        try { await MeasureDurations(todo, () => { if (SettingsOpen && settingsPage == "Stats") BuildGlobalSettings(); }); }
        catch { }
        finally { probing = false; if (SettingsOpen && settingsPage == "Stats") BuildGlobalSettings(); }
    }

    // ---------- little builders (as the WPF app's) ----------

    static void Section(StackPanel p, string title)
    {
        var t = GlassLook.Label(title.ToUpperInvariant(), 12, bold: true); t.Opacity = 0.55; t.Margin = new Thickness(4, p.Children.Count == 0 ? 0 : 22, 0, 8);
        p.Children.Add(t);
    }

    static Grid Row(UIElement right, UIElement left)
    {
        var g = new Grid { Margin = new Thickness(4, 6, 4, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (right is FrameworkElement fr) fr.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn((FrameworkElement)right, 1);
        g.Children.Add(left); g.Children.Add(right);
        return g;
    }

    static StackPanel Label(string title, string? sub)
    {
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var t = GlassLook.Label(title, 15); t.TextWrapping = TextWrapping.Wrap;
        sp.Children.Add(t);
        if (sub != null) { var s = GlassLook.Label(sub, 12.5); s.Foreground = Muted; s.Margin = new Thickness(0, 2, 0, 0); s.TextWrapping = TextWrapping.Wrap; sp.Children.Add(s); }
        return sp;
    }

    static UIElement Plus(string text)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9 };
        sp.Children.Add(GlassLook.Icon("", 12)); sp.Children.Add(GlassLook.Label(text, 14));
        return sp;
    }

    static Button Chip(string text, bool on, string name, Action click)
    {
        var b = GlassLook.Chip(text, on, click, 13, name);
        b.Margin = new Thickness(0, 0, 6, 6);
        return b;
    }

    // the rainbow bar: dragging recolours the app live; the page is rebuilt (and saved) when the drag ends
    UIElement HueBar(TextBox hex)
    {
        const double W = 320, Knob = 22;
        var current = ThemeW.Parse(S.CustomColor) ?? Color.FromArgb(0xFF, 0x2B, 0xA8, 0xFF);
        var rainbow = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 0) };
        for (int i = 0; i <= 6; i++) rainbow.GradientStops.Add(new GradientStop { Color = ThemeW.FromHsl(i * 60 % 360, 0.85, 0.58), Offset = i / 6.0 });
        var bar = new Border { Width = W, Height = 28, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Background = rainbow, BorderBrush = GlassLook.GlassStroke() };
        var knob = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = Knob, Height = Knob, Stroke = new SolidColorBrush(Colors.White), StrokeThickness = 3, Fill = new SolidColorBrush(current),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        void PlaceKnob(double hue) => knob.Margin = new Thickness(3 + hue / 360 * (W - Knob - 6), 0, 0, 0);
        PlaceKnob(ThemeW.Hsl(current).H);
        AutomationProperties.SetName(bar, "Custom colour hue");
        void Pick(double x)
        {
            double hue = Math.Clamp((x - 3 - Knob / 2) / (W - Knob - 6), 0, 1) * 360;
            var c = ThemeW.FromHsl(hue % 360, 0.85, 0.58);
            S.CustomColor = hex.Text = ThemeW.Hex(c);
            ((SolidColorBrush)knob.Fill).Color = c; PlaceKnob(hue);
            ThemeW.Apply("custom");
        }
        bool dragging = false;
        bar.PointerPressed += (_, e) => { dragging = true; bar.CapturePointer(e.Pointer); Pick(e.GetCurrentPoint(bar).Position.X); e.Handled = true; };
        bar.PointerMoved += (_, e) => { if (dragging) Pick(e.GetCurrentPoint(bar).Position.X); };
        bar.PointerReleased += (_, e) => { if (!dragging) return; dragging = false; bar.ReleasePointerCapture(e.Pointer); S.Save(); UpdateLogos(); BuildGlobalSettings(); };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Grid { Width = W, VerticalAlignment = VerticalAlignment.Center, Children = { bar, knob } });
        hex.Margin = new Thickness(14, 0, 0, 0);
        row.Children.Add(hex);
        return row;
    }

    static Button Tip(Button b, string tip) { ToolTipService.SetToolTip(b, tip); return b; }

    static Button IconBtn(string glyph, string name, bool enabled, Action click)
    {
        var b = GlassLook.Flat(GlassLook.Icon(glyph, 14), name, click, 34);
        b.IsEnabled = enabled; b.Opacity = enabled ? 1 : 0.3; b.Margin = new Thickness(4, 0, 0, 0);
        return b;
    }

    // a switch on the right of a row (child: indented under its parent, with a guide line)
    void Toggle(StackPanel p, string title, string? sub, bool on, Action flip, bool child = false)
    {
        if (child)
        {
            var inner = new StackPanel();
            p.Children.Add(new Border { Child = inner, Margin = new Thickness(12, -4, 0, 4), Padding = new Thickness(16, 0, 0, 0), BorderThickness = new Thickness(2, 0, 0, 0), BorderBrush = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)) });
            p = inner;
        }
        var knob = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = new SolidColorBrush(Colors.White), HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left, Margin = new Thickness(3) };
        var track = new Border { Width = 50, Height = 28, CornerRadius = new CornerRadius(14), Child = knob, Background = on ? ThemeW.Grad : new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)) };
        var b = GlassLook.Flat(track, title, flip);
        b.Padding = new Thickness(0);
        AutomationProperties.SetHelpText(b, on ? "on" : "off");
        p.Children.Add(Row(b, Label(title, sub)));
    }

    void Chips<T>(StackPanel p, string title, (T Value, string Name)[] options, T current, Action<T> pick)
    {
        var chips = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (value, name) in options) { var c = Chip(name, Equals(value, current), $"{title}: {name}", () => pick(value)); c.Margin = new Thickness(4, 0, 0, 0); chips.Children.Add(c); }
        p.Children.Add(Row(chips, Label(title, null)));
    }
}
