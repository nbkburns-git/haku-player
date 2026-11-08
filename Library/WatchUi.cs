using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace AnimePlayer;

// Watched ticks on the show page: toggle one episode, or a whole season / show at once.
public partial class MainWindow
{
    void OnWatchedToggle(object s, RoutedEventArgs e)
    {
        e.Handled = true; // don't also play it
        if (((FrameworkElement)s).DataContext is Video v) Watch.SetDone(new[] { v.Path }, !Watch.IsDone(v.Path));
    }

    // What "Mark all" covers: the season you're in, else the whole show.
    List<Video> ShownVideos() =>
        Videos.ItemsSource is IEnumerable<Video> list ? list.ToList()
        : Detail.DataContext is Entry e ? e.Videos.Where(v => v.Season >= 0).ToList() : new();

    void OnMarkAll(object s, RoutedEventArgs e)
    {
        var list = ShownVideos();
        if (list.Count == 0) return;
        Watch.SetDone(list.Select(v => v.Path), !list.All(v => Watch.IsDone(v.Path)));
    }

    // Ticks or progress changed: redraw whatever shows them, keeping your place in the list.
    void RefreshWatchViews()
    {
        if (Detail.IsVisible && Detail.DataContext is Entry cur)
        {
            if (SeasonList.IsVisible) ShowEpisodes(cur, null);
            else { var items = Videos.ItemsSource; Videos.ItemsSource = null; Videos.ItemsSource = items; UpdateMarkAll(); }
        }
        if (Library.IsVisible) ApplyFilter();
        if (Home.IsVisible) UpdateContinue();
    }

    void UpdateMarkAll()
    {
        bool show = Detail.DataContext is Entry e && e.Unit == "episodes" || Detail.DataContext is Entry { Kind: "movie" };
        var list = show ? ShownVideos() : new();
        MarkAllBtn.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        bool all = list.Count > 0 && list.All(v => Watch.IsDone(v.Path));
        MarkAllText.Text = all ? "Mark as unwatched" : SeasonList.IsVisible || !SeasonBack.IsVisible ? "Mark all watched" : "Mark season watched";
    }

    static string WatchedSuffix(IEnumerable<Video> videos)
    {
        var l = videos.ToList();
        int done = l.Count(v => Watch.IsDone(v.Path));
        return done == 0 ? "" : done >= l.Count ? " · all watched" : $" · {done} watched";
    }
}

// Watched ticks and progress for the episode rows and cover cards.
public sealed class WatchConverter : System.Windows.Data.IValueConverter
{
    static readonly System.Windows.Media.Brush Faint = Freeze(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)));
    static System.Windows.Media.Brush Freeze(System.Windows.Media.Brush b) { b.Freeze(); return b; }

    public object Convert(object value, Type t, object? p, System.Globalization.CultureInfo c)
    {
        if (value is Entry e)
        {
            if (e.IsRandom || e.Unit == "themes") return e.Count;
            int done = e.Videos.Count(v => Watch.IsDone(v.Path));
            if (e.Kind == "movie") return done > 0 ? "Watched" : e.Videos.Count > 1 ? e.Count : Watch.Progress(e.Videos[0].Path) > 0 ? "In progress" : "Movie";
            return done == 0 ? e.Count : done >= e.Videos.Count ? $"{e.Count} · all watched" : $"{e.Count} · {done} watched";
        }
        if (value is not Video v) return System.Windows.DependencyProperty.UnsetValue;
        bool isDone = Watch.IsDone(v.Path);
        return (p as string) switch
        {
            "progress" => Watch.Progress(v.Path),                                   // ScaleX of the bar under the row
            "bar" => Watch.Progress(v.Path) > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed,
            "dim" => isDone ? 0.55 : 1.0,                                          // watched rows fade back a little
            "color" => isDone ? System.Windows.Application.Current.Resources["Accent"] : Faint,
            "tip" => isDone ? "Watched · click to mark as unwatched" : "Mark as watched",
            _ => System.Windows.DependencyProperty.UnsetValue,
        };
    }
    public object ConvertBack(object v, Type t, object? p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
}
