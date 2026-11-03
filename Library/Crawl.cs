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
    static readonly Regex PureSeason = new(@"^(?:(?:season|series|staffel|saison|temporada|s)[\s._-]*)?\d{1,3}$|^(?:season|series)\s*\d+\b|^(?:specials?|extras?|featurettes?|bonus(?: features)?|behind the scenes|deleted scenes|interviews|ovas?|oads?|movies?|subs?|subtitles|nc(?:op|ed)s?|creditless)$|^(?:disc|disk|cd|part|vol(?:ume)?)[\s._-]*\d+$", RegexOptions.IgnoreCase);
    static readonly Regex Letters = new(@"^(?:[a-z0-9#]|[a-z0-9]\s*[-–]\s*[a-z0-9]|#-9)$", RegexOptions.IgnoreCase);
    static readonly Regex SeasonMark = new(@"\b(?:S\d{1,2}|Season\s*\d+)\b", RegexOptions.IgnoreCase);
    static readonly string[] Markers = { "cover.*", "poster.*", "folder.*", "tvshow.nfo", "movie.nfo", "anilist.txt" };

    static bool IsVideo(string f) => VideoExt.Contains(Path.GetExtension(f).ToLowerInvariant()) && !Regex.IsMatch(Path.GetFileNameWithoutExtension(f), @"\bsample\b", I);
    static bool SkipDir(string d) => Path.GetFileName(d) is var n && (n.StartsWith('.') || n.StartsWith('$') || n is "System Volume Information" or "@eaDir" || Regex.IsMatch(n, @"^(?:samples?|trailers?)$", I));
    static string[] Dirs(string d) { try { return Directory.GetDirectories(d).Where(x => !SkipDir(x)).ToArray(); } catch { return Array.Empty<string>(); } }
    static List<string> Files(string d) { try { return Directory.GetFiles(d).Where(IsVideo).ToList(); } catch { return new(); } }
    static bool HasVideos(string d) { try { return Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Any(IsVideo); } catch { return false; } }
    static bool HasMarker(string d) { try { return Markers.Any(m => Directory.EnumerateFiles(d, m).Any()); } catch { return false; } }
    static string Norm(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]", "");
    static bool IsSeasonName(string d) => PureSeason.IsMatch(Path.GetFileName(d).Trim());

    // the show's name from an episode file: "[Grp] Naruto - 012 [1080p]" -> "Naruto", "Rick.and.Morty.S01E02…" -> "Rick and Morty"
    public static string ShowGuess(string file, bool theme = false)
    {
        if (theme) return Friendly(file).Split(" – ")[0].Trim();
        var n = Regex.Replace(Path.GetFileNameWithoutExtension(file), @"\[[^\]]*\]", " ");
        var m = Regex.Match(n, @"S\d{1,2}\s*[-–]?\s*E\d{1,3}|\b\d{1,2}x\d{2,3}\b|\b(?:Episode|Ep|E)[\s._-]?\d{1,4}\b|\s[-–]\s*\d{1,4}\b|[\s._]\d{2,4}(?=\s*(?:\(|v\d|$))", I);
        return CleanTitle(m.Success ? n[..m.Index] : n, false).Title.Trim(' ', '-', '–', '.', '_');
    }

    // a folder inside a show that belongs to it: "Season 2", "S01", "Specials", or the show's own name ("Rick.and.Morty.S03…", "Code Geass R2")
    static bool PartOf(string show, string child)
    {
        if (IsSeasonName(child)) return true;
        string p = Norm(CleanTitle(Path.GetFileName(show)).Title), c = Norm(CleanTitle(Path.GetFileName(child)).Title);
        return p.Length > 0 && (c == p || (Math.Min(p.Length, c.Length) >= 4 && (c.StartsWith(p) || p.StartsWith(c)))); // "A" isn't part of "Archer"
    }

    // does this folder look like one show (or a season of one)?
    static bool LooksLikeShow(string d)
    {
        if (HasMarker(d) || Dirs(d).Any(IsSeasonName)) return true;
        var name = Path.GetFileName(d);
        if (SeasonMark.IsMatch(name) && CleanTitle(name).Title.Length > 0) return true; // "Rick.and.Morty.S01.1080p"
        var first = Files(d).FirstOrDefault();
        if (first == null) return false;
        string g = Norm(ShowGuess(first)), n = Norm(CleanTitle(name).Title);
        return g.Length >= 3 && n.Length >= 3 && (g.Contains(n) || n.Contains(g)); // its episodes are named after it
    }

    // a folder with no videos of its own: is it a folder of shows, or one show split into parts?
    static bool HoldsShows(string d, string[] subs)
    {
        if (subs.Length == 0) return false;
        if (Letters.IsMatch(Path.GetFileName(d).Trim())) return true;
        if (subs.Any(s => PartOf(d, s)) || HasMarker(d)) return false;
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
        return big.Count >= 2 && !big.All(f => Regex.IsMatch(Path.GetFileNameWithoutExtension(f), @"\b(?:part|pt|cd|disc|disk)[\s._-]*\d\b", I));
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
    static IEnumerable<(string Name, string Folder, List<string> Files)> LooseShows(List<string> loose, bool theme) =>
        loose.GroupBy(f => (Dir: Path.GetDirectoryName(f)!, Key: Norm(ShowGuess(f, theme)) is { Length: > 0 } k ? k : Norm(Path.GetFileName(Path.GetDirectoryName(f)!))))
            .Select(g =>
            {
                var name = g.Select(f => ShowGuess(f, theme)).FirstOrDefault(x => x.Length > 0) ?? Path.GetFileName(g.Key.Dir);
                return (name, Path.Combine(g.Key.Dir, "[loose] " + name), g.OrderBy(f => f, Natural.I).ToList());
            });
}
