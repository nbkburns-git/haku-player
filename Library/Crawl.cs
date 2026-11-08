using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace AnimePlayer;

// Finds the shows (or movies) in a library folder however it's laid out:
//   show folders at any depth (inside "A–F", genre or "Cartoons" folders), season folders kept with their show,
//   videos lying loose grouped by the show name in their file names, and movie collections split into movies.
// A folder is a show when it has videos of its own, a season-like folder ("Season 2", "S01", "Specials",
// or its own name plus a season), or a cover / poster / .nfo. A folder without those is a folder of shows when
// it's named like "A" or "0-9", or when most of what's inside looks like shows. Anything else stays one show,
// as before (e.g. "Code Geass\R1", "Code Geass\R2").
public partial class MainWindow
{
    const int CrawlDepth = 6;
    [GeneratedRegex(@"^(?:(?:season|series|staffel|saison|temporada|s)[\s._-]*)?\d{1,3}$|^(?:season|series)\s*\d+\b|^(?:specials?|extras?|featurettes?|bonus(?: features)?|behind the scenes|deleted scenes|interviews|ovas?|oads?|movies?|subs?|subtitles|nc(?:op|ed)s?|creditless)$|^(?:disc|disk|cd|part|vol(?:ume)?)[\s._-]*\d+$", IC)]
    private static partial Regex PureSeason();
    [GeneratedRegex(@"^(?:[a-z0-9#]|[a-z0-9]\s*[-–]\s*[a-z0-9]|#-9)$", IC)] private static partial Regex Letters();
    [GeneratedRegex(@"\b(?:S\d{1,2}|Season\s*\d+)\b", IC)] private static partial Regex SeasonMark();
    [GeneratedRegex(@"^(?:samples?|trailers?)$", IC)] private static partial Regex ExtraDirRx();
    [GeneratedRegex(@"\b(?:part|pt|cd|disc|disk)[\s._-]*\d\b", IC)] private static partial Regex PartRx();

    static bool SkipDir(string d) => Path.GetFileName(d) is var n && (n.StartsWith('.') || n.StartsWith('$') || n is "System Volume Information" or "@eaDir" || ExtraDirRx().IsMatch(n));
    static string[] Dirs(string d) { try { return Directory.GetDirectories(d).Where(x => !SkipDir(x)).ToArray(); } catch { return Array.Empty<string>(); } }
    static List<string> Files(string d) { try { return Directory.GetFiles(d).Where(IsVideo).ToList(); } catch { return new(); } }
    static bool HasVideos(string d) { try { return Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Any(IsVideo); } catch { return false; } }
    static bool IsSeasonName(string d) => PureSeason().IsMatch(Path.GetFileName(d.AsSpan()).Trim());

    // cover.* / poster.* / folder.* / tvshow.nfo / movie.nfo / anilist.txt: one listing of the folder, not one per name
    static bool HasMarker(string d)
    {
        try { return Directory.EnumerateFiles(d).Any(f => IsMarker(Path.GetFileName(f.AsSpan()))); }
        catch { return false; }

        // "cover.*" as Windows reads it: "cover", or "cover." followed by anything
        static bool Stem(ReadOnlySpan<char> n, string stem) =>
            n.StartsWith(stem, StringComparison.OrdinalIgnoreCase) && (n.Length == stem.Length || n[stem.Length] == '.');
        static bool IsMarker(ReadOnlySpan<char> n) =>
            Stem(n, "cover") || Stem(n, "poster") || Stem(n, "folder") ||
            n.Equals("tvshow.nfo", StringComparison.OrdinalIgnoreCase) || n.Equals("movie.nfo", StringComparison.OrdinalIgnoreCase) || n.Equals("anilist.txt", StringComparison.OrdinalIgnoreCase);
    }

    // a folder's name as compared with its sub-folders: "Rick.and.Morty.S01.1080p" -> "rickandmorty"
    static string FolderKey(string d) => Norm(CleanTitle(Path.GetFileName(d)).Title);

    // a folder inside a show that belongs to it: "Season 2", "S01", "Specials", or the show's own name ("Rick.and.Morty.S03…", "Code Geass R2")
    // p: the show's FolderKey, worked out once for all its sub-folders
    static bool PartOf(string p, string child)
    {
        if (IsSeasonName(child)) return true;
        string c = FolderKey(child);
        return p.Length > 0 && (c == p || (Math.Min(p.Length, c.Length) >= 4 && (c.StartsWith(p, StringComparison.Ordinal) || p.StartsWith(c, StringComparison.Ordinal)))); // "A" isn't part of "Archer"
    }

    // does this folder look like one show (or a season of one)?
    static bool LooksLikeShow(string d)
    {
        if (HasMarker(d) || Dirs(d).Any(IsSeasonName)) return true;
        var name = Path.GetFileName(d);
        var title = CleanTitle(name).Title;
        if (SeasonMark().IsMatch(name) && title.Length > 0) return true; // "Rick.and.Morty.S01.1080p"
        var first = Files(d).FirstOrDefault();
        if (first == null) return false;
        string g = Norm(ShowGuess(first)), n = Norm(title);
        return g.Length >= 3 && n.Length >= 3 && (g.Contains(n, StringComparison.Ordinal) || n.Contains(g, StringComparison.Ordinal)); // its episodes are named after it
    }

    // a folder with no videos of its own: is it a folder of shows, or one show split into parts?
    static bool HoldsShows(string d, string[] subs)
    {
        if (subs.Length == 0) return false;
        if (Letters().IsMatch(Path.GetFileName(d.AsSpan()).Trim())) return true;
        var key = FolderKey(d);
        if (subs.Any(s => PartOf(key, s)) || HasMarker(d)) return false;
        int shows = subs.Count(LooksLikeShow);
        return shows > 0 && shows * 2 >= subs.Length;
    }

    // two or more full-length videos with different names, in one folder: a collection, one movie each ("Part 1" / "CD2" stay one movie)
    static bool Collection(List<string> vids)
    {
        if (vids.Count < 2) return false;
        var sizes = vids.Select(f => { try { return new FileInfo(f).Length; } catch { return 0L; } }).ToList();
        long max = sizes.Max();
        var big = vids.Where((f, i) => max > 0 && sizes[i] >= max * 0.4).ToList();
        return big.Count >= 2 && !big.All(f => PartRx().IsMatch(Path.GetFileNameWithoutExtension(f.AsSpan())));
    }

    // Show (or movie) folders under a library folder, and the videos lying loose in folders of shows.
    public static (List<string> Shows, List<string> Loose) Crawl(string root, bool movies)
    {
        var shows = new List<string>();
        var loose = new List<string>();
        if (!Directory.Exists(root)) return (shows, loose);
        var subs = Dirs(root);
        if (!movies && subs.Any(IsSeasonName)) { shows.Add(root); return (shows, loose); } // the library folder is itself one show
        loose.AddRange(Files(root));
        foreach (var s in subs) Walk(s, 1);
        return (shows, loose);

        void Walk(string d, int depth)
        {
            var vids = Files(d);
            var kids = Dirs(d);
            if (movies)
            {
                if (vids.Count == 0)
                {
                    // one folder inside ("Movie and Sample"): still one movie, named after this folder; two or more: a folder of movies
                    if (kids.Count(k => !IsSeasonName(k) && HasVideos(k)) <= 1 || depth >= CrawlDepth) shows.Add(d);
                    else foreach (var k in kids) Walk(k, depth + 1);
                    return;
                }
                if (!Collection(vids)) { shows.Add(d); return; }                                                      // one movie (and its extras)
                loose.AddRange(vids);
                foreach (var k in kids.Where(k => !IsSeasonName(k))) Walk(k, depth + 1);
                return;
            }
            if (vids.Count > 0 || depth >= CrawlDepth || !HoldsShows(d, kids)) { shows.Add(d); return; }
            foreach (var k in kids) Walk(k, depth + 1);
        }
    }

    // Loose episodes grouped by show. Folder is a made-up path, so favourites and details still have a key for each group.
    // Each file's show is guessed once (it was guessed again for the group's name).
    static IEnumerable<(string Name, string Folder, List<string> Files)> LooseShows(List<string> loose, bool theme) =>
        loose.Select(f => (File: f, Dir: Path.GetDirectoryName(f)!, Guess: ShowGuess(f, theme)))
            .GroupBy(x => (x.Dir, Key: Norm(x.Guess) is { Length: > 0 } k ? k : Norm(Path.GetFileName(x.Dir))))
            .Select(g =>
            {
                var name = g.Select(x => x.Guess).FirstOrDefault(x => x.Length > 0) ?? Path.GetFileName(g.Key.Dir);
                return (name, Path.Combine(g.Key.Dir, "[loose] " + name), g.Select(x => x.File).OrderBy(f => f, Natural.I).ToList());
            });
}
