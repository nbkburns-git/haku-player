using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace AnimePlayer;

// Library page extras: favourites, search, filters (favourites / genre), sorting,
// and the background fetch that gives every show its details, banner and (if missing) cover.
public partial class MainWindow
{
    bool filterFav;
    string sortMode = "az";     // az / new / top
    string? genre;
    string? kindFilter; // a tag ("Anime", "TV shows", your own…): only shows with it
    bool prefetching;

    // ---------- favourites ----------

    public static bool IsFav(object? item) => item switch
    {
        Entry e => !e.IsRandom && Settings.Current.Favorites.Contains(e.Folder),
        Video v => Settings.Current.FavoriteVideos.Contains(v.Path),
        _ => false,
    };

    void ToggleFav(object? item)
    {
        var s = Settings.Current;
        if (item is Entry e && !e.IsRandom) { if (!s.Favorites.Remove(e.Folder)) s.Favorites.Add(e.Folder); }
        else if (item is Video v) { if (!s.FavoriteVideos.Remove(v.Path)) s.FavoriteVideos.Add(v.Path); }
        else return;
        s.Save();
        RefreshFavViews();
    }

    void OnStar(object s, RoutedEventArgs e)
    {
        ToggleFav(((FrameworkElement)s).DataContext);
        e.Handled = true;
    }

    void OnDetailStar(object s, RoutedEventArgs e) => ToggleFav(Detail.DataContext);

    // star glyphs update by re-binding (entries are immutable records)
    void RefreshFavViews()
    {
        if (Library.IsVisible) ApplyFilter();
        if (Detail.DataContext is Entry cur)
        {
            DetailStar.Content = IsFav(cur) ? "" : "";
            var list = Videos.ItemsSource;
            Videos.ItemsSource = null;
            Videos.ItemsSource = list;
        }
    }

    // ---------- search / filter / sort ----------

    List<Entry> LibraryBase()
    {
        if (currentCategory == "All")
            return Everything(lib).OrderBy(x => x.Name, Natural.I).ToList();
        return CategoryList(currentCategory);
    }

    void ApplyFilter()
    {
        var q = SearchBox.Text.Trim();
        SearchHint.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        IEnumerable<Entry> items = LibraryBase();
        if (filterFav) items = items.Where(x => !x.IsRandom && (IsFav(x) || x.Videos.Any(IsFav)));
        if (kindFilter != null) items = items.Where(x => x.Tag == kindFilter);
        if (genre != null) items = items.Where(x => InfoService.Cached(x.InfoKey)?.Genres.Contains(genre) == true);
        if (q.Length > 0)
            items = items.Where(x => !x.IsRandom && Matches(x, q));
        items = sortMode switch
        {
            "new" => items.OrderByDescending(x => x.IsRandom).ThenByDescending(x => SafeTime(x.Folder)),
            "top" => items.OrderByDescending(x => x.IsRandom).ThenByDescending(x => InfoService.Cached(x.InfoKey)?.Score ?? -1),
            _ => items,
        };
        Cards.ItemsSource = items.ToList();
        BuildFilterBar();
    }

    static bool Matches(Entry x, string q)
    {
        var info = InfoService.Cached(x.InfoKey);
        return x.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || (info?.Title?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
            || (info?.Native?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
            || x.Videos.Any(v => v.Title.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    static DateTime SafeTime(string folder)
    {
        try { return folder.Length > 0 ? Directory.GetCreationTime(folder) : DateTime.MinValue; } catch { return DateTime.MinValue; }
    }

    // Filtering rebuilds every cover card, so it waits for a short pause in typing.
    System.Windows.Threading.DispatcherTimer? searchDelay;
    void OnSearch(object s, TextChangedEventArgs e)
    {
        if (searchDelay == null)
        {
            searchDelay = new() { Interval = TimeSpan.FromMilliseconds(150) };
            searchDelay.Tick += (_, _) => { searchDelay.Stop(); if (Library.IsVisible) ApplyFilter(); };
        }
        searchDelay.Stop();
        searchDelay.Start();
    }

    void BuildFilterBar()
    {
        FilterBar.Children.Clear();
        Button Chip(string text, bool on, Action click)
        {
            var b = new Button { Style = (Style)FindResource("Chip"), Content = text, Tag = on ? "On" : null, Foreground = (System.Windows.Media.Brush)FindResource("Text"),
                FontSize = 14, Margin = new Thickness(0, 0, 8, 8) };
            System.Windows.Automation.AutomationProperties.SetName(b, $"Filter {text}");
            b.Click += (_, _) => { click(); ApplyFilter(); };
            FilterBar.Children.Add(b);
            return b;
        }
        void Gap() => FilterBar.Children.Add(new Border { Width = 1, Height = 22, Margin = new Thickness(6, 3, 14, 11), Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x30, 255, 255, 255)) });

        Chip("All", !filterFav, () => filterFav = false);
        Chip("★ Favourites", filterFav, () => filterFav = true);
        // its tags (Anime / TV shows / Movies / your own), when this library mixes them; click again to show everything
        var builtIn = LibraryFolder.Kinds.Select(k => k.Name).ToList();
        var tags = LibraryBase().Where(x => !x.IsRandom && x.Tag.Length > 0).Select(x => x.Tag).Distinct()
            .OrderBy(t => builtIn.IndexOf(t) is var i && i >= 0 ? i : 99).ThenBy(t => t, Natural.I).ToList();
        if (tags.Count > 1)
        {
            Gap();
            foreach (var t in tags) Chip(t, kindFilter == t, () => kindFilter = kindFilter == t ? null : t);
        }
        Gap();
        Chip("A–Z", sortMode == "az", () => sortMode = "az");
        Chip("Newest", sortMode == "new", () => sortMode = "new");
        Chip("Top rated", sortMode == "top", () => sortMode = "top");
        // genres present in this section (from fetched details), most common first
        var genres = LibraryBase().Select(x => InfoService.Cached(x.InfoKey)).Where(i => i != null)
            .SelectMany(i => i!.Genres).GroupBy(g => g).OrderByDescending(g => g.Count()).Take(10).Select(g => g.Key).ToList();
        if (genres.Count > 0)
        {
            Gap();
            foreach (var g in genres) Chip(g, genre == g, () => genre = genre == g ? null : g);
        }
    }

    void ResetFilters()
    {
        filterFav = false; genre = null; kindFilter = null; sortMode = "az";
        SearchBox.Text = "";
    }

    // Header search: everything at once, cursor in the search box.
    void OnSearchAll(object s, RoutedEventArgs e)
    {
        currentCategory = "All";
        LibraryTitle.Text = "Everything";
        ResetFilters();
        ApplyFilter();
        Show(Library);
        Dispatcher.BeginInvoke(() => { SearchBox.Focus(); Keyboard.Focus(SearchBox); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    // ---------- background details + covers ----------

    // Fetches details for every series that doesn't have them yet (AniList allows ~90 requests a minute,
    // so this paces itself), and saves a cover.jpg into any folder that has no cover.
    async Task PrefetchDetails()
    {
        if (prefetching) return;
        prefetching = true;
        try
        {
            // new shows, ones matched by the old name-only lookup (re-checked once with the better matching), and missing covers
            // TV/movie posters saved from metahub (sometimes the wrong picture) are swapped once for IMDb's official one
            static bool OldPoster(Entry x) => x.Kind != "anime" && InfoService.Cached(x.InfoKey)?.CoverUrl?.Contains("metahub.space") == true;
            // TV details saved before episode names were kept: fetch once more to get them
            static bool NoEpisodeNames(Entry x) => x.Kind != "movie" && x.Unit == "episodes" && InfoService.Cached(x.InfoKey) is { EpisodeNames: null };
            var todo = Everything(lib).Where(x => !x.IsRandom)
                .Where(x => InfoService.Cached(x.InfoKey) is not { Source: not null } || (x.Cover == null && InfoService.Cached(x.InfoKey)?.CoverUrl != null) || OldPoster(x) || NoEpisodeNames(x))
                .ToList();
            if (todo.Count == 0) return;
            int done = 0, covers = 0;
            foreach (var e in todo)
            {
                bool repoint = OldPoster(e);
                var info = InfoService.Cached(e.InfoKey) is { Source: not null } fresh && !repoint && !NoEpisodeNames(e) ? fresh : await InfoService.Refresh(e);
                NameEpisodes(e); // official episode names, straight away
                // save when there's no cover yet, or to replace an old metahub poster now that IMDb's is available
                bool save = info?.CoverUrl != null && (e.Cover == null || (repoint && !info.CoverUrl.Contains("metahub.space")));
                if (save && await SaveCover(e, info!.CoverUrl!, replace: repoint)) covers++;
                if (Detail.IsVisible && Detail.DataContext == e) { _ = LoadDetailInfo(e); ShowEpisodes(e, openSeason); } // you're looking at it: show the corrected details
                done++;
                if (done % 5 == 0 || done == todo.Count) Toast($"Checking show details · {done} of {todo.Count}");
                await Task.Delay(2000); // AniList allows ~30 requests a minute right now
            }
            if (covers > 0) await SyncLibrary(false); // pick up the new covers
            Toast(covers > 0 ? $"Details ready · {covers} cover{(covers == 1 ? "" : "s")} added" : "Show details ready");
            if (Library.IsVisible) ApplyFilter();
        }
        finally { prefetching = false; }
    }

    static readonly HttpClient CoverHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    // Anime: cover.jpg into the show folder (as before). TV shows and movies: into the app's data\covers, never your media folders.
    static async Task<bool> SaveCover(Entry e, string url, bool replace = false)
    {
        try
        {
            string target;
            if (e.Kind == "anime" && Directory.Exists(e.Folder)) // loose files have no folder of their own: like TV
            {
                if (e.Folder.Length == 0 || Directory.EnumerateFiles(e.Folder, "cover.*").Any()) return false;
                target = Path.Combine(e.Folder, "cover" + Path.GetExtension(new Uri(url).AbsolutePath));
            }
            else
            {
                target = CoverFile(e.InfoKey);
                if (File.Exists(target) && !replace) return false;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            }
            await File.WriteAllBytesAsync(target, await CoverHttp.GetByteArrayAsync(url));
            return true;
        }
        catch { return false; }
    }
}

// Star glyph for an entry or a video: filled when it's a favourite. With parameter "vis": Visible only when favourite.
public sealed class FavConverter : IValueConverter
{
    static readonly System.Windows.Media.Brush Gold = Freeze(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xC8, 0x3D)));
    static readonly System.Windows.Media.Brush Plain = Freeze(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)));
    static readonly System.Windows.Media.Brush Frost = Freeze(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)));
    static System.Windows.Media.Brush Freeze(System.Windows.Media.Brush b) { b.Freeze(); return b; }
    public object Convert(object value, Type t, object? p, CultureInfo c)
    {
        bool fav = MainWindow.IsFav(value);
        switch (p as string)
        {
            case "vis": return fav ? Visibility.Visible : Visibility.Collapsed;
            case "color": return fav ? Gold : Plain;                 // gold when favourite, soft white otherwise
            case "fill": return fav ? Gold : Frost;                  // star body: gold, or frosted see-through glass
            case "glow": return fav ? 0.85 : 0.0;
        }
        return fav ? "" : "";
    }
    public object ConvertBack(object v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}
