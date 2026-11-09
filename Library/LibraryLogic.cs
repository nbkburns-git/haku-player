using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace AnimePlayer;

// The library's logic, with no UI in it: shared by the WPF and the WinUI app (docs/winui3-migration-plan.md). What's in
// the libraries (lib, Libs), favourites, search / filters / sorting, Continue watching, seasons and what's missing, where
// Play starts, the library sync message, and the background fetch of show details and covers.
// Each app provides, in its own MainWindow: Toast(string), SyncLibrary(bool), OnDetailsFetched(Entry), OnDetailsDone().
public partial class MainWindow
{
    // Libraries: a command-line arg (one anime folder with Anime Shows / Anime Openings, for testing), else the ones set in Settings.
    static readonly string? Arg = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault();
    static readonly string? ArgRoot = Arg != null && Directory.Exists(Arg) ? Arg : null;
    static readonly string? ArgFile = Arg != null && File.Exists(Arg) ? Arg : null; // "Open with" / a video dropped on the exe: play it
    static readonly List<MediaLibrary> ArgLibs = ArgRoot == null ? new() : new()
    {
        new() { Id = "shows", Name = "Shows and Movies", Folders = { new() { Path = ArgRoot, Kind = "anime" } } },
        new() { Id = "themes", Name = "Openings / Endings", Folders = { new() { Path = ArgRoot, Kind = "themes" } } },
    };
    static List<MediaLibrary> Libs => ArgRoot != null ? ArgLibs : Settings.Current.Libs;

    static string DataDir => Settings.DataDir; // %LOCALAPPDATA%\Haku Player (a property: static fields in other files of
                                                // this partial class may be set before a field here)
    static readonly System.Collections.Frozen.FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> CoverExt =
        System.Collections.Frozen.FrozenSet.ToFrozenSet(new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp" }, StringComparer.OrdinalIgnoreCase)
            .GetAlternateLookup<ReadOnlySpan<char>>();

    Dictionary<string, List<Entry>> lib = new();
    string currentCategory = "";
    bool filterFav;
    string sortMode = "az";     // az / new / top
    string? genre;
    string? kindFilter; // a tag ("Anime", "TV shows", your own…): only shows with it
    bool prefetching;
    const int ContinueMax = 10;
    static readonly HttpClient CoverHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    // a show or a video as a favourite: on / off (saved). False when it can't be one (the Random card).
    static bool ToggleFavData(object? item)
    {
        var s = Settings.Current;
        if (item is Entry e && !e.IsRandom) { if (!s.Favorites.Remove(e.Folder)) s.Favorites.Add(e.Folder); }
        else if (item is Video v) { if (!s.FavoriteVideos.Remove(v.Path)) s.FavoriteVideos.Add(v.Path); }
        else return false;
        s.Save();
        return true;
    }

    // The section's shows with the filters, search (q) and sort applied.
    List<Entry> Filtered(List<Entry> all, string q)
    {
        IEnumerable<Entry> items = all;
        if (filterFav) items = items.Where(x => !x.IsRandom && (IsFav(x) || x.Videos.Any(IsFav)));
        if (kindFilter != null) items = items.Where(x => x.Tag == kindFilter);
        if (genre != null) items = items.Where(x => InfoService.Cached(x.InfoKey)?.Genres.Contains(genre) == true);
        if (q.Length > 0)
            items = items.Where(x => !x.IsRandom && Matches(x, q));
        items = sortMode switch
        {
            "new" => items.OrderByDescending(x => x.IsRandom).ThenByDescending(x => SafeTime(x.Folder)),
            "top" => items.OrderByDescending(x => x.IsRandom).ThenByDescending(x => InfoService.Cached(x.InfoKey)?.Score ?? -1),
            _ => items.OrderByDescending(x => x.IsRandom).ThenBy(x => x.Title, Natural.I),
        };
        return items.ToList();
    }

    // the section's tags, when it mixes them (Anime / TV shows / Movies first, then your own)
    static List<string> FilterTags(List<Entry> all)
    {
        var builtIn = LibraryFolder.Kinds.Select(k => k.Name).ToList();
        return all.Where(x => !x.IsRandom && x.Tag.Length > 0).Select(x => x.Tag).Distinct()
            .OrderBy(t => builtIn.IndexOf(t) is var i && i >= 0 ? i : 99).ThenBy(t => t, Natural.I).ToList();
    }

    // genres present in the section (from fetched details), most common first
    static List<string> FilterGenres(List<Entry> all) =>
        all.Select(x => InfoService.Cached(x.InfoKey)).OfType<ShowInfo>()
            .SelectMany(i => i.Genres).CountBy(g => g).OrderByDescending(g => g.Value).Take(10).Select(g => g.Key).ToList();

    void ResetFilterState() { filterFav = false; genre = null; kindFilter = null; sortMode = "az"; }

    // What a library sync changed: "2 new shows · 5 new episodes · 1 series removed", or "" when nothing did.
    static string SyncMessage(Dictionary<string, List<Entry>> old, Dictionary<string, List<Entry>> fresh)
    {
        string Diff(bool themes, string seriesWord, string videoWord)
        {
            var o = Everything(old).Where(e => (e.Unit == "themes") == themes).ToList();
            var n = Everything(fresh).Where(e => (e.Unit == "themes") == themes).ToList();
            // name sets: each side is looked up in the other once (was every pair: library size squared)
            var oNames = o.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
            var nNames = n.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
            int newSeries = n.Count(e => !oNames.Contains(e.Name));
            int goneSeries = o.Count(e => !nNames.Contains(e.Name));
            int newVideos = Math.Max(0, n.Sum(e => e.Videos.Count) - o.Sum(e => e.Videos.Count));
            var parts = new List<string>();
            if (newSeries > 0) parts.Add($"{newSeries} new {seriesWord}{(newSeries == 1 ? "" : "s")}");
            if (newVideos > 0) parts.Add($"{newVideos} new {videoWord}{(newVideos == 1 ? "" : "s")}");
            if (goneSeries > 0) parts.Add($"{goneSeries} {seriesWord}{(goneSeries == 1 ? "" : "s")} removed");
            return string.Join(" · ", parts);
        }
        return string.Join(" · ", new[] { Diff(false, "show", "episode"), Diff(true, "series", "theme") }.Where(x => x.Length > 0))
            .Replace("seriess", "series");
    }

    // The line under a cover card: "12 episodes · 3 watched", "Movie", "In progress"...
    public static string CardSubtitle(Entry e)
    {
        if (e.IsRandom || e.Unit == "themes") return e.Count;
        int done = e.Videos.Count(v => Watch.IsDone(v.Path));
        if (e.Kind == "movie") return done > 0 ? "Watched" : e.Videos.Count > 1 ? e.Count : Watch.Progress(e.Videos[0].Path) > 0 ? "In progress" : "Movie";
        return done == 0 ? e.Count : done >= e.Videos.Count ? $"{e.Count} · all watched" : $"{e.Count} · {done} watched";
    }

    static List<IGrouping<int, Video>> Seasons(Entry e) => e.Unit != "episodes" ? new()
        : e.Videos.GroupBy(v => v.Season).OrderBy(g => g.Key < 0 ? int.MaxValue : g.Key).ToList();

    static string SeasonName(int s) => s < 0 ? "Extras" : s == 0 ? "Other episodes" : $"Season {s}";

    // The show's official episodes (aired, specials left out) that have no file in your library, by season.
    static List<(int Season, List<int> Eps, int Total)> Missing(Entry e, Dictionary<string, string> names)
    {
        var have = e.Videos.Where(v => v.Season >= 0 && v.Ep > 0).Select(v => (Math.Max(1, v.Season), v.Ep)).ToHashSet();
        var official = names.Keys.Select(k => k.Split(':')).Where(p => p.Length == 2 && int.TryParse(p[0], out _) && int.TryParse(p[1], out _))
            .Select(p => (S: int.Parse(p[0]), E: int.Parse(p[1]))).Where(x => x.S >= 1).ToList();
        return official.GroupBy(x => x.S).OrderBy(g => g.Key)
            .Select(g => (g.Key, g.Where(x => !have.Contains(x)).Select(x => x.E).OrderBy(x => x).ToList(), g.Count()))
            .Where(x => x.Item2.Count > 0).ToList();
    }

    // 1,2,3,5,7,8 -> "E1–3, E5, E7–8"
    static string Ranges(List<int> eps)
    {
        var parts = new List<string>();
        for (int i = 0; i < eps.Count; i++)
        {
            int start = eps[i];
            while (i + 1 < eps.Count && eps[i + 1] == eps[i] + 1) i++;
            parts.Add(eps[i] == start ? $"E{start}" : $"E{start}–{eps[i]}");
        }
        return string.Join(", ", parts);
    }

    public static bool IsFav(object? item) => item switch
    {
        Entry e => !e.IsRandom && Settings.Current.Favorites.Contains(e.Folder),
        Video v => Settings.Current.FavoriteVideos.Contains(v.Path),
        _ => false,
    };

    List<Entry> LibraryBase()
    {
        if (currentCategory == "All")
            return Everything(lib).OrderBy(x => x.Name, Natural.I).ToList();
        return CategoryList(currentCategory);
    }

    static bool Matches(Entry x, string q)
    {
        var info = InfoService.Cached(x.InfoKey);
        return x.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || x.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
            || (info?.Title?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
            || (info?.Native?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
            || x.Videos.Any(v => v.Title.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    static DateTime SafeTime(string folder)
    {
        try { return folder.Length > 0 ? Directory.GetCreationTime(folder) : DateTime.MinValue; } catch { return DateTime.MinValue; }
    }

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
                OnDetailsFetched(e); // you're looking at it: show the corrected details
                done++;
                if (done % 5 == 0 || done == todo.Count) Toast($"Checking show details · {done} of {todo.Count}");
                await Task.Delay(2000); // AniList allows ~30 requests a minute right now
            }
            if (covers > 0) await SyncLibrary(false); // pick up the new covers
            Toast(covers > 0 ? $"Details ready · {covers} cover{(covers == 1 ? "" : "s")} added" : "Show details ready");
            OnDetailsDone();
        }
        finally { prefetching = false; }
    }

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

    List<Entry> CategoryList(string key)
    {
        var list = lib.GetValueOrDefault(key) ?? new();
        if (list.Where(x => x.Unit == "themes").SelectMany(x => x.Videos).ToList() is { Count: > 0 } themes) // a shuffle of this library's openings
            list = list.Prepend(new Entry("Random", null, themes, "themes", true)).ToList();
        return list;
    }

    // (show, episode to play, resume record if mid-episode, when you last watched it)
    List<(Entry E, int Index, WatchRec? Rec, DateTime Last)> ContinueList()
    {
        var list = new List<(Entry, int, WatchRec?, DateTime)>();
        foreach (var e in Everything(lib).Where(x => x.Unit != "themes"))
        {
            int bi = -1; WatchRec? best = null;
            for (int i = 0; i < e.Videos.Count; i++)
                if (Watch.Get(e.Videos[i].Path) is { } r && (best == null || r.Last > best.Last)) { best = r; bi = i; }
            if (best == null) continue;
            if (Watch.Data.Hidden.TryGetValue(e.Folder, out var hid) && best.Last <= hid) continue; // closed, and not watched since
            if (!best.Done && best.Pos > 5) list.Add((e, bi, best, best.Last)); // mid-episode: resume it
            else if (best.Done && e.Kind != "movie")
            {
                int next = e.Videos.FindIndex(bi + 1, v => v.Season >= 0 && !Watch.IsDone(v.Path)); // finished one: the next unwatched episode
                if (next >= 0) list.Add((e, next, null, best.Last));
            }
        }
        return list.OrderByDescending(x => x.Item4).Take(ContinueMax).ToList();
    }

    // where Play starts: the episode you're part-way through, else the next unwatched one, else the first
    static int ResumeIndex(Entry e)
    {
        int bi = -1; WatchRec? best = null;
        for (int i = 0; i < e.Videos.Count; i++)
            if (Watch.Get(e.Videos[i].Path) is { } r && (best == null || r.Last > best.Last)) { best = r; bi = i; }
        if (best == null || e.Unit == "themes") return 0;
        if (!best.Done) return bi;
        int next = e.Videos.FindIndex(bi + 1, v => v.Season >= 0 && !Watch.IsDone(v.Path));
        return next >= 0 ? next : 0;
    }

    static string WatchedSuffix(IEnumerable<Video> videos)
    {
        var l = videos.ToList();
        int done = l.Count(v => Watch.IsDone(v.Path));
        return done == 0 ? "" : done >= l.Count ? " · all watched" : $" · {done} watched";
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
}
