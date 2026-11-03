using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AnimePlayer;

// App-wide settings window (gear in the main top bar). Player quick settings live in the player's own menu;
// both read and write the same Settings, so they always agree.
public partial class MainWindow
{
    static Settings S => Settings.Current;
    static readonly string ThumbDir = Path.Combine(Settings.DataDir, "thumbs");

    GlassSurface? settingsGlass;
    LibraryFolder? tagEditor;          // folder whose "+ Tag" editor is open
    string newTagName = "", newTagKind = "tv";

    // what a newly added folder probably holds (you can change it with the chips)
    static string GuessKind(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('\\'));
        bool Has(string sub) => Directory.Exists(Path.Combine(path, sub));
        bool Like(string pattern) => System.Text.RegularExpressions.Regex.IsMatch(name, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (Like("opening|ending|themes") || (Has("Anime Openings") && !Has("Anime Shows"))) return "themes";
        if (Has("Anime Shows") || Like("anime")) return "anime";
        return Like("movie|film") ? "movie" : "tv";
    }

    void OnOpenSettings(object s, RoutedEventArgs e)
    {
        settingsGlass ??= new GlassSurface(SettingsCard, 28);
        BuildGlobalSettings();
        // blur the page behind only while open: a blur left on (even at 0) would slow scrolling
        var blur = new System.Windows.Media.Effects.BlurEffect { Radius = 0, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance };
        Content.Effect = blur;
        blur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, new DoubleAnimation(36, TimeSpan.FromMilliseconds(260)));
        SettingsLayer.Visibility = Visibility.Visible;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        SettingsLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        SettingsScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        SettingsScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
    }

    void CloseSettings()
    {
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
        fade.Completed += (_, _) => { SettingsLayer.Visibility = Visibility.Collapsed; Content.Effect = null; };
        (Content.Effect as System.Windows.Media.Effects.BlurEffect)?.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)));
        SettingsLayer.BeginAnimation(OpacityProperty, fade);
    }

    void OnCloseSettings(object s, RoutedEventArgs e) => CloseSettings();
    void OnSettingsBackdrop(object s, System.Windows.Input.MouseButtonEventArgs e) { if (e.OriginalSource == SettingsLayer) CloseSettings(); }

    string settingsPage = "Library";
    bool upscalePreparing; // "Prepare all now" is running
    string? upscaleDetecting; // "Detect best for this PC" is running: its progress text
    static readonly (string Key, string Icon, string Name)[] SettingsPages =
    {
        ("Library", "", "Library"),
        ("Appearance", "", "Appearance"),
        ("Playback", "", "Playback"),
        ("Upscale", "", "AI upscaling"),
        ("Audio", "", "Audio & subtitles"),
        ("Stats", "", "Stats"),
        ("Advanced", "", "Advanced"),
    };

    // Sidebar of sections; the chosen one is highlighted.
    void BuildSettingsNav()
    {
        SettingsNav.Children.Clear();
        foreach (var (key, icon, name) in SettingsPages)
        {
            bool on = key == settingsPage;
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = icon, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16, Width = 30, VerticalAlignment = VerticalAlignment.Center,
                Foreground = on ? (Brush)FindResource("Accent") : (Brush)FindResource("Muted") });
            row.Children.Add(new TextBlock { Text = name, FontSize = 15, VerticalAlignment = VerticalAlignment.Center, FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal, Opacity = on ? 1 : 0.8 });
            var item = new Border { Child = row, CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 11, 14, 11), Margin = new Thickness(0, 0, 0, 4),
                Background = on ? new SolidColorBrush(Color.FromArgb(0x26, 255, 255, 255)) : Brushes.Transparent };
            var b = new Button { Style = (Style)FindResource("Flat"), Content = item, HorizontalContentAlignment = HorizontalAlignment.Stretch, Foreground = (Brush)FindResource("Text") };
            System.Windows.Automation.AutomationProperties.SetName(b, $"Settings: {name}");
            b.Click += (_, _) => { settingsPage = key; SettingsScroll.ScrollToTop(); BuildGlobalSettings(); };
            SettingsNav.Children.Add(b);
        }
    }

    void BuildGlobalSettings()
    {
        BuildSettingsNav();
        var p = SettingsItems;
        p.Children.Clear();
        void Change(Action a) { a(); S.Save(); BuildGlobalSettings(); }
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
        async Task Rescan() { S.Save(); BuildGlobalSettings(); await RescanLibraries(); }
        void Reorder() { S.Save(); BuildGlobalSettings(); BuildTiles(); }
        Brush line = new SolidColorBrush(Color.FromArgb(0x22, 255, 255, 255));
        Button Icon(string glyph, string name, string tip, Action click, bool enabled = true)
        {
            var b = new Button { Style = (Style)FindResource("Caption"), Content = glyph, FontSize = 14, ToolTip = tip, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0), IsEnabled = enabled, Opacity = enabled ? 1 : 0.3 };
            System.Windows.Automation.AutomationProperties.SetName(b, name);
            b.Click += (_, _) => click();
            return b;
        }
        Button Chip(string text, bool on, string name, Action click)
        {
            var c = new Button { Style = (Style)FindResource("Chip"), Content = text, Tag = on ? "On" : null, Foreground = (Brush)FindResource("Text"), Margin = new Thickness(0, 0, 6, 6) };
            System.Windows.Automation.AutomationProperties.SetName(c, name);
            c.Click += (_, _) => click();
            return c;
        }
        Border Field(TextBox box) => new() { Child = box, CornerRadius = new CornerRadius(16), Background = (Brush)FindResource("GlassFill"), BorderThickness = new Thickness(1),
            BorderBrush = (Brush)FindResource("GlassStroke"), VerticalAlignment = VerticalAlignment.Center };
        UIElement Plus(string text) => new StackPanel { Orientation = Orientation.Horizontal, Children =
        {
            new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0) },
            new TextBlock { Text = text },
        } };

        Section(p, "Libraries");
        for (int i = 0; i < S.Libs.Count; i++)
        {
            var l = S.Libs[i]; int at = i;
            var inner = new StackPanel();
            p.Children.Add(new Border { Child = inner, CornerRadius = new CornerRadius(16), Padding = new Thickness(16, 12, 12, 12), Margin = new Thickness(0, 0, 0, 10),
                Background = new SolidColorBrush(Color.FromArgb(0x16, 255, 255, 255)), BorderThickness = new Thickness(1), BorderBrush = line });

            // name (type to rename), On home, move up / down, delete
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var home = Chip("On home", l.Home, $"{l.Name}: on home", () => { l.Home = !l.Home; Reorder(); });
            home.Margin = new Thickness(0, 0, 6, 0);
            home.ToolTip = "Show this library as a tile on the home page";
            tools.Children.Add(home);
            tools.Children.Add(Icon("", $"Move {l.Name} up", "Move up (earlier on the home page)", () => { S.Libs.RemoveAt(at); S.Libs.Insert(at - 1, l); Reorder(); }, at > 0));
            tools.Children.Add(Icon("", $"Move {l.Name} down", "Move down (later on the home page)", () => { S.Libs.RemoveAt(at); S.Libs.Insert(at + 1, l); Reorder(); }, at < S.Libs.Count - 1));
            tools.Children.Add(Icon("", $"Delete library {l.Name}", "Delete this library (your files are not touched)", async () => { S.Libs.Remove(l); await Rescan(); }));
            DockPanel.SetDock(tools, Dock.Right);
            head.Children.Add(tools);
            var name = new TextBox { Text = l.Name, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = (Brush)FindResource("Text"), CaretBrush = Brushes.White,
                FontSize = 19, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Library name: click to rename" };
            System.Windows.Automation.AutomationProperties.SetName(name, "Library name");
            name.TextChanged += (_, _) => { l.Name = name.Text.Trim().Length > 0 ? name.Text.Trim() : "Library"; S.Save(); };
            name.LostFocus += (_, _) => BuildTiles();
            name.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { BuildTiles(); System.Windows.Input.Keyboard.ClearFocus(); } };
            head.Children.Add(name);
            inner.Children.Add(head);

            if (l.Folders.Count == 0)
                inner.Children.Add(new TextBlock { Text = "No folders yet. Add one, then choose what's in it.", FontSize = 13, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 2, 0, 8) });
            foreach (var f in l.Folders.ToList())
            {
                // a folder: where it is, remove; then its tag (built-in kinds, your own tags, + Tag)
                var box = new StackPanel { Margin = new Thickness(0, 6, 0, 4) };
                var row = new DockPanel();
                var remove = Icon("", $"Remove folder {f.Path}", "Remove this folder from the library (your files are not touched)", async () => { l.Folders.Remove(f); await Rescan(); });
                DockPanel.SetDock(remove, Dock.Right);
                row.Children.Add(remove);
                bool here = Directory.Exists(f.Path);
                string what = f.Kind switch
                {
                    "tv" => "A folder of TV show folders",
                    "movie" => "A folder of movies",
                    "themes" => "Opening and ending folders, one per show",
                    _ => "A folder of anime show folders",
                };
                row.Children.Add(Label(f.Path, !here ? "Not found: drive unplugged or folder moved" : f.Tag != null ? $"{what} · read like {LibraryFolder.KindName(f.Kind)}" : what));
                box.Children.Add(row);
                var tags = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
                foreach (var (k, label) in LibraryFolder.Kinds)
                    tags.Children.Add(Chip(label, f.Tag == null && f.Kind == k, $"{f.Path}: {label}", async () => { f.Kind = k; f.Tag = null; await Rescan(); }));
                foreach (var t in S.Tags.ToList())
                    tags.Children.Add(Chip(t.Name, f.Tag == t.Name, $"{f.Path}: {t.Name}", async () => { f.Kind = t.Kind; f.Tag = t.Name; await Rescan(); }));
                tags.Children.Add(Chip("+ Tag", tagEditor == f, $"{f.Path}: new tag", () => { tagEditor = tagEditor == f ? null : f; BuildGlobalSettings(); }));
                box.Children.Add(tags);
                if (tagEditor == f) box.Children.Add(TagEditor(f));
                inner.Children.Add(box);
            }

            var addFolder = new Button { Style = (Style)FindResource("Glass"), Padding = new Thickness(14, 6, 16, 6), FontSize = 13.5, Margin = new Thickness(0, 6, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left, Content = Plus("Add folder") };
            System.Windows.Automation.AutomationProperties.SetName(addFolder, $"Add folder to {l.Name}");
            addFolder.Click += async (_, _) =>
            {
                using var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = $"Choose a folder for {l.Name}", UseDescriptionForTitle = true };
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                var path = dlg.SelectedPath;
                if (l.Folders.Any(x => string.Equals(x.Path.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) { Toast("That folder is already in this library"); return; }
                l.Folders.Add(new LibraryFolder { Path = path, Kind = GuessKind(path) }); // best guess; change it with the chips
                await Rescan();
            };
            inner.Children.Add(addFolder);
        }

        var add = new Button { Style = (Style)FindResource("Glass"), Padding = new Thickness(16, 8, 18, 8), FontSize = 14, Margin = new Thickness(0, 4, 10, 0), Content = Plus("New library") };
        System.Windows.Automation.AutomationProperties.SetName(add, "New library");
        add.Click += (_, _) =>
        {
            S.Libs.Add(new MediaLibrary());
            lib[S.Libs[^1].Id] = new();
            Reorder();
            Dispatcher.BeginInvoke(() => SettingsScroll.ScrollToEnd(), System.Windows.Threading.DispatcherPriority.Loaded);
        };
        var sync = new Button { Style = (Style)FindResource("Glass"), Content = "Sync now", Padding = new Thickness(16, 8, 16, 8), FontSize = 14, Margin = new Thickness(0, 4, 0, 0) };
        sync.Click += async (_, _) => await SyncLibrary(true);
        System.Windows.Automation.AutomationProperties.SetName(sync, "Sync now");
        p.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { add, sync } });

        p.Children.Add(new TextBlock { Text = "Each library is a tile on the home page; drag the tiles there to move them. A library can hold any mix of folders, and their tags become filters inside it. Folders can be laid out any way: shows inside other folders (A–Z, genres…), season folders, or loose files. New files are picked up automatically while Haku Player is open.",
            FontSize = 12.5, Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 14, 0, 0) });
        p.Children.Add(new TextBlock { Text = "Details come from AniList for anime and Cinemeta (IMDb) for TV shows and movies. Your own tags get the details of the kind they're read like.", FontSize = 12.5,
            Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 2, 0, 0) });

        // new tag for a folder: its name, and which kind of folder it's read like
        UIElement TagEditor(LibraryFolder f)
        {
            var sp = new StackPanel { Margin = new Thickness(12, 2, 0, 6) };
            var nameBox = new TextBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = (Brush)FindResource("Text"), CaretBrush = Brushes.White,
                FontSize = 14, Padding = new Thickness(14, 7, 14, 7), Width = 220, Text = newTagName };
            System.Windows.Automation.AutomationProperties.SetName(nameBox, "New tag name");
            nameBox.TextChanged += (_, _) => newTagName = nameBox.Text;
            var create = new Button { Style = (Style)FindResource("Glass"), Content = "Add tag", Padding = new Thickness(14, 6, 14, 6), FontSize = 13.5, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetName(create, "Add tag");
            create.Click += async (_, _) =>
            {
                var n = newTagName.Trim();
                if (n.Length == 0) { Toast("Give the tag a name"); return; }
                if (LibraryFolder.Kinds.Any(k => k.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) || S.Tags.Any(t => t.Name.Equals(n, StringComparison.OrdinalIgnoreCase))) { Toast("There's already a tag with that name"); return; }
                S.Tags.Add(new TagDef { Name = n, Kind = newTagKind });
                f.Tag = n; f.Kind = newTagKind;
                tagEditor = null; newTagName = "";
                await Rescan();
            };
            sp.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { Field(nameBox), create } });
            var like = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            like.Children.Add(new TextBlock { Text = "Read like", FontSize = 13, Foreground = (Brush)FindResource("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 10, 6) });
            foreach (var (k, label) in LibraryFolder.Kinds)
                like.Children.Add(Chip(label, newTagKind == k, $"New tag read like {label}", () => { newTagKind = k; BuildGlobalSettings(); }));
            sp.Children.Add(like);
            Dispatcher.BeginInvoke(() => nameBox.Focus(), System.Windows.Threading.DispatcherPriority.Loaded);
            return sp;
        }
        }

        void AppearancePage()
        {
        Section(p, "Theme");
        var swatches = new WrapPanel { Margin = new Thickness(0, 2, 0, 4) };
        foreach (var (key, name) in Theme.All)
        {
            var (a, b, c) = Theme.Colors(key);
            bool on = S.Theme == key;
            var dot = new Border
            {
                Width = 44, Height = 44, CornerRadius = new CornerRadius(22), HorizontalAlignment = HorizontalAlignment.Center,
                Background = new LinearGradientBrush(new GradientStopCollection { new(a, 0), new(b, 0.55), new(c, 1) }, new Point(0, 0), new Point(1, 1)),
                BorderThickness = new Thickness(on ? 3 : 1), BorderBrush = on ? Brushes.White : new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)),
            };
            var sp = new StackPanel { Width = 92 };
            sp.Children.Add(dot);
            sp.Children.Add(new TextBlock { Text = name, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0),
                FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal, Opacity = on ? 1 : 0.75 });
            var btn = new Button { Style = (Style)FindResource("Flat"), Content = sp, Margin = new Thickness(0, 4, 8, 4) };
            System.Windows.Automation.AutomationProperties.SetName(btn, $"Theme: {name}");
            btn.Click += (_, _) => Change(() => { S.Theme = key; Theme.Apply(key); MakeBlobs(); ApplyAppearance(); });
            swatches.Children.Add(btn);
        }
        p.Children.Add(swatches);
        if (S.Theme == "custom") p.Children.Add(CustomColorPicker());
        Section(p, "Glass");
        Chips(p, "Glass clarity", new[] { (0, "Clear"), (1, "Balanced"), (2, "Frosted") }, S.Glass, v => Change(() => { S.Glass = v; ApplyGlass(); }));

        Section(p, "Background");
        Toggle(p, "Moving background", "Slow colour glows drifting behind the glass", S.MovingBackground, () => Change(() =>
        {
            S.MovingBackground = !S.MovingBackground;
            ApplyAppearance();
        }));
        if (S.MovingBackground) // only matters while the main switch is on
            Toggle(p, "Keep moving when not in focus", "While another app is in front. Off saves CPU", S.MovingAlways, () => Change(() =>
            {
                S.MovingAlways = !S.MovingAlways;
                UpdateBlobs();
            }), child: true);
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
        Chips(p, "Hide controls after", new[] { (2.0, "2s"), (3.0, "3s"), (5.0, "5s") }, S.HideAfter, v => Change(() => S.HideAfter = v));
        Toggle(p, "Timeline previews", "Picture preview when hovering the timeline", S.Previews, () => Change(() => S.Previews = !S.Previews));
        }

        void UpscalePage()
        {
        var gpu = Upscaler.Gpu.Replace("NVIDIA GeForce ", "");
        Section(p, "AI upscaling");
        Toggle(p, "AI upscaling", !Upscaler.Installed ? @"Needs the Haku Upscaler pack in %LOCALAPPDATA%\Haku Upscaler"
                : Upscaler.Speed <= 0 ? "Needs an NVIDIA RTX graphics card"
                : $"Redraws videos at twice the size with AnimeJaNai, live on your {gpu}",
            S.Upscale, () => Change(() => { S.Upscale = !S.Upscale; ApplyUpscaleSetting(); }));
        if (!S.Upscale || !Upscaler.Installed || Upscaler.Speed <= 0)
        {
            Note("Turn it on or off while watching with the sparkle button in the player, or Ctrl+U.");
            Credits();
            return;
        }

        Section(p, "TV shows & movies");
        Chips(p, "Upscale with", new[] { ("rtx", "RTX Video"), ("anime", "Anime model"), ("off", "Off") }, S.UpscaleLiveAction,
            v => Change(() => { S.UpscaleLiveAction = v; ApplyUpscaleSetting(); }));
        Note(S.UpscaleLiveAction switch
        {
            "rtx" => "RTX Video Super Resolution is NVIDIA's own upscaler, made for real-life video: it cleans up compression and keeps skin and texture natural. Light on the graphics card. Cartoons (shows IMDb lists as Animation, like South Park) and anime use the anime model below.",
            "anime" => "The anime model on everything: great for cartoons in your TV library, but it makes live action look smooth and painted.",
            _ => "TV shows and movies play as they are; anime still uses the anime model below.",
        });

        Section(p, "Model");
        Chips(p, "Speed and quality", new[] { ("auto", "Auto"), ("fast", "Fast"), ("balanced", "Balanced"), ("quality", "Quality") }, S.UpscaleTier,
            v => Change(() => { S.UpscaleTier = v; ApplyUpscaleSetting(); }));
        var detect = new Button { Style = (Style)FindResource("Glass"), Padding = new Thickness(16, 8, 16, 8), FontSize = 14, Margin = new Thickness(4, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = upscaleDetecting == null && !upscalePreparing,
            Content = upscaleDetecting ?? (Upscaler.Measured(Upscaler.Tiers[0]) != null ? "Detect again" : "Detect best for this PC") };
        System.Windows.Automation.AutomationProperties.SetName(detect, "Detect best AI upscaling for this PC");
        detect.Click += async (_, _) =>
        {
            upscaleDetecting = "Starting…";
            UpdateBlobs(); // still background while measuring, so it doesn't share the GPU
            BuildGlobalSettings();
            string? err = await Upscaler.Detect(S.UpscaleLook, msg => { upscaleDetecting = msg; if (SettingsLayer.IsVisible) BuildGlobalSettings(); });
            upscaleDetecting = null;
            UpdateBlobs();
            if (err == null)
            {
                S.UpscaleTier = "auto"; // Auto now picks from the measured speeds
                S.Save();
                var best = Upscaler.Auto(1920, 1080, 23.976);
                Toast(best == null ? "Measured: no tier keeps up with 1080p on this PC" : $"Best for this PC: {best.Name} ({Upscaler.Fps1080(best):0} fps at 1080p)");
                ApplyUpscaleSetting();
            }
            else Toast($"Couldn't detect: {err}");
            if (SettingsLayer.IsVisible) BuildGlobalSettings();
        };
        p.Children.Add(detect);
        Note(upscaleDetecting != null ? "Timing each tier on your graphics card. Pausing the video gives the most accurate result."
            : Upscaler.Measured(Upscaler.Tiers[0]) != null ? "Speeds below were measured on this PC. Auto picks the best tier that keeps up with each video."
            : "Times each tier on your graphics card (about 10 seconds, longer the first time) and sets Auto to use the results.");
        Chips(p, "Look", new[] { ("sharp", "Sharp"), ("soft", "Soft") }, S.UpscaleLook, v => Change(() => { S.UpscaleLook = v; ApplyUpscaleSetting(); }));
        Note(S.UpscaleLook == "soft" ? "Soft: AnimeJaNai V3, cleaner and closer to the source." : "Sharp: AnimeJaNai V3 Sharp1, crisper lines and more detail.");

        // the tiers on this PC: estimated speed for 1080p and whether each is prepared yet
        Section(p, $"On your {gpu}");
        var auto = Upscaler.Auto(1920, 1080, 23.976);
        var grid = new Grid { Margin = new Thickness(4, 4, 4, 0) };
        foreach (var wdt in new[] { 1.4, 1.6, 1.0, 1.1 }) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(wdt, GridUnitType.Star) });
        void Cell(int r, int c, string text, bool muted = false, bool accent = false, bool bold = false)
        {
            var t = new TextBlock { Text = text, FontSize = 13.5, Margin = new Thickness(0, 6, 8, 6), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = accent ? (Brush)FindResource("Accent") : muted ? (Brush)FindResource("Muted") : (Brush)FindResource("Text") };
            Grid.SetRow(t, r); Grid.SetColumn(t, c); grid.Children.Add(t);
        }
        grid.RowDefinitions.Add(new RowDefinition());
        Cell(0, 0, "Tier", muted: true); Cell(0, 1, "Model", muted: true); Cell(0, 2, "1080p speed", muted: true); Cell(0, 3, "Status", muted: true);
        int row = 1;
        bool missing = false;
        var slow = new List<string>();
        foreach (var t in Upscaler.Tiers)
        {
            grid.RowDefinitions.Add(new RowDefinition());
            double fps = Upscaler.Fps1080(t);
            bool ready = Upscaler.Prepared(t, S.UpscaleLook);
            missing |= !ready;
            Cell(row, 0, t.Name + (auto == t ? "  · Auto" : ""), bold: auto == t);
            Cell(row, 1, t.Size, muted: true);
            Cell(row, 2, $"{(Upscaler.Measured(t) != null ? "" : "~")}{fps:0} fps", muted: fps < 27.6);
            if (fps < 27.6) slow.Add(t.Name);
            Cell(row, 3, ready ? "Ready" : "Not prepared", accent: ready, muted: !ready);
            row++;
        }
        p.Children.Add(new Border { Child = grid, CornerRadius = new CornerRadius(16), Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 2, 0, 6),
            Background = (Brush)FindResource("GlassFill"), BorderThickness = new Thickness(1), BorderBrush = (Brush)FindResource("GlassStroke") });
        if (slow.Count > 0)
            Note($"{string.Join(" and ", slow)} can't keep up with 1080p at 24 fps on this card. Auto still uses {(slow.Count == 1 ? "it" : "them")} for smaller videos when {(slow.Count == 1 ? "it does" : "they do")}.");
        if (missing)
        {
            var prep = new Button { Style = (Style)FindResource("Glass"), Content = upscalePreparing ? "Preparing…" : "Prepare all now", Padding = new Thickness(16, 8, 16, 8),
                FontSize = 14, Margin = new Thickness(4, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = !upscalePreparing && upscaleDetecting == null };
            System.Windows.Automation.AutomationProperties.SetName(prep, "Prepare AI upscaling models");
            prep.Click += async (_, _) =>
            {
                upscalePreparing = true;
                BuildGlobalSettings();
                string? err = null;
                foreach (var t in Upscaler.Tiers)
                    err ??= await Upscaler.Prepare(Upscaler.ModelFile(t, S.UpscaleLook));
                upscalePreparing = false;
                Toast(err == null ? "AI upscaling models ready" : $"Couldn't prepare a model: {err}");
                if (SettingsLayer.IsVisible) BuildGlobalSettings();
            };
            p.Children.Add(prep);
            Note("Each model is prepared for your graphics card once (about 15 seconds). Otherwise it happens the first time it's used.");
        }

        Section(p, "When it runs");
        Note("• Videos up to 2560×1728 are drawn at twice the size (a 4K file plays as it is).\n" +
             "• SDR video, decoded on the graphics card. HDR plays without upscaling for now.\n" +
             "• Subtitles, the controls and the glass are drawn after upscaling, so they stay sharp.\n" +
             "• If frames start dropping, it turns itself off for that video. The sparkle button in the player shows what it's doing; Ctrl+U turns it on or off.");
        Credits();

        void Credits() => Note("Models: AnimeJaNai by the-database (CC BY-NC-SA 4.0). Runs on NVIDIA TensorRT.");
        }

        void Note(string text) => p.Children.Add(new TextBlock { Text = text, FontSize = 12.5, Foreground = (Brush)FindResource("Muted"),
            TextWrapping = TextWrapping.Wrap, LineHeight = 20, Margin = new Thickness(4, 8, 0, 0) });

        void AudioPage()
        {
        // one set of language defaults per kind of video
        foreach (var (kind, title) in new[] { ("anime", "Anime"), ("tv", "TV shows & movies") })
        {
            var pref = S.LangFor(kind);
            Section(p, title);
            Chips(p, "Audio language", new[] { ("jpn", "Japanese"), ("eng", "English"), ("", "File default") }, pref.Audio, v => Change(() => S.SetLang(kind, audio: v)));
            Toggle(p, "Show subtitles", null, pref.On, () => Change(() => S.SetLang(kind, on: !pref.On)));
            if (pref.On)
                Chips(p, "Subtitle language", new[] { ("eng", "English"), ("jpn", "Japanese"), ("", "File default") }, pref.Subs, v => Change(() => S.SetLang(kind, subs: v)));
        }

        Section(p, "All subtitles");
        Toggle(p, "Prefer full subtitles", "Picks the full dialogue track over “Signs & Songs” or forced tracks", S.FullSubs, () => Change(() => S.FullSubs = !S.FullSubs));
        Chips(p, "Subtitle size", new[] { (0.8, "S"), (1.0, "M"), (1.3, "L") }, S.SubScale, v => Change(() => S.SubScale = v));
        p.Children.Add(new TextBlock { Text = "These are the defaults for each kind of video. You can still switch tracks, add your own subtitle files and fix their timing from the player.",
            FontSize = 12.5, Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 14, 0, 0) });
        }

        void AdvancedPage()
        {
        Section(p, "Troubleshooting");
        Toggle(p, "Debug info", "Shows live playback details (codec, frame rate, dropped frames…) over the video", S.Debug, () => Change(() => S.Debug = !S.Debug));

        Section(p, "Discord");
        Toggle(p, "Show what I'm watching on Discord", "Your profile shows the show, episode and time left while Haku Player plays", S.DiscordStatus, () => Change(() =>
        {
            S.DiscordStatus = !S.DiscordStatus;
            if (!S.DiscordStatus) Discord.Clear(); else DiscordUpdate();
        }));
        if (S.DiscordStatus)
        {
            // Discord shows the name of the application the status comes from, so it has to be one you made
            var box = new TextBox { Text = S.DiscordAppId, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = (Brush)FindResource("Text"),
                CaretBrush = Brushes.White, FontSize = 14, Padding = new Thickness(14, 8, 14, 8), Width = 260, VerticalAlignment = VerticalAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetName(box, "Discord application ID");
            box.TextChanged += (_, _) => { S.DiscordAppId = box.Text.Trim(); S.Save(); };
            var field = new Border { Child = box, CornerRadius = new CornerRadius(16), Background = (Brush)FindResource("GlassFill"), BorderThickness = new Thickness(1),
                BorderBrush = (Brush)FindResource("GlassStroke"), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(field, Dock.Right);
            var inner = new StackPanel();
            p.Children.Add(new Border { Child = inner, Margin = new Thickness(12, -4, 0, 4), Padding = new Thickness(16, 0, 0, 0),
                BorderThickness = new Thickness(2, 0, 0, 0), BorderBrush = new SolidColorBrush(Color.FromArgb(0x38, 255, 255, 255)) });
            inner.Children.Add(Row(field, Label("Application ID", "From discord.com/developers/applications")));
            inner.Children.Add(new TextBlock { Text = "One-time setup: on that page, click New Application, name it “Haku Player” (that name is what Discord shows), and paste its Application ID here. Discord must be open on this PC.",
                FontSize = 12.5, Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 2, 0, 6) });
        }

        Section(p, "Storage");
        long bytes = 0;
        try { bytes = Directory.Exists(ThumbDir) ? new DirectoryInfo(ThumbDir).EnumerateFiles().Sum(f => f.Length) : 0; } catch { }
        var clear = new Button { Style = (Style)FindResource("Glass"), Content = "Clear", Padding = new Thickness(14, 6, 14, 6), FontSize = 14 };
        clear.Click += (_, _) =>
        {
            try { foreach (var f in Directory.EnumerateFiles(ThumbDir)) File.Delete(f); } catch { }
            BuildGlobalSettings();
        };
        DockPanel.SetDock(clear, Dock.Right);
        p.Children.Add(Row(clear, Label("Timeline preview cache", $"{bytes / 1048576.0:0.#} MB · rebuilt automatically when needed")));

        p.Children.Add(new TextBlock { Text = "Haku Player · plays your files at their original quality", FontSize = 12, Foreground = (Brush)FindResource("Muted"),
            Margin = new Thickness(4, 22, 0, 4), HorizontalAlignment = HorizontalAlignment.Center });
        }
    }

    // Custom theme colour: drag along the rainbow bar for a hue, or type an exact hex colour.
    // Dragging recolours the app live; the settings page is rebuilt (and saved) when the drag ends.
    UIElement CustomColorPicker()
    {
        const double W = 320, Knob = 22;
        var current = Theme.Parse(S.CustomColor) ?? Color.FromRgb(0x2B, 0xA8, 0xFF);
        var rainbow = new GradientStopCollection();
        for (int i = 0; i <= 6; i++) rainbow.Add(new GradientStop(Theme.FromHsl(i * 60 % 360, 0.85, 0.58), i / 6.0));
        var bar = new Border { Width = W, Height = 28, CornerRadius = new CornerRadius(14), Cursor = System.Windows.Input.Cursors.Hand, BorderThickness = new Thickness(1),
            Background = new LinearGradientBrush(rainbow, new Point(0, 0), new Point(1, 0)), BorderBrush = (Brush)FindResource("GlassStroke") };
        var knob = new System.Windows.Shapes.Ellipse { Width = Knob, Height = Knob, Stroke = Brushes.White, StrokeThickness = 3, Fill = new SolidColorBrush(current),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        void PlaceKnob(double hue) => knob.Margin = new Thickness(3 + hue / 360 * (W - Knob - 6), 0, 0, 0);
        PlaceKnob(Theme.Hsl(current).H);
        var hex = new TextBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = (Brush)FindResource("Text"), CaretBrush = Brushes.White,
            FontSize = 14, Padding = new Thickness(14, 7, 14, 7), Width = 120, Text = S.CustomColor };
        System.Windows.Automation.AutomationProperties.SetName(hex, "Custom colour (hex)");
        System.Windows.Automation.AutomationProperties.SetName(bar, "Custom colour hue");

        void Pick(double x)
        {
            double hue = Math.Clamp((x - 3 - Knob / 2) / (W - Knob - 6), 0, 1) * 360;
            var c = Theme.FromHsl(hue % 360, 0.85, 0.58);
            S.CustomColor = hex.Text = Theme.Hex(c);
            ((SolidColorBrush)knob.Fill).Color = c; PlaceKnob(hue);
            Theme.Apply("custom");
        }
        void Commit() { S.Save(); MakeBlobs(); BuildGlobalSettings(); }
        bar.MouseLeftButtonDown += (_, e) => { bar.CaptureMouse(); Pick(e.GetPosition(bar).X); e.Handled = true; };
        bar.MouseMove += (_, e) => { if (bar.IsMouseCaptured) Pick(e.GetPosition(bar).X); };
        bar.MouseLeftButtonUp += (_, _) => { if (bar.IsMouseCaptured) { bar.ReleaseMouseCapture(); Commit(); } };
        void TypeHex()
        {
            var t = hex.Text.Trim();
            if (!t.StartsWith('#')) t = "#" + t;
            if (t.Equals(S.CustomColor, StringComparison.OrdinalIgnoreCase)) return; // also stops the rebuild re-firing this via LostFocus
            if (t.Length != 7 || Theme.Parse(t) is not Color c) { Toast("Use a hex colour like #2BA8FF"); hex.Text = S.CustomColor; return; }
            S.CustomColor = Theme.Hex(c);
            Theme.Apply("custom");
            Commit();
        }
        hex.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { TypeHex(); e.Handled = true; } };
        hex.LostFocus += (_, _) => TypeHex();

        return new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 8), Children =
        {
            new Grid { Width = W, VerticalAlignment = VerticalAlignment.Center, Children = { bar, knob } },
            new FrameworkElement { Width = 14 },
            new Border { Child = hex, CornerRadius = new CornerRadius(16), Background = (Brush)FindResource("GlassFill"), BorderThickness = new Thickness(1),
                BorderBrush = (Brush)FindResource("GlassStroke"), VerticalAlignment = VerticalAlignment.Center },
        } };
    }

    // Moving background on/off.
    void ApplyAppearance()
    {
        Blobs.Opacity = S.MovingBackground ? 1 : 0;
        UpdateBlobs(); // no point animating what you can't see
    }

    // How much of the desktop shows through the main window's glass.
    // In the player it's solid black: nothing behind the window may show through, even for the frame or two a resize
    // leaves before mpv has drawn the video at the new size.
    void ApplyGlass() => Tint.Background = Player.Visibility == Visibility.Visible ? Brushes.Black
        : new SolidColorBrush(Color.FromArgb(S.Glass switch { 0 => 0x80, 2 => 0xE2, _ => 0xB8 }, 0x0A, 0x0B, 0x10));

    // ---------- little builders ----------

    static void Section(Panel p, string title) => p.Children.Add(new TextBlock
    {
        Text = title.ToUpperInvariant(), FontSize = 12, FontWeight = FontWeights.Bold, Opacity = 0.55,
        Margin = new Thickness(4, p.Children.Count == 0 ? 0 : 22, 0, 8),
    });

    static DockPanel Row(UIElement right, UIElement left)
    {
        var d = new DockPanel { Margin = new Thickness(4, 6, 4, 6), LastChildFill = true };
        d.Children.Add(right);
        d.Children.Add(left);
        return d;
    }

    StackPanel Label(string title, string? sub)
    {
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(new TextBlock { Text = title, FontSize = 15 });
        if (sub != null) sp.Children.Add(new TextBlock { Text = sub, FontSize = 12.5, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 2, 0, 0) });
        return sp;
    }

    // iOS-style switch on the right of a row.
    void Toggle(Panel p, string title, string? sub, bool on, Action flip, bool child = false)
    {
        if (child)
        {
            // sub-setting: indented under its parent, with a soft guide line on the left
            var inner = new StackPanel();
            p.Children.Add(new Border { Child = inner, Margin = new Thickness(12, -4, 0, 4), Padding = new Thickness(16, 0, 0, 0),
                BorderThickness = new Thickness(2, 0, 0, 0), BorderBrush = new SolidColorBrush(Color.FromArgb(0x38, 255, 255, 255)) });
            p = inner;
        }
        var knob = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = Brushes.White,
            HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left, Margin = new Thickness(3),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.4 } };
        var track = new Border { Width = 50, Height = 28, CornerRadius = new CornerRadius(14), Child = knob,
            Background = on ? (Brush)FindResource("AccentGrad") : new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)) };
        var b = new Button { Style = (Style)FindResource("Flat"), Content = track, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(b, title);
        b.Click += (_, _) => flip();
        DockPanel.SetDock(b, Dock.Right);
        p.Children.Add(Row(b, Label(title, sub)));
    }

    void Chips<T>(Panel p, string title, (T Value, string Name)[] options, T current, Action<T> pick)
    {
        var chips = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (value, name) in options)
        {
            var b = new Button { Style = (Style)FindResource("Chip"), Content = name, Tag = Equals(value, current) ? "On" : null, Foreground = (Brush)FindResource("Text") };
            System.Windows.Automation.AutomationProperties.SetName(b, $"{title}: {name}");
            b.Click += (_, _) => pick(value);
            chips.Children.Add(b);
        }
        DockPanel.SetDock(chips, Dock.Right);
        p.Children.Add(Row(chips, Label(title, null)));
    }
}
