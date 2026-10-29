using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace AnimePlayer;

// Library sync: rescan on demand (top-bar button / Settings), and automatically when files in the
// library folder change while the app is open. A small glass toast says what changed.
public partial class MainWindow
{
    bool syncing;
    string currentCategory = "";
    readonly List<FileSystemWatcher> watchers = new();
    readonly DispatcherTimer syncDebounce = new() { Interval = TimeSpan.FromSeconds(3) };
    readonly DispatcherTimer toastTimer = new() { Interval = TimeSpan.FromSeconds(3.2) };
    static readonly string[] WatchedExt = { ".mp4", ".mkv", ".webm", ".avi", ".mov", ".m4v", ".png", ".jpg", ".jpeg", ".webp", ".bmp" };

    void OnSync(object s, RoutedEventArgs e) => _ = SyncLibrary(true);

    public async Task SyncLibrary(bool manual)
    {
        if (syncing) return;
        syncing = true;
        var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever };
        SyncSpin.BeginAnimation(RotateTransform.AngleProperty, spin);
        try
        {
            var old = lib;
            var fresh = await Task.Run(ScanAll);
            lib = fresh;
            ApplyLibrary();
            _ = PrefetchDetails();

            // what changed
            string Diff(string key, string seriesWord, string videoWord)
            {
                var o = old.GetValueOrDefault(key) ?? new();
                var n = fresh[key];
                int newSeries = n.Count(e => o.All(x => x.Name != e.Name));
                int goneSeries = o.Count(e => n.All(x => x.Name != e.Name));
                int newVideos = Math.Max(0, n.Sum(e => e.Videos.Count) - o.Sum(e => e.Videos.Count));
                var parts = new List<string>();
                if (newSeries > 0) parts.Add($"{newSeries} new {seriesWord}{(newSeries == 1 ? "" : "s")}");
                if (newVideos > 0) parts.Add($"{newVideos} new {videoWord}{(newVideos == 1 ? "" : "s")}");
                if (goneSeries > 0) parts.Add($"{goneSeries} {seriesWord}{(goneSeries == 1 ? "" : "s")} removed");
                return string.Join(" · ", parts);
            }
            var msg = string.Join(" · ", new[] { Diff("Shows", "show", "episode"), Diff("Openings", "series", "theme") }.Where(x => x.Length > 0))
                .Replace("seriess", "series");
            if (msg.Length > 0) Toast($"Library synced · {msg}");
            else if (manual) Toast("Library is up to date");
        }
        catch { if (manual) Toast("Couldn't read the library folder"); }
        finally
        {
            SyncSpin.BeginAnimation(RotateTransform.AngleProperty, null);
            syncing = false;
        }
    }

    // Push the (re)scanned library into whatever is on screen.
    void ApplyLibrary()
    {
        BuildWall(ShowsWall, lib["Shows"]);
        BuildWall(OpsWall, lib["Openings"]);
        int shows = lib["Shows"].Count(x => x.Kind != "movie"), movies = lib["Shows"].Count(x => x.Kind == "movie"), themes = lib["Openings"].Sum(x => x.Videos.Count);
        ShowsCount.Text = string.Join(" · ", new[] { shows > 0 ? $"{shows} show{(shows == 1 ? "" : "s")}" : null, movies > 0 ? $"{movies} movie{(movies == 1 ? "" : "s")}" : null }
            .Where(x => x != null).DefaultIfEmpty("Nothing yet"));
        OpsCount.Text = $"{lib["Openings"].Count} series · {themes} theme{(themes == 1 ? "" : "s")}";
        UpdateContinue();
        if (Library.IsVisible && currentCategory != "") ApplyFilter();
        if (Detail.IsVisible && Detail.DataContext is Entry cur)
        {
            var now = lib.Values.SelectMany(x => x).FirstOrDefault(x => x.Folder == cur.Folder);
            if (now != null) { Detail.DataContext = now; ShowEpisodes(now, openSeason); } // stay in the season you were looking at
        }
    }

    List<Entry> CategoryList(string key)
    {
        var list = lib.GetValueOrDefault(key) ?? new();
        if (key == "Openings" && list.Count > 0)
            list = list.Prepend(new Entry("Random", null, list.SelectMany(x => x.Videos).ToList(), "themes", true)).ToList();
        return list;
    }

    // Auto-sync: any video/cover added, removed or renamed under the library re-syncs a few seconds later.
    void StartWatcher()
    {
        foreach (var w in watchers) w.Dispose();
        watchers.Clear();
        syncDebounce.Tick -= OnSyncDebounce;
        syncDebounce.Tick += OnSyncDebounce;
        FileSystemEventHandler changed = (_, e) =>
        {
            var ext = Path.GetExtension(e.FullPath).ToLowerInvariant();
            if (ext.Length > 0 && !WatchedExt.Contains(ext)) return; // ignore temp files etc. (folders have no extension)
            Dispatcher.BeginInvoke(() => { syncDebounce.Stop(); syncDebounce.Start(); });
        };
        foreach (var lf in Locations)
        {
            if (!Directory.Exists(lf.Path)) continue;
            try
            {
                var w = new FileSystemWatcher(lf.Path) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName };
                w.Created += changed;
                w.Deleted += changed;
                w.Renamed += (s, e) => changed(s, e);
                w.EnableRaisingEvents = true;
                watchers.Add(w);
            }
            catch { } // network drives etc. without change notifications: manual sync still works
        }
    }

    // After adding, removing or retyping a library location.
    async Task RescanLibraries()
    {
        StartWatcher();
        await SyncLibrary(true);
    }

    void OnSyncDebounce(object? s, EventArgs e)
    {
        syncDebounce.Stop();
        _ = SyncLibrary(false);
    }

    void Toast(string text)
    {
        ToastText.Text = text;
        ToastBox.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
        ToastShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        toastTimer.Stop();
        toastTimer.Tick -= HideToast;
        toastTimer.Tick += HideToast;
        toastTimer.Start();
    }

    void HideToast(object? s, EventArgs e)
    {
        toastTimer.Stop();
        ToastBox.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)));
    }
}
