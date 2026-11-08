using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AnimePlayer;

// Show details (description, genres, year, studio, score, banner) from AniList's free public API,
// cached in data\info, so later visits are instant and offline. Which AniList entry, best first:
//   1. your own pick ("Wrong show info?" on the show page) or an "anilist.txt" with an id in the folder
//   2. AnimeThemes: opening/ending files keep their AnimeThemes names, and AnimeThemes knows each one's AniList id (exact)
//   3. AniList search by name, ranked by title match, popularity and kind of release
public sealed record ShowInfo(string? Title, string? Native, string? Description, string[] Genres, string? Format,
                              int? Year, int? Episodes, string? Studio, double? Score, string? BannerFile, string? CoverUrl = null,
                              int? AniListId = null, string? Source = null, // Source: "manual" / "animethemes" / "search" / "cinemeta"; null = older cache
                              string? ImdbId = null,
                              Dictionary<string, string>? EpisodeNames = null, // official episode names, key "season:episode" (season 0 = specials)
                              int Matcher = 0); // the search's version (InfoService.MatcherVersion) when Source is "search"

// A search result for picking the right show by hand.
public sealed record ShowChoice(string Id, string Title, string? Other, int? Year, string? Format, int? Episodes, string? Cover); // Id: AniList id or IMDb id

static partial class InfoService
{
    // patterns compiled into the app at build time (see Names.cs)
    const RegexOptions IC = RegexOptions.IgnoreCase;
    [GeneratedRegex(@"^[A-Za-z0-9]+-(OP|ED)\d*\b")] private static partial Regex ThemeFileRx();
    [GeneratedRegex(@"\s+")] private static partial Regex SpacesRx();
    [GeneratedRegex(@"\b(complete( series| collection)?|the complete series|collection|batch|dual[ -]?audio|dubbed|subbed|eng ?(dub|sub)|uncensored|remastered|bd|dvd|tv series|anime)\b", IC)]
    private static partial Regex NoiseRx();
    [GeneratedRegex(@"\([^)]*\)")] private static partial Regex ParensRx();
    [GeneratedRegex(@"^(Season|S)\s*\d+|^Series\s*\d+", IC)] private static partial Regex SeasonDirRx();
    [GeneratedRegex(@"-(OP|ED)\d|\b(NC)?(OP|ED)\d*\b.*creditless|\bsample\b", IC)] private static partial Regex NotEpisodeRx();
    [GeneratedRegex(@"\bS(\d+)\b")] private static partial Regex SeasonTagRx();
    [GeneratedRegex(@"(?<=[a-z])(?=[A-Z])")] private static partial Regex CamelRx();
    [GeneratedRegex(@"\s*\bS\d+\b")] private static partial Regex SeasonTagSpacedRx();
    [GeneratedRegex(@"\bSeason \d+\b")] private static partial Regex SeasonWordRx();
    [GeneratedRegex(@"\s*\((19|20)\d\d\)\s*$")] private static partial Regex TitleYearRx();
    [GeneratedRegex(@"<br\s*/?>", IC)] private static partial Regex BreakRx();
    [GeneratedRegex("<[^>]+>")] private static partial Regex TagRx();
    [GeneratedRegex(@"\(Source:[^)]*\)")] private static partial Regex SourceNoteRx();
    [GeneratedRegex(@"\n{3,}")] private static partial Regex BlankLinesRx();
    [GeneratedRegex(@"(19|20)\d\d")] private static partial Regex YearRx();
    [GeneratedRegex(@"\._V1_[^/]*\.jpg$")] private static partial Regex ImdbSizeRx();

    static readonly string Dir = Path.Combine(Settings.DataDir, "info");
    static readonly HttpClient Http = MakeHttp();
    static HttpClient MakeHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("HakuPlayer/1.0");
        return h;
    }
    const string Fields = "id title{romaji english native} synonyms format startDate{year} episodes averageScore popularity genres studios(isMain:true){nodes{name}} bannerImage coverImage{extraLarge} description(asHtml:false)";

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ShowInfo?> Memo = new();

    static string FileFor(string name) => Path.Combine(Dir, MainWindow.SafeName(name) + ".json");

    // Already-fetched details, without touching the network (for search, genres, sorting).
    public static ShowInfo? Cached(string name)
    {
        if (Memo.TryGetValue(name, out var hit)) return hit;
        try
        {
            var file = FileFor(name);
            // remembered as "none" too: the library page asks for every show's details on each filter and sort,
            // and a show without any cost a disk check every time (only Refresh writes these files, and it updates Memo)
            if (!File.Exists(file)) { Memo[name] = null; return null; }
            var info = JsonSerializer.Deserialize<ShowInfo>(File.ReadAllText(file));
            Memo[name] = info;
            return info;
        }
        catch { return null; }
    }

    // v1: a year in the name now outweighs an exact title ("Hunter x Hunter (2011)" had been matched to the 1999 series)
    const int MatcherVersion = 1;

    // An automatic match from an older search is looked up again once (keeping it if that fails); your picks never are.
    public static async Task<ShowInfo?> Get(Entry e) =>
        Cached(e.InfoKey) is ShowInfo hit && !(hit.Source == "search" && hit.Matcher < MatcherVersion) ? hit : await Refresh(e);

    // Look it up again (ignoring the cache) and save the result; keeps the old details if this fails.
    public static async Task<ShowInfo?> Refresh(Entry e)
    {
        var name = e.InfoKey;
        var key = MainWindow.SafeName(name);
        var file = Path.Combine(Dir, key + ".json");
        try
        {
            ShowInfo info;
            string? banner;
            if (e.Kind == "anime")
            {
                var (media, source) = await Lookup(e.Folder, e.Name);
                if (media == null) return Cached(name);
                info = ToInfo(media) with { Source = source, Matcher = MatcherVersion };
                banner = media["bannerImage"]?.GetValue<string>();
                // AniList has no episode titles: get them from the same show on Cinemeta (IMDb), by its English title.
                // An empty list marks "looked, nothing found", so it isn't retried on every start.
                if (e.Unit == "episodes")
                {
                    Dictionary<string, string>? names = null;
                    try
                    {
                        var meta = await FromCinemeta(e with { Kind = "tv", Name = info.Title ?? e.Name }, info.Year);
                        if (meta != null) names = FromMeta(meta, "tv").EpisodeNames;
                    }
                    catch { }
                    info = info with { EpisodeNames = names ?? new() };
                }
                // an anime's openings share its details: refreshing them must not drop the show's episode names
                else info = info with { EpisodeNames = Cached(name)?.EpisodeNames };
            }
            else
            {
                var meta = await FromCinemeta(e);
                if (meta == null) return Cached(name);
                info = FromMeta(meta, e.Kind);
                banner = Str(meta["background"]);
                // Cinemeta's poster host (metahub) has wrong art for some titles (Band of Brothers got a Brazilian soap);
                // IMDb's own listing for the same id is the official poster, so prefer that.
                if (await ImdbPoster(info.ImdbId) is string poster) info = info with { CoverUrl = poster };
            }
            if (banner != null)
            {
                var ext = Path.GetExtension(new Uri(banner).AbsolutePath);
                var img = Path.Combine(Dir, key + (ext.Length is > 1 and <= 5 ? ext : ".jpg"));
                Directory.CreateDirectory(Dir);
                try { await File.WriteAllBytesAsync(img, await Http.GetByteArrayAsync(banner)); info = info with { BannerFile = img }; }
                catch { } // no banner: keep the rest of the details
            }
            Directory.CreateDirectory(Dir);
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(info));
            Memo[name] = info;
            return info;
        }
        catch { return Cached(name); } // offline or not found: keep what we had (or show the page without details)
    }

    // awaited, not ContinueWith(t => t.Result): a failed request came back wrapped in an AggregateException
    static async Task<JsonNode?> ById(int id) =>
        (await Query($"query($id:Int){{Media(id:$id,type:ANIME){{{Fields}}}}}", new JsonObject { ["id"] = id }))?["Media"];

    static async Task<(JsonNode? Media, string Source)> Lookup(string folder, string name)
    {
        // 1. your own pick
        if (Settings.Current.InfoOverrides.TryGetValue(folder, out var mine)) return (await ById(mine), "manual");
        var idFile = Path.Combine(folder, "anilist.txt");
        if (File.Exists(idFile) && int.TryParse((await File.ReadAllTextAsync(idFile)).Trim(), out var id))
            return (await ById(id), "manual");
        // 2. AnimeThemes, from the opening/ending file names
        if (await FromAnimeThemes(folder) is int at && await ById(at) is JsonNode exact) return (exact, "animethemes");
        // 3. search
        return (await Search(folder, name), "search");
    }

    // Files like "KimetsuNoYaiba-OP1-NCBD1080-Encoded…mp4" are AnimeThemes videos ("KimetsuNoYaiba-OP1-NCBD1080.webm").
    // AnimeThemes says which show each belongs to; for a folder we take the main TV series (the earliest, if several seasons).
    static async Task<int?> FromAnimeThemes(string folder)
    {
        try
        {
            var stems = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(f => MainWindow.IsVideoExt(f))
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .Select(n => n.IndexOf("-Encoded", StringComparison.OrdinalIgnoreCase) is var i and > 0 ? n[..i] : n)
                .Where(n => ThemeFileRx().IsMatch(n))
                .Distinct().Take(6).ToList();
            var shows = new List<(string Slug, int Year, string Format)>();
            foreach (var b in stems)
            {
                var v = await GetJson($"https://api.animethemes.moe/video/{Uri.EscapeDataString(b)}.webm?include=animethemeentries.animetheme.anime");
                foreach (var e in v?["video"]?["animethemeentries"]?.AsArray() ?? new JsonArray())
                    if (e?["animetheme"]?["anime"] is JsonNode a && a["slug"]?.GetValue<string>() is string slug)
                        shows.Add((slug, a["year"]?.GetValue<int?>() ?? 9999, a["media_format"]?.GetValue<string>() ?? ""));
                await Task.Delay(300); // be gentle with their free API
            }
            if (shows.Count == 0) return null;
            var pick = shows.Distinct().OrderByDescending(s => s.Format == "TV").ThenBy(s => s.Year).First();
            var res = await GetJson($"https://api.animethemes.moe/anime/{pick.Slug}?include=resources");
            var al = res?["anime"]?["resources"]?.AsArray().FirstOrDefault(r => r?["site"]?.GetValue<string>() == "AniList");
            return al?["external_id"]?.GetValue<int?>();
        }
        catch { return null; }
    }

    static async Task<JsonNode?> GetJson(string url)
    {
        using var res = await Http.GetAsync(url);
        return res.IsSuccessStatusCode ? await ParseBody(res) : null;
    }

    // ---------- names to search for ----------

    // Folders like "Naruto Complete", "Monster 2004 1080p" or "Neon Genesis Evangelion Complete Series" find nothing as they
    // are, so the search tries, in order: the folder name cleaned up (no quality, year, "Complete"...), the series name the
    // episode files carry ("[CBM]_Monster_-_01_-_Herr_Dr._Tenma_[5CBB3C92].mkv" -> "Monster"), then the raw folder name.
    static List<string> SearchNames(string folder, string name, out int? year)
    {
        var clean = MainWindow.CleanTitle(name);
        year = clean.Year;
        var names = new List<string> { StripNoise(clean.Title) };
        if (SeriesFromFiles(folder) is { } fromFiles)
        {
            names.Add(fromFiles.Title);
            year ??= fromFiles.Year;
        }
        names.Add(name);
        return names.Select(n => SpacesRx().Replace(n, " ").Trim(' ', '-', ',')).Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Leftover release words CleanTitle doesn't cut at ("... Complete Series", "... Dual Audio").
    static string StripNoise(string s) => NoiseRx().Replace(s, " ");

    // The series name in an episode's file name, or null for a name that doesn't look like an episode.
    internal static (string Title, int? Year)? SeriesFromFile(string file)
    {
        var n = Path.GetFileName(file).Replace('_', ' '); // "[CBM]_Monster_-_01_-_..." -> "[CBM] Monster - 01 - ..."
        var title = SpacesRx().Replace(StripNoise(ParensRx().Replace(MainWindow.ShowGuess(n), " ")), " ").Trim(' ', '-');
        return title.Length > 1 ? (title, MainWindow.CleanTitle(Path.GetFileNameWithoutExtension(n)).Year) : null;
    }

    // The name most episode files agree on (at least half of a sample of them).
    static (string Title, int? Year)? SeriesFromFiles(string folder)
    {
        var found = EpisodeFiles(folder).Take(40).Select(SeriesFromFile).Where(x => x != null).Select(x => x!.Value).ToList();
        if (found.Count == 0) return null;
        var top = found.GroupBy(x => Norm(x.Title)).Select(g => g.ToList()).MaxBy(g => g.Count)!; // the first of the biggest, as before
        return top.Count * 2 >= found.Count ? (top[0].Title, top.Select(x => x.Year).FirstOrDefault(y => y != null)) : null;
    }

    // A show's episodes: videos in the folder itself or a "Season N" folder, not "Movies", "NC" (creditless OP/ED),
    // "Extras" and the like, and not opening/ending files.
    static IEnumerable<string> EpisodeFiles(string folder)
    {
        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories(folder).Where(d => SeasonDirRx().IsMatch(Path.GetFileName(d.AsSpan()))).Prepend(folder).ToList(); }
        catch { yield break; }
        foreach (var d in dirs)
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(d).ToList(); } catch { continue; }
            foreach (var f in files)
                if (MainWindow.IsVideoExt(f) && !NotEpisodeRx().IsMatch(Path.GetFileName(f.AsSpan())))
                    yield return f;
        }
    }

    // Pick from AniList's search results. Exact title matches win, but a mere nickname match on an obscure
    // entry (e.g. "Demon Slayer" is also a synonym of the 2016 short "Onigiri") loses to the hugely popular series.
    static async Task<JsonNode?> Search(string folder, string name)
    {
        int files = 0;
        try { files = EpisodeFiles(folder).Count(); } catch { }
        int season = SeasonTagRx().Match(name) is { Success: true } sm ? int.Parse(sm.Groups[1].ValueSpan) : 0;
        // "2nd Season" / "Season 2" / "2" in a result's title: built once per search, not once per result
        var seasonRx = season > 1 ? new Regex($@"\b({season}(st|nd|rd|th)? Season|Season {season}|{season})\b", RegexOptions.IgnoreCase) : null;
        // each name, then friendlier spellings of it: "SteinsGate" -> "Steins Gate", "Haikyuu S2" -> "Haikyuu Season 2"
        var tries = new List<string>();
        foreach (var n in SearchNames(folder, name, out var year))
        {
            var spaced = CamelRx().Replace(n, " ");
            tries.AddRange(new[] { n, spaced, SeasonTagRx().Replace(spaced, "Season $1"), SeasonTagSpacedRx().Replace(spaced, "") });
        }
        tries = tries.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
        int? yearHint = MainWindow.CleanTitle(name).Year ?? SeriesFromFiles(folder)?.Year;

        foreach (var q in tries)
        {
            var page = await Query($"query($s:String){{Page(perPage:8){{media(search:$s,type:ANIME,sort:SEARCH_MATCH){{{Fields}}}}}}}", new JsonObject { ["s"] = q });
            var list = page?["Page"]?["media"]?.AsArray();
            if (list == null || list.Count == 0) continue; // nothing for this spelling: try the next
            string want = Norm(SeasonWordRx().Replace(q, ""));
            return list.Select((m, idx) => (m, idx)).OrderByDescending(t =>
            {
                var m = t.m!;
                var main = new[] { m["title"]?["english"], m["title"]?["romaji"] }.Select(x => x?.GetValue<string>()).Where(x => x != null).Select(x => NormTitle(x!)).ToList();
                var syn = m["synonyms"]?.AsArray().Select(x => x?.GetValue<string>()).Where(x => x != null).Select(x => Norm(x!)).ToList() ?? new();
                double score = main.Contains(want) ? 150 : main.Any(x => x.StartsWith(want)) ? 50 : syn.Contains(want) ? 45 : 0;
                score += m["format"]?.GetValue<string>() switch { "TV" => 20, "MOVIE" => 5, "TV_SHORT" => -15, "MUSIC" => -40, null => 0, _ => -20 }; // OVA, ONA, SPECIAL
                score += (8 - t.idx) * 3;                                 // AniList's own relevance order
                score += 15 * Math.Log10(1 + (m["popularity"]?.GetValue<int>() ?? 0)); // ~90 for a hit series, ~45 for an obscure one
                // a show folder with N episodes should match an entry with at least about N episodes
                if (files > 3 && m["episodes"]?.GetValue<int?>() is int eps) score += eps + 2 >= files ? 10 : -15;
                // a year in the folder or file names ("Monster 2004", "Hunter x Hunter (2011)") picks that version
                if (yearHint != null && m["startDate"]?["year"]?.GetValue<int?>() is int y) score += Math.Abs(y - yearHint.Value) <= 1 ? 40 : -20;
                if (seasonRx != null)
                {
                    var all = string.Join(" ", new[] { m["title"]?["english"], m["title"]?["romaji"] }.Select(x => x?.GetValue<string>() ?? ""));
                    if (seasonRx.IsMatch(all)) score += 60;
                }
                return score;
            }).First().m;
        }
        return null;
    }

    static string Norm(string s) => MainWindow.Norm(s);
    // a title without the year AniList adds to tell remakes apart: "Hunter x Hunter (2011)" is as exact a match for
    // "Hunter x Hunter" as the 1999 series, so the year in your folder name decides between them
    internal static string NormTitle(string s) => Norm(TitleYearRx().Replace(s, ""));

    static async Task<JsonNode?> Query(string query, JsonObject vars)
    {
        var body = new JsonObject { ["query"] = query, ["variables"] = vars }.ToJsonString();
        using var res = await Http.PostAsync("https://graphql.anilist.co", new StringContent(body, Encoding.UTF8, "application/json"));
        if (!res.IsSuccessStatusCode) return null;
        return (await ParseBody(res))?["data"];
    }

    // Parsed straight from the network stream: a long show's Cinemeta details run to a megabyte or more,
    // and reading them into one string first made a second full copy (on the large-object heap).
    static async Task<JsonNode?> ParseBody(HttpResponseMessage res)
    {
        await using var body = await res.Content.ReadAsStreamAsync();
        return await JsonNode.ParseAsync(body);
    }

    static ShowInfo ToInfo(JsonNode m)
    {
        string? S(JsonNode? n) => n?.GetValue<string>();
        var desc = S(m["description"]);
        if (desc != null)
        {
            desc = BreakRx().Replace(desc, "\n");
            desc = TagRx().Replace(desc, "");
            desc = System.Net.WebUtility.HtmlDecode(desc);
            desc = SourceNoteRx().Replace(desc, "").Trim();
            desc = BlankLinesRx().Replace(desc, "\n\n");
        }
        var format = S(m["format"]) switch { "TV" => "TV series", "TV_SHORT" => "TV short", "MOVIE" => "Movie", "ONA" => "Web series", var f => f };
        int? score = m["averageScore"]?.GetValue<int?>();
        return new ShowInfo(
            S(m["title"]?["english"]) ?? S(m["title"]?["romaji"]), S(m["title"]?["native"]), desc,
            m["genres"]?.AsArray().Select(g => g!.GetValue<string>()).ToArray() ?? Array.Empty<string>(),
            format, m["startDate"]?["year"]?.GetValue<int?>(), m["episodes"]?.GetValue<int?>(),
            S(m["studios"]?["nodes"]?.AsArray().FirstOrDefault()?["name"]), score / 10.0, null, S(m["coverImage"]?["extraLarge"]),
            m["id"]?.GetValue<int?>());
    }

    // ---------- TV shows and movies: Cinemeta (free, keyless IMDb-based metadata) ----------

    const string Cinemeta = "https://v3-cinemeta.strem.io";
    static string? Str(JsonNode? n) => n is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToString()) : null;
    static int? YearOf(string? s) => s != null && YearRx().Match(s) is { Success: true } m ? int.Parse(m.ValueSpan) : null;

    // Your pick, else the search result whose title (and year, from the folder name) fits best.
    static async Task<JsonNode?> FromCinemeta(Entry e, int? yearHint = null)
    {
        string type = e.Kind == "movie" ? "movie" : "series";
        if (!Settings.Current.MediaOverrides.TryGetValue(e.Folder, out var id))
        {
            // the folder's name, else (for a badly named folder) the series name its episode files carry
            List<JsonNode>? metas = null;
            string query = e.Name;
            int? fileYear = null;
            var names = new List<string> { e.Name };
            if (e.Kind == "tv" && Directory.Exists(e.Folder) && SeriesFromFiles(e.Folder) is { } ff) { names.Add(ff.Title); fileYear = ff.Year; }
            foreach (var n in names.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var res = await GetJson($"{Cinemeta}/catalog/{type}/top/search={Uri.EscapeDataString(n)}.json");
                metas = res?["metas"]?.AsArray().Where(m => m != null).Select(m => m!).ToList();
                if (metas is { Count: > 0 }) { query = n; break; }
            }
            if (metas == null || metas.Count == 0) return null;
            int? year = yearHint ?? MainWindow.CleanTitle(Path.GetFileName(e.Folder)).Year ?? fileYear;
            string want = Norm(query);
            id = Str(metas.Select((m, i) => (m: m!, i)).OrderByDescending(t =>
            {
                string n = Norm(Str(t.m["name"]) ?? "");
                return (n == want ? 100 : n.StartsWith(want) || want.StartsWith(n) ? 30 : 0)
                       + (year != null && YearOf(Str(t.m["releaseInfo"])) == year ? 60 : 0)
                       - t.i * 5; // Cinemeta's own order
            }).First().m["id"]);
            if (id == null) return null;
        }
        return (await GetJson($"{Cinemeta}/meta/{type}/{id}.json"))?["meta"];
    }

    // The title's primary image from IMDb's (keyless) suggestion service, resized to 1000px wide.
    static async Task<string?> ImdbPoster(string? imdbId)
    {
        if (imdbId == null || !imdbId.StartsWith("tt")) return null;
        try
        {
            var res = await GetJson($"https://v3.sg.media-imdb.com/suggestion/x/{imdbId}.json");
            var url = Str(res?["d"]?.AsArray().FirstOrDefault(x => Str(x?["id"]) == imdbId)?["i"]?["imageUrl"]);
            return url == null ? null : ImdbSizeRx().Replace(url, "._V1_SX1000.jpg");
        }
        catch { return null; }
    }

    static ShowInfo FromMeta(JsonNode m, string kind)
    {
        var genres = ((m["genres"] ?? m["genre"]) as JsonArray)?.Select(Str).Where(g => g != null).Select(g => g!).ToArray() ?? Array.Empty<string>();
        string? director = m["director"] is JsonArray da ? Str(da.FirstOrDefault()) : Str(m["director"]);
        double? score = double.TryParse(Str(m["imdbRating"]), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : null;
        static int Num(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : int.TryParse(Str(n), out var j) ? j : -1;
        int? episodes = kind == "tv" && m["videos"] is JsonArray vids ? vids.Count(v => Num(v?["season"]) > 0) : null;
        Dictionary<string, string>? names = null;
        if (kind == "tv" && m["videos"] is JsonArray all)
        {
            names = new();
            foreach (var v in all)
                if (Num(v?["season"]) is int s and >= 0 && Num(v?["episode"] ?? v?["number"]) is int ep and > 0 && (Str(v?["name"]) ?? Str(v?["title"])) is { Length: > 0 } n
                    && !(DateTime.TryParse(Str(v?["released"]), out var aired) && aired > DateTime.UtcNow)) // not aired yet: not "missing"
                    names[$"{s}:{ep}"] = n;
        }
        return new ShowInfo(Str(m["name"]), null, Str(m["description"]), genres, kind == "movie" ? "Movie" : "TV series",
            YearOf(Str(m["releaseInfo"]) ?? Str(m["year"])), episodes is > 0 ? episodes : null, director, score, null,
            Str(m["poster"])?.Replace("/small/", "/large/"), null, "cinemeta", Str(m["imdb_id"]) ?? Str(m["id"]), names);
    }

    // For "Wrong show info?": the top results for whatever you type (AniList for anime, Cinemeta for TV and movies).
    public static async Task<List<ShowChoice>> Choices(string text, string kind = "anime")
    {
        if (kind != "anime")
        {
            var res = await GetJson($"{Cinemeta}/catalog/{(kind == "movie" ? "movie" : "series")}/top/search={Uri.EscapeDataString(text)}.json");
            return (res?["metas"]?.AsArray() ?? new JsonArray()).Where(m => m != null && Str(m["id"]) != null).Take(12)
                .Select(m => new ShowChoice(Str(m!["id"])!, Str(m["name"]) ?? "?", null, YearOf(Str(m["releaseInfo"])), kind == "movie" ? "Movie" : "TV series", null, Str(m["poster"]))).ToList();
        }
        var page = await Query("query($s:String){Page(perPage:12){media(search:$s,type:ANIME,sort:SEARCH_MATCH){id title{romaji english} format startDate{year} episodes coverImage{medium}}}}",
            new JsonObject { ["s"] = text });
        string? S(JsonNode? n) => n?.GetValue<string>();
        return (page?["Page"]?["media"]?.AsArray() ?? new JsonArray()).Where(m => m != null).Select(m => new ShowChoice(
            m!["id"]!.GetValue<int>().ToString(), S(m["title"]?["english"]) ?? S(m["title"]?["romaji"]) ?? "?",
            S(m["title"]?["english"]) != null ? S(m["title"]?["romaji"]) : null,
            m["startDate"]?["year"]?.GetValue<int?>(),
            S(m["format"]) switch { "TV" => "TV series", "TV_SHORT" => "TV short", "MOVIE" => "Movie", "ONA" => "Web series", var f => f },
            m["episodes"]?.GetValue<int?>(), S(m["coverImage"]?["medium"]))).ToList();
    }
}
