using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AnimePlayer;

// The library scan: shows, movies and openings in the library folders, with their covers. No UI in here, so the coming
// WinUI app shares it (docs/winui3-migration-plan.md). Entry.Cover is whatever image type the app shows: each app decodes
// covers its own way (DecodeImage / SaveSmallCover), on the scan's worker threads, so startup stays fast in both.

public partial class MainWindow
{
    // Every library, keyed by its id: the shows in all its folders, each tagged with its folder's tag.
    static Dictionary<string, List<Entry>> ScanAll()
    {
        var scanned = new Dictionary<(string, string), List<Entry>>(); // a folder in two libraries is read once
        var result = new Dictionary<string, List<Entry>>();
        foreach (var l in Libs.ToList())
        {
            var list = new List<Entry>();
            foreach (var f in l.Folders.ToList())
            {
                var key = (f.Path.TrimEnd('\\').ToLowerInvariant(), f.Kind);
                if (!scanned.TryGetValue(key, out var got))
                {
                    try { got = ScanFolder(f); } catch { got = new(); } // a drive that isn't plugged in: skip it, keep the rest
                    scanned[key] = got;
                }
                list.AddRange(got.Select(e => e with { Tag = f.TagName }));
            }
            result[l.Id] = list.DistinctBy(e => (e.Folder, e.Unit)).OrderBy(e => e.Name, Natural.I).ToList();
        }
        return result;
    }

    static List<Entry> ScanFolder(LibraryFolder f) => f.Kind switch
    {
        "tv" => ScanTv(f.Path),
        "movie" => ScanMovies(f.Path),
        "themes" => Scan(Inner(f.Path, "Anime Openings"), "themes"),
        _ => Scan(Inner(f.Path, "Anime Shows"), "episodes"),
    };

    // a whole anime folder (Anime Shows + Anime Openings inside) works too: use the part that fits
    static string Inner(string dir, string sub) => Directory.Exists(Path.Combine(dir, sub)) ? Path.Combine(dir, sub) : dir;

    // every show once, even when it's in more than one library
    static IEnumerable<Entry> Everything(Dictionary<string, List<Entry>> l) => l.Values.SelectMany(x => x).DistinctBy(e => (e.Folder, e.Unit));



    // Videos in a folder, without sample clips.
    static IEnumerable<string> VideosIn(string dir) => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Where(IsVideo);

    // Official episode names (from the show's details) in place of the file names. Only in the app: files aren't renamed.
    // A file with no season ("Episode 03") is matched to season 1, not to specials (season 0).
    public static void NameEpisodes(Entry e)
    {
        if (e.Kind == "movie" || e.Unit != "episodes" || InfoService.Cached(e.InfoKey)?.EpisodeNames is not { Count: > 0 } names) return; // not movies, not openings
        for (int i = 0; i < e.Videos.Count; i++)
        {
            var v = e.Videos[i];
            if (v.Season >= 0 && v.Ep > 0 && names.TryGetValue($"{Math.Max(1, v.Season)}:{v.Ep}", out var name)) e.Videos[i] = v with { Title = name };
        }
    }

    static Video WithSeason(Video v) => WithOrder(v, EpisodeOrder(v.Path));

    static Video WithOrder(Video v, (int S, int E) order)
    {
        var (s, e) = order;
        return v with { Season = s == int.MaxValue ? -1 : s, Ep = s == int.MaxValue || e == 999 ? 0 : e };
    }

    // TV shows: show folders found by Crawl (any depth), plus loose episodes grouped by show; seasons of the same show
    // in separate folders ("Rick.and.Morty.S01…", "Rick and Morty Season 9") merge.
    static List<Entry> ScanTv(string dir)
    {
        var (shows, loose) = Crawl(dir, false);
        var items = shows.Select(d => (Key: CleanTitle(Path.GetFileName(d)).Title, Folder: d, Files: (List<string>?)null))
            .Concat(LooseShows(loose, false).Select(g => (Key: CleanTitle(g.Name).Title, g.Folder, Files: (List<string>?)g.Files)));
        return items
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .AsParallel()
            .Select(g =>
            {
                var parts = g.OrderBy(x => x.Files != null).ThenBy(x => Path.GetFileName(x.Folder), Natural.I).ToList(); // real folders first
                var videos = parts.SelectMany(x => x.Files ?? VideosIn(x.Folder)).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(f => (File: f, Order: EpisodeOrder(f))).OrderBy(x => x.Order).ThenBy(x => x.File, Natural.I)
                    .Select((x, i) => WithOrder(new Video(x.File, EpisodeTitle(x.File), i + 1), x.Order)).ToList();
                var e = new Entry(g.Key, null, videos, "episodes", false, parts[0].Folder, "tv");
                NameEpisodes(e);
                return e with { Cover = LoadCover(parts[0].Folder) ?? LoadCoverFile(CoverFile(e.InfoKey)) };
            })
            .Where(e => e.Videos.Count > 0)
            .ToList();
    }

    // Movies: a folder per movie (biggest file first: the film, then any extras) or a video file on its own,
    // at any depth; a folder of several full-length films is a collection (one movie each).
    static List<Entry> ScanMovies(string dir)
    {
        var (shows, loose) = Crawl(dir, true);
        var folders = shows.Select(d => (Path: d, Name: Path.GetFileName(d), Files: VideosIn(d).OrderByDescending(f => new FileInfo(f).Length).ToList()));
        var files = loose.Select(f => (Path: f, Name: Path.GetFileNameWithoutExtension(f), Files: new List<string> { f }));
        return folders.Concat(files).Where(x => x.Files.Count > 0).AsParallel().Select(x =>
        {
            var (title, year) = CleanTitle(x.Name);
            var videos = x.Files.Select((f, i) => new Video(f, i == 0 ? (year != null ? $"{title} ({year})" : title) : CleanTitle(Path.GetFileNameWithoutExtension(f)).Title, i + 1)).ToList();
            var e = new Entry(title, null, videos, "videos", false, x.Path, "movie");
            return e with { Cover = LoadCover(x.Path) ?? LoadCoverFile(CoverFile(e.InfoKey)) };
        }).ToList();
    }

    // Covers fetched for TV shows and movies live in the app's data folder (nothing is written into your media folders).
    public static string CoverFile(string infoKey) => Path.Combine(DataDir, "covers", SafeName(infoKey) + ".jpg");

    // Anime shows ("episodes") and openings / endings ("themes"): show folders found by Crawl, plus loose files grouped by show.
    static List<Entry> Scan(string dir, string unit)
    {
        bool themes = unit == "themes";
        var (shows, loose) = Crawl(dir, false);
        Video Make(string f, int i) => themes ? new Video(f, Friendly(f), i + 1) : WithSeason(new Video(f, Friendly(f), i + 1));
        var folders = shows.Select(d => (Name: Path.GetFileName(d), Folder: d, Files: VideosIn(d).OrderBy(f => Path.GetRelativePath(d, f), Natural.I).ToList()));
        return folders.Concat(LooseShows(loose, themes))
            .OrderBy(x => x.Name, Natural.I)
            .AsParallel().AsOrdered() // covers decode in parallel: the slow part of startup
            .Select(x => new Entry(x.Name, LoadCover(x.Folder) ?? LoadCoverFile(CoverFile(x.Name)), x.Files.Select(Make).ToList(), unit, false, x.Folder))
            .Where(e => e.Videos.Count > 0)
            .Select(e => { NameEpisodes(e); return e; }) // anime shows get official episode names too (openings don't)
            .ToList();
    }

    // cover.jpg / poster.png / folder.jpg in the show's folder (none for loose files)
    static object? LoadCover(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        // one listing of the folder (it was listed once per name): the first cover.*, else poster.*, else folder.*
        string? best = null; int rank = int.MaxValue;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                var n = Path.GetFileName(f.AsSpan());
                if (!CoverExt.Contains(Path.GetExtension(n))) continue;
                int r = n.StartsWith("cover.", StringComparison.OrdinalIgnoreCase) ? 0
                      : n.StartsWith("poster.", StringComparison.OrdinalIgnoreCase) ? 1
                      : n.StartsWith("folder.", StringComparison.OrdinalIgnoreCase) ? 2 : int.MaxValue;
                if (r < rank) { rank = r; best = f; if (r == 0) break; }
            }
        }
        catch { return null; }
        return LoadCoverFile(best);
    }

    // A cover at card size (420 px wide, 2x the card). Most covers are big PNGs (up to 2047x2894, ~0.5 MB), and a PNG
    // can't be decoded at a smaller size: every start decoded each one in full and shrank it, ~2.4 s of CPU for 176 covers
    // (the bulk of the library scan). So the shrunk copy is kept in the app's data folder (covers-small) and read from then on.
    // Its name comes from the cover's path, size and time, so a replaced cover gets a new copy.
    static readonly string SmallCovers = Path.Combine(Settings.DataDir, "covers-small"); // Settings.DataDir: static fields in other files of this partial class may not be set yet

    static object? LoadCoverFile(string? f)
    {
        if (f == null) return null;
        var fi = new FileInfo(f);
        if (!fi.Exists) return null;
        var key = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes($"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}")))[..16];
        var jpg = Path.Combine(SmallCovers, key + ".jpg");
        var png = Path.Combine(SmallCovers, key + ".png"); // covers with see-through parts keep them
        if (DecodeImage(File.Exists(jpg) ? jpg : File.Exists(png) ? png : null, 0) is { } small) return small;
        var full = DecodeImage(f, 420);
        if (full != null) SaveSmallCover(full, jpg, png);
        return full;
    }

    // the app's own image decoding (MainWindow.xaml.cs for WPF): null if the file can't be read
    private static partial object? DecodeImage(string? f, int width);
    private static partial void SaveSmallCover(object image, string jpg, string png);
}
