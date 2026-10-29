using System;
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
    static readonly (string Key, string Icon, string Name)[] SettingsPages =
    {
        ("Library", "", "Library"),
        ("Appearance", "", "Appearance"),
        ("Playback", "", "Playback"),
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
            case "Audio": AudioPage(); break;
            case "Stats": StatsPage(p); break;
            default: AdvancedPage(); break;
        }

        void LibraryPage()
        {
        Section(p, "Library locations");
        async Task Changed() { S.Save(); BuildGlobalSettings(); await RescanLibraries(); }
        foreach (var lf in S.Libraries.ToList())
        {
            // one row per location: where it is, what's in it (type), remove
            var card = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(16, 12, 10, 12), Margin = new Thickness(0, 0, 0, 8),
                Background = new SolidColorBrush(Color.FromArgb(0x16, 255, 255, 255)), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(0x22, 255, 255, 255)) };
            var row = new DockPanel();
            var remove = new Button { Style = (Style)FindResource("Caption"), Content = "", FontSize = 14, ToolTip = "Remove this location (your files are not touched)", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            System.Windows.Automation.AutomationProperties.SetName(remove, $"Remove library {lf.Path}");
            remove.Click += async (_, _) => { S.Libraries.Remove(lf); await Changed(); };
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            var kinds = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            foreach (var (k, label) in new[] { ("anime", "Anime"), ("tv", "TV shows"), ("movie", "Movies") })
            {
                var chip = new Button { Style = (Style)FindResource("Chip"), Content = label, Tag = lf.Kind == k ? "On" : null, Foreground = (Brush)FindResource("Text") };
                System.Windows.Automation.AutomationProperties.SetName(chip, $"{lf.Path}: {label}");
                chip.Click += async (_, _) => { if (lf.Kind == k) return; lf.Kind = k; await Changed(); };
                kinds.Children.Add(chip);
            }
            DockPanel.SetDock(kinds, Dock.Right);
            row.Children.Add(kinds);
            bool here = Directory.Exists(lf.Path);
            row.Children.Add(Label(lf.Path, here ? lf.Kind switch { "tv" => "A folder of TV show folders", "movie" => "A folder of movies", _ => "Anime Shows and Anime Openings inside" } : "Not found: drive unplugged or folder moved"));
            card.Child = row;
            p.Children.Add(card);
        }

        var add = new Button { Style = (Style)FindResource("Glass"), Padding = new Thickness(16, 8, 18, 8), FontSize = 14, Margin = new Thickness(0, 4, 10, 0),
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) }, new TextBlock { Text = "Add library" } } } };
        System.Windows.Automation.AutomationProperties.SetName(add, "Add library");
        add.Click += async (_, _) =>
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "Choose a folder of anime, TV shows or movies", UseDescriptionForTitle = true };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            var path = dlg.SelectedPath;
            if (S.Libraries.Any(x => string.Equals(x.Path.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) { Toast("That folder is already in your library"); return; }
            // best guess at what it holds; you can change it with the chips
            string kind = Directory.Exists(Path.Combine(path, "Anime Shows")) || Directory.Exists(Path.Combine(path, "Anime Openings")) ? "anime"
                : System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path.TrimEnd('\\')), "movie|film", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? "movie" : "tv";
            S.Libraries.Add(new LibraryFolder { Path = path, Kind = kind });
            await Changed();
        };
        var sync = new Button { Style = (Style)FindResource("Glass"), Content = "Sync now", Padding = new Thickness(16, 8, 16, 8), FontSize = 14, Margin = new Thickness(0, 4, 0, 0) };
        sync.Click += async (_, _) => await SyncLibrary(true);
        System.Windows.Automation.AutomationProperties.SetName(sync, "Sync now");
        p.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { add, sync } });

        p.Children.Add(new TextBlock { Text = "Anime, TV shows and movies all appear under Shows and Movies; anime openings and endings have their own section. New files are picked up automatically while Haku Player is open.",
            FontSize = 12.5, Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 14, 0, 0) });
        p.Children.Add(new TextBlock { Text = "Details come from AniList for anime and Cinemeta (IMDb) for TV shows and movies.", FontSize = 12.5,
            Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 2, 0, 0) });
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

    // Moving background on/off.
    void ApplyAppearance()
    {
        Blobs.Opacity = S.MovingBackground ? 1 : 0;
        UpdateBlobs(); // no point animating what you can't see
    }

    // How much of the desktop shows through the main window's glass.
    void ApplyGlass() => Tint.Background = new SolidColorBrush(Color.FromArgb(S.Glass switch { 0 => 0x80, 2 => 0xE2, _ => 0xB8 }, 0x0A, 0x0B, 0x10));

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
