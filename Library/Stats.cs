using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AnimePlayer;

// Library stats (Settings > Stats): what's in the library, how long it all runs, and what you've watched.
public partial class MainWindow
{
    // Measures whatever isn't cached yet, in the background; the stats page refreshes as it goes.
    async void MeasureLibrary(List<string> files)
    {
        if (probing) return;
        var cache = Durations();
        var todo = new List<(string Path, long Size, long Stamp)>();
        foreach (var f in files)
        {
            try
            {
                var fi = new FileInfo(f);
                if (!cache.TryGetValue(f, out var p) || p.Size != fi.Length || p.Stamp != fi.LastWriteTimeUtc.Ticks) todo.Add((f, fi.Length, fi.LastWriteTimeUtc.Ticks));
            }
            catch { }
        }
        if (todo.Count == 0) return;
        probing = true; probeDone = 0; probeTotal = todo.Count;
        try { await MeasureDurations(todo, () => { if (SettingsLayer.IsVisible && settingsPage == "Stats") BuildGlobalSettings(); }); }
        catch { }
        finally
        {
            probing = false;
            if (SettingsLayer.IsVisible && settingsPage == "Stats") BuildGlobalSettings();
        }
    }


    void StatsPage(Panel p)
    {
        var shows = Everything(lib).Where(e => e.Unit != "themes").ToList();
        var ops = Everything(lib).Where(e => e.Unit == "themes").ToList();
        var files = shows.Concat(ops).SelectMany(e => e.Videos).Select(v => v.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        MeasureLibrary(files);
        var cache = Durations();
        double runtime = 0; long bytes = 0; int measured = 0;
        foreach (var f in files) if (cache.TryGetValue(f, out var pr)) { runtime += pr.Dur; bytes += pr.Size; measured++; }
        string Pending(string s) => probing ? $"{s} so far · measuring {probeDone} of {probeTotal}…" : s;

        void Stat(string label, string value)
        {
            var v = new TextBlock { Text = value, FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(v, Dock.Right);
            p.Children.Add(Row(v, Label(label, null)));
        }

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

        // the last two weeks, one bar per day
        Section(p, "Last 14 days");
        var days = Enumerable.Range(0, 14).Select(i => DateTime.Now.Date.AddDays(i - 13)).ToList();
        double max = Math.Max(1800, days.Max(d => Watch.Data.Days.GetValueOrDefault(d.ToString("yyyy-MM-dd"))));
        var chart = new Grid { Height = 120, Margin = new Thickness(4, 4, 4, 0) };
        for (int i = 0; i < days.Count; i++)
        {
            chart.ColumnDefinitions.Add(new ColumnDefinition());
            double secs = Watch.Data.Days.GetValueOrDefault(days[i].ToString("yyyy-MM-dd"));
            var col = new DockPanel { Margin = new Thickness(3, 0, 3, 0), ToolTip = $"{days[i]:ddd d MMM} · {(secs > 0 ? Hours(secs) : "nothing")}" };
            var day = new TextBlock { Text = days[i].ToString("ddd")[..2], FontSize = 11, Foreground = (Brush)FindResource("Muted"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
            DockPanel.SetDock(day, Dock.Bottom);
            col.Children.Add(day);
            col.Children.Add(new Border
            {
                VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = new CornerRadius(5), Height = Math.Max(3, 96 * secs / max),
                Background = secs > 0 ? (Brush)FindResource("AccentGrad") : new SolidColorBrush(Color.FromArgb(0x26, 255, 255, 255)),
            });
            Grid.SetColumn(col, i);
            chart.Children.Add(col);
        }
        p.Children.Add(chart);

        // most watched: time spent per show
        var byShow = shows.Select(e => (Name: e.Title, Secs: e.Videos.Sum(v => Watch.Get(v.Path)?.Seconds ?? 0))).Where(x => x.Secs > 60).OrderByDescending(x => x.Secs).Take(5).ToList();
        if (byShow.Count > 0)
        {
            Section(p, "Most watched");
            double top = byShow[0].Secs;
            foreach (var (name, secs) in byShow)
            {
                var row = new Grid { Margin = new Thickness(4, 4, 4, 6) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
                row.Children.Add(new TextBlock { Text = name, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
                var bar = new Border { Height = 8, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left, Background = (Brush)FindResource("AccentGrad"), Margin = new Thickness(10, 0, 10, 0) };
                double share = secs / top;
                row.SizeChanged += (_, _) => bar.Width = Math.Max(6, (row.ActualWidth - 290) * share); // the middle column, scaled to the top show
                Grid.SetColumn(bar, 1);
                row.Children.Add(bar);
                var t = new TextBlock { Text = Hours(secs), FontSize = 13, Foreground = (Brush)FindResource("Muted"), TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(t, 2);
                row.Children.Add(t);
                p.Children.Add(row);
            }
        }
        p.Children.Add(new TextBlock { Text = "Watch time is counted from now on (earlier viewing wasn't recorded). Running times are read from the files once and remembered.",
            FontSize = 12.5, Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 16, 0, 0) });
    }
}
