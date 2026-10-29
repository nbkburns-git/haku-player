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
