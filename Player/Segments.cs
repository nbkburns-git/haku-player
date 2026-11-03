using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AnimePlayer;

// Kind: "op" (opening / intro), "ed" (ending / credits) or "recap". Times in seconds.
public sealed record Segment(string Kind, double Start, double End);
public sealed record Chapter(double Time, string Title);

// Where the openings and endings are, best source first:
//   1. the file's own chapters ("Opening", "OP", "Ending", "Credits"...)
//   2. online: AniSkip for anime (by MyAnimeList id + episode), TheIntroDB then IntroDB for TV and movies (by IMDb id)
//   3. detection: the same audio at the same place in two episodes of a season (Chromaprint fingerprints, like Jellyfin's Intro Skipper)
// Online and detected results are cached per file in segments.json.
static class Segments
{
    sealed class Rec
    {
        public List<Segment> Online { get; set; } = new();
        public DateTime OnlineAt { get; set; }
        public List<Segment>? Detected { get; set; } // null = not tried yet
    }

    static readonly string FilePath = Path.Combine(Settings.DataDir, "segments.json");
    static readonly Dictionary<string, Rec> Cache = Load();
    static readonly HttpClient Http = MakeHttp();
    static HttpClient MakeHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("HakuPlayer/1.0");
        return h;
    }

    static Dictionary<string, Rec> Load()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, Rec>>(File.ReadAllText(FilePath)) is { } d ? new(d, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase); }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }

    static void Save()
    {
        try
        {
            string json;
            lock (Cache) json = JsonSerializer.Serialize(Cache);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, true);
        }
        catch { }
    }

    static Rec RecFor(string path) { lock (Cache) return Cache.TryGetValue(path, out var r) ? r : Cache[path] = new Rec(); }

    // Adds the kinds `more` has that `have` doesn't (earlier sources win).
    // One of each kind: online lists can hold two "openings" (a short mixed one and the real one), so the longest wins.
    public static List<Segment> Merge(List<Segment> have, IEnumerable<Segment> more) =>
        have.Concat(more.Where(m => have.All(h => h.Kind != m.Kind)).GroupBy(m => m.Kind).Select(g => g.OrderByDescending(x => x.End - x.Start).First()))
            .OrderBy(s => s.Start).ToList();

    public static bool Has(List<Segment> s, string kind) => s.Any(x => x.Kind == kind);

    // ---------- 1. chapters ----------

    static string? KindOf(string title)
    {
        var t = title.Trim();
        if (Regex.IsMatch(t, @"^(op\d*|ncop\d*)\b|\b(opening|intro|introduction|theme song|main titles?)\b|オープニング", RegexOptions.IgnoreCase)) return "op";
        if (Regex.IsMatch(t, @"^(ed\d*|nced\d*)\b|\b(ending|credits|outro|end titles?)\b|エンディング", RegexOptions.IgnoreCase)) return "ed";
        if (Regex.IsMatch(t, @"\b(recap|previously)\b", RegexOptions.IgnoreCase)) return "recap";
        return null;
    }

    public static List<Segment> FromChapters(List<Chapter> chapters, double duration)
    {
        var list = new List<Segment>();
        for (int i = 0; i < chapters.Count; i++)
        {
            if (KindOf(chapters[i].Title) is not string kind || list.Any(s => s.Kind == kind)) continue;
            double end = i + 1 < chapters.Count ? chapters[i + 1].Time : duration;
            if (end - chapters[i].Time is >= 8 and <= 900) list.Add(new Segment(kind, chapters[i].Time, end));
        }
        return list;
    }

    // ---------- 2. online ----------

    public static async Task<List<Segment>> Online(Entry e, Video v, double duration)
    {
        var rec = RecFor(v.Path);
        if (rec.OnlineAt > DateTime.UtcNow.AddDays(-7)) return rec.Online; // nothing found is re-checked after a week (people keep submitting)
        List<Segment>? found = null;
        try
        {
            var info = InfoService.Cached(e.InfoKey);
            if (e.Kind == "anime" && e.Unit == "episodes" && info?.AniListId is int al && v.Season <= 1)
            {
                int ep = v.Ep > 0 ? v.Ep : v.N;
                if (await MalId(al) is int mal) found = await AniSkip(mal, ep, duration);
            }
            else if (e.Kind != "anime" && info?.ImdbId is { } imdb && imdb.StartsWith("tt"))
            {
                int? s = e.Kind == "tv" ? Math.Max(1, v.Season) : null, ep = e.Kind == "tv" ? v.Ep : null;
                if (e.Kind == "movie" || ep > 0)
                    found = await TheIntroDb(imdb, s, ep, duration) ?? (e.Kind == "tv" ? await IntroDb(imdb, s!.Value, ep!.Value, duration) : null);
            }
        }
        catch { return rec.Online; } // offline: try again next time
        rec.Online = found ?? new();
        rec.OnlineAt = DateTime.UtcNow;
        Save();
        return rec.Online;
    }

    static readonly Dictionary<int, int?> MalIds = new();
    static async Task<int?> MalId(int aniListId)
    {
        lock (MalIds) if (MalIds.TryGetValue(aniListId, out var hit)) return hit;
        var body = new JsonObject { ["query"] = "query($id:Int){Media(id:$id){idMal}}", ["variables"] = new JsonObject { ["id"] = aniListId } }.ToJsonString();
        using var res = await Http.PostAsync("https://graphql.anilist.co", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        int? mal = res.IsSuccessStatusCode ? JsonNode.Parse(await res.Content.ReadAsStringAsync())?["data"]?["Media"]?["idMal"]?.GetValue<int?>() : null;
        lock (MalIds) MalIds[aniListId] = mal;
        return mal;
    }

    static async Task<JsonNode?> GetJson(string url)
    {
        using var res = await Http.GetAsync(url);
        return res.IsSuccessStatusCode ? JsonNode.Parse(await res.Content.ReadAsStringAsync()) : null;
    }

    static string Inv(double d) => d.ToString(CultureInfo.InvariantCulture);

    // AniSkip: community opening / ending / recap times for anime, by MyAnimeList id and episode number.
    static async Task<List<Segment>?> AniSkip(int mal, int ep, double duration)
    {
        var j = await GetJson($"https://api.aniskip.com/v2/skip-times/{mal}/{ep}?types[]=op&types[]=ed&types[]=mixed-op&types[]=mixed-ed&types[]=recap&episodeLength={Inv(Math.Round(duration))}");
        if (j?["found"]?.GetValue<bool>() != true) return null;
        var list = new List<Segment>();
        foreach (var r in j["results"]?.AsArray() ?? new JsonArray())
        {
            var kind = r?["skipType"]?.GetValue<string>() switch { "op" or "mixed-op" => "op", "ed" or "mixed-ed" => "ed", "recap" => "recap", _ => null };
            double? a = r?["interval"]?["startTime"]?.GetValue<double>(), b = r?["interval"]?["endTime"]?.GetValue<double>();
            // times were submitted against a file of some length; skip ones from a clearly different cut
            double len = r?["episodeLength"]?.GetValue<double>() ?? 0;
            if (kind != null && a != null && b > a && (len <= 0 || duration <= 0 || Math.Abs(len - duration) < 30)) list.Add(new Segment(kind, a.Value, b!.Value));
        }
        return list.Count > 0 ? Merge(new(), list) : null;
    }

    // TheIntroDB (v4): intro / recap / credits for TV episodes and movies. IMDb ids work without an API key.
    // A missing end time means "to the end of the file".
    static async Task<List<Segment>?> TheIntroDb(string imdb, int? season, int? ep, double duration)
    {
        var url = $"https://api.theintrodb.org/v4/media?imdb_id={imdb}" + (season != null ? $"&season={season}&episode={ep}" : "");
        var j = await GetJson(url);
        if (j == null) return null;
        var list = new List<Segment>();
        foreach (var (field, kind) in new[] { ("intro", "op"), ("recap", "recap"), ("credits", "ed") })
            if (j[field] is JsonArray arr && arr.FirstOrDefault() is JsonNode n && n["start_ms"]?.GetValue<double?>() is double a)
            {
                double b = n["end_ms"]?.GetValue<double?>() / 1000 ?? duration;
                if (b > a / 1000) list.Add(new Segment(kind, a / 1000, b));
            }
        return list.Count > 0 ? list.OrderBy(s => s.Start).ToList() : null;
    }

    // IntroDB: the same idea, smaller database, TV episodes only.
    static async Task<List<Segment>?> IntroDb(string imdb, int season, int ep, double duration)
    {
        var j = await GetJson($"https://api.introdb.app/segments?imdb_id={imdb}&season={season}&episode={ep}");
        if (j == null) return null;
        var list = new List<Segment>();
        foreach (var (field, kind) in new[] { ("intro", "op"), ("recap", "recap"), ("outro", "ed") })
            if (j[field] is JsonObject n && n["start_sec"]?.GetValue<double?>() is double a && (n["end_sec"]?.GetValue<double?>() ?? duration) is var b && b > a)
                list.Add(new Segment(kind, a, b));
        return list.Count > 0 ? list.OrderBy(s => s.Start).ToList() : null;
    }

    // ---------- 3. detection ----------

    const double Item = 0.1238; // seconds per Chromaprint fingerprint value (11025 Hz, 4096-sample frames, 2/3 overlap)

    public static List<Segment>? CachedDetection(string path) { lock (Cache) return Cache.TryGetValue(path, out var r) ? r.Detected : null; }

    // Compares this episode with another from the same season: a stretch of identical audio near the start is the opening,
    // near the end the ending. Results are saved for both files.
    public static async Task<List<Segment>> Detect(string path, double duration, string other)
    {
        if (CachedDetection(path) is { } done) return done;
        var found = await Task.Run(() =>
        {
            double otherDur = Duration(other);
            if (duration <= 0 || otherDur <= 0) return (Mine: new List<Segment>(), Theirs: new List<Segment>());
            var mine = new List<Segment>(); var theirs = new List<Segment>();
            // opening: somewhere in the first third (cold opens can run several minutes); ending: in the last stretch
            double introLen = Math.Min(Math.Min(duration, otherDur) * 0.3, 420);
            double outroLen = Math.Min(Math.Min(duration, otherDur) * 0.25, 300);
            double oa = duration - outroLen, ob = otherDur - outroLen;
            // one read at a time: on a hard disk, reading several places at once is much slower (and would fight playback)
            var fp = new[] { (path, 0.0, introLen), (other, 0.0, introLen), (path, oa, outroLen), (other, ob, outroLen) }
                .Select(x => Fingerprint(x.Item1, x.Item2, x.Item3)).ToArray();
            if (Match(fp[0], fp[1]) is (double a0, double a1, double b0, double b1))
            { mine.Add(new("op", a0, a1)); theirs.Add(new("op", b0, b1)); }
            if (Match(fp[2], fp[3]) is (double c0, double c1, double d0, double d1))
            {
                // an ending that runs to (nearly) the end of the file
                mine.Add(new("ed", oa + c0, oa + c1 > duration - 3 ? duration : oa + c1));
                theirs.Add(new("ed", ob + d0, ob + d1 > otherDur - 3 ? otherDur : ob + d1));
            }
            return (Mine: mine, Theirs: theirs);
        });
        RecFor(path).Detected = found.Mine;
        if (CachedDetection(other) == null) RecFor(other).Detected = found.Theirs;
        Save();
        return found.Mine;
    }

    // Longest run of matching fingerprints between a and b at any alignment, in seconds into each: (aStart, aEnd, bStart, bEnd).
    // Matching = at most 6 of 32 bits differ; gaps up to 3.5 s inside a run are allowed (a sound effect over the song).
    static (double, double, double, double)? Match(uint[] a, uint[] b)
    {
        if (a.Length < 100 || b.Length < 100) return null;
        int maxGap = (int)(3.5 / Item), bestLen = 0, bestStart = 0, bestShift = 0;
        for (int shift = -(b.Length - 1); shift < a.Length; shift++)
        {
            int from = Math.Max(0, shift), to = Math.Min(a.Length, b.Length + shift);
            if (to - from <= bestLen) continue;
            int runStart = -1, last = -1000;
            for (int i = from; i < to; i++)
            {
                if (BitOperations.PopCount(a[i] ^ b[i - shift]) > 6) continue;
                if (i - last > maxGap) runStart = i;
                last = i;
                if (last - runStart > bestLen) { bestLen = last - runStart; bestStart = runStart; bestShift = shift; }
            }
        }
        double secs = bestLen * Item;
        if (secs is < 15 or > 240) return null; // too short to be a song, or the two files are mostly the same
        double s0 = bestStart * Item, s1 = (bestStart + bestLen) * Item;
        return (s0, s1, s0 - bestShift * Item, s1 - bestShift * Item);
    }

    // Raw Chromaprint fingerprint of `len` seconds of the first audio track from `start`.
    static uint[] Fingerprint(string path, double start, double len)
    {
        try
        {
            var psi = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            foreach (var x in new[] { "-v", "quiet", "-nostdin", "-ss", Inv(Math.Max(0, start)), "-t", Inv(len), "-i", path, "-map", "0:a:0", "-vn", "-sn", "-dn", "-ac", "1",
                                      "-f", "chromaprint", "-fp_format", "raw", "-" })
                psi.ArgumentList.Add(x);
            using var p = Process.Start(psi)!;
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            using var ms = new MemoryStream();
            p.StandardOutput.BaseStream.CopyTo(ms);
            p.WaitForExit(30000);
            var bytes = ms.ToArray();
            var fp = new uint[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, fp, 0, fp.Length * 4);
            return fp;
        }
        catch { return Array.Empty<uint>(); }
    }

    public static double Duration(string path)
    {
        try
        {
            var psi = new ProcessStartInfo("ffprobe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            foreach (var x in new[] { "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", path }) psi.ArgumentList.Add(x);
            using var p = Process.Start(psi)!;
            var s = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(10000);
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
        }
        catch { return 0; }
    }
}
