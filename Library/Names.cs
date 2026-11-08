using System;
using System.Collections.Frozen;
using System.IO;
using System.Text.RegularExpressions;

namespace AnimePlayer;

// Reading show, season and episode names out of file and folder names.
// The library scan runs these on every file, so the patterns are compiled into the app at build time ([GeneratedRegex]):
// nothing is parsed at run time, and they don't compete for Regex's small 15-pattern cache with the rest of the app.
public partial class MainWindow
{
    const RegexOptions IC = RegexOptions.IgnoreCase;

    [GeneratedRegex(@"\bsample\b", IC)] private static partial Regex SampleRx();
    [GeneratedRegex(@"\[[^\]]*\]")] private static partial Regex BracketsRx();
    [GeneratedRegex(@"\s+")] private static partial Regex SpacesRx();
    [GeneratedRegex(@"^www\.\S+\s*-\s*", IC)] private static partial Regex SiteRx();
    [GeneratedRegex(@"\(?\b(19|20)\d\d\b\)?|\bS\d{1,2}(E\d+)?\b|\bSeasons?\b|\b(2160p|1080p|720p|480p|4K|UHD|Blu-?Ray|BDRip|BRRip|WEB(-?DL|-?Rip)?|HDR|x26[45]|HEVC|Mp4|Complete|REMUX|MULTi|RERIP|REMASTERED|DVDRip)\b", IC)]
    private static partial Regex CutRx();
    [GeneratedRegex(@"\b(19|20)\d\d\b")] private static partial Regex YearRx();
    [GeneratedRegex(@"S\d{1,2}\s*[-–]?\s*E\d{1,3}|\b\d{1,2}x\d{2,3}\b|\b(?:Episode|Ep|E)[\s._-]?\d{1,4}\b|\s[-–]\s*\d{1,4}\b|[\s._]\d{2,4}(?=\s*(?:\(|v\d|$))", IC)]
    private static partial Regex EpisodeMarkRx();
    [GeneratedRegex(@"\\(Extras?|Featurettes?|Bonus|Specials? Features?|Behind The Scenes)(\\|$)", IC)] private static partial Regex ExtrasDirRx();
    [GeneratedRegex(@"S(\d{1,2})\s*[-–]?\s*E(\d{1,3})", IC)] private static partial Regex SxxExxRx();
    [GeneratedRegex(@"\bS\d+\s*[-–]?\s*E\d+", IC)] internal static partial Regex EpisodeCodeRx();
    [GeneratedRegex(@"Season[ ._]?(\d+)", IC)] private static partial Regex SeasonNumRx();
    [GeneratedRegex(@"(?:Episode|Ep)[ ._]?(\d+)", IC)] private static partial Regex EpisodeNumRx();
    [GeneratedRegex(@"Episode[ ._]?\d+", IC)] private static partial Regex EpisodeWordRx();
    [GeneratedRegex(@"-(NC|BD|DVD)\w*$")] private static partial Regex ReleaseTagRx();
    [GeneratedRegex(@"(?<=[a-z])(?=[A-Z0-9])|(?<=\d)(?=[A-Z][a-z])")] private static partial Regex CamelRx();
    [GeneratedRegex(@"-(?=(OP|ED)\d)")] private static partial Regex ThemeDashRx();
    [GeneratedRegex(@"[^\w]+")] private static partial Regex NonWordRx();
    [GeneratedRegex(@"[^\p{L}\p{N}]")] private static partial Regex NotLetterOrDigitRx();

    // a name made safe for a file name: "Re:Zero (2016)" -> "Re_Zero_2016_" (cached details, fetched covers)
    internal static string SafeName(string s) => NonWordRx().Replace(s, "_");

    // Looked up by the extension's characters, so checking a path allocates nothing.
    static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> VideoExt =
        new[] { ".mp4", ".mkv", ".webm", ".avi", ".mov", ".m4v" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase).GetAlternateLookup<ReadOnlySpan<char>>();

    internal static bool IsVideoExt(ReadOnlySpan<char> path) => VideoExt.Contains(Path.GetExtension(path));

    // a video, and not a sample clip
    static bool IsVideo(string f) => IsVideoExt(f) && !SampleRx().IsMatch(Path.GetFileNameWithoutExtension(f.AsSpan()));

    // lower-case letters and digits only, for comparing names: "Re:Zero (2016)" -> "rezero2016"
    internal static string Norm(string s)
    {
        Span<char> buf = s.Length <= 256 ? stackalloc char[s.Length] : new char[s.Length];
        int n = 0;
        foreach (char ch in s)
        {
            char c = char.ToLowerInvariant(ch);
            if (char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c)) buf[n++] = c;
        }
        return new string(buf[..n]);
    }

    // "Game.of.Thrones.Season.1-8.COMPLETE.1080p…" -> ("Game of Thrones", null); "Hellboy (2004) [1080p]" -> ("Hellboy", 2004).
    // Cuts at the first year, season marker or release tag that isn't at the very start.
    public static (string Title, int? Year) CleanTitle(string raw) => CleanTitle(raw, true);

    // keepRaw: when nothing is left after cleaning, return the input (a name) or "" (an episode's leftover tags)
    static (string Title, int? Year) CleanTitle(string raw, bool keepRaw)
    {
        var s = SiteRx().Replace(raw, "");                                    // "www.UIndex.org - Sinners 2025…"
        s = BracketsRx().Replace(s, " ").Replace('.', ' ').Replace('_', ' ');
        var cut = FirstPastStart(CutRx().Match(s));
        int? year = FirstPastStart(YearRx().Match(s)) is { } y && (cut == null || y.Index <= cut.Index + 1) ? int.Parse(y.ValueSpan) : null;
        var title = SpacesRx().Replace(cut != null ? s[..cut.Index] : s, " ").Trim(' ', '-', '(', '+');
        return (title.Length > 0 || !keepRaw ? title : raw, year);
    }

    // the first match that doesn't start the string
    static Match? FirstPastStart(Match m)
    {
        while (m.Success && m.Index == 0) m = m.NextMatch();
        return m.Success ? m : null;
    }

    // the show's name from an episode file: "[Grp] Naruto - 012 [1080p]" -> "Naruto", "Rick.and.Morty.S01E02…" -> "Rick and Morty"
    public static string ShowGuess(string file, bool theme = false)
    {
        if (theme)
        {
            var f = Friendly(file);
            int dash = f.IndexOf(" – ", StringComparison.Ordinal);
            return (dash >= 0 ? f[..dash] : f).Trim();
        }
        var n = BracketsRx().Replace(Path.GetFileNameWithoutExtension(file), " ");
        var m = EpisodeMarkRx().Match(n);
        return CleanTitle(m.Success ? n[..m.Index] : n, false).Title.Trim(' ', '-', '–', '.', '_');
    }

    // Season/episode from "S01E02" or ".../Season 1/... Episode 02", for ordering a show split over folders.
    static (int S, int E) EpisodeOrder(string f)
    {
        if (ExtrasDirRx().IsMatch(Path.GetDirectoryName(f.AsSpan()))) return (int.MaxValue, 0); // bonus clips after the episodes
        var n = Path.GetFileNameWithoutExtension(f);
        if (SxxExxRx().Match(n) is { Success: true } m) return (int.Parse(m.Groups[1].ValueSpan), int.Parse(m.Groups[2].ValueSpan));
        int season = SeasonNumRx().Match(f) is { Success: true } sm ? int.Parse(sm.Groups[1].ValueSpan) : 0;
        return EpisodeNumRx().Match(n) is { Success: true } em ? (season, int.Parse(em.Groups[1].ValueSpan)) : (season, 999);
    }

    // "Rick and Morty - S08E01 - Summer of All Fears" -> "S08E01 · Summer of All Fears"; "Curb.Your.Enthusiasm.S01E01.1080p…" -> "S01E01"
    static string EpisodeTitle(string f)
    {
        var n = Path.GetFileNameWithoutExtension(f);
        var m = SxxExxRx().Match(n);
        if (!m.Success) return EpisodeWordRx().Match(n) is { Success: true } em ? em.Value : CleanTitle(n).Title;
        var rest = CleanTitle(" " + n[(m.Index + m.Length)..], false).Title.Trim(' ', '-');
        var code = $"S{int.Parse(m.Groups[1].ValueSpan):00}E{int.Parse(m.Groups[2].ValueSpan):00}";
        return rest.Length > 1 ? $"{code} · {rest}" : code;
    }

    // "CodeGeassR2-OP1-Encoded.23.976fps..." -> "Code Geass R2 – OP1"; "[Grp] Show S01 - E01 [1080p]-Encoded..." -> "Show S01 - E01"
    public static string Friendly(string path)
    {
        var n = Path.GetFileNameWithoutExtension(path);
        var i = n.IndexOf("-Encoded", StringComparison.OrdinalIgnoreCase);
        if (i > 0) n = n[..i];
        n = BracketsRx().Replace(n, "");
        n = ReleaseTagRx().Replace(n, "").Trim(' ', '-', '_', '.');
        if (!n.Contains(' ')) // AnimeThemes-style basename
        {
            n = CamelRx().Replace(n, " ");
            n = ThemeDashRx().Replace(n, " – ");
        }
        return n;
    }
}
