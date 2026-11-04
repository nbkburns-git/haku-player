using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AnimePlayer;

// A folder in a library, and how it's read. Kind: "anime" (a folder of anime show folders), "tv" (TV show folders),
// "movie" (movie folders or files) or "themes" (opening / ending folders, one per show).
// Tag: a custom tag's name (read like Kind), or null for the kind's own name.
// (Before libraries had names, Kind "anime" meant a folder with Anime Shows / Anime Openings inside; such a folder still works.)
public sealed class LibraryFolder
{
    public string Path { get; set; } = "";
    public string Kind { get; set; } = "anime";
    public string? Tag { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public string TagName => Tag ?? KindName(Kind);
    public static readonly (string Kind, string Name)[] Kinds = { ("anime", "Anime"), ("tv", "TV shows"), ("movie", "Movies"), ("themes", "Openings / Endings") };
    public static string KindName(string kind) => Array.Find(Kinds, k => k.Kind == kind).Name ?? kind;
}

// A named library: one tile on the home page, made of any number of folders.
public sealed class MediaLibrary
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New library";
    public List<LibraryFolder> Folders { get; set; } = new();
    public bool Home { get; set; } = true;            // shown as a tile on the home page
}

// A tag you made: its name, and which built-in kind its folders are read like.
public sealed class TagDef
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "tv";
}

// Where you left off: the show folder, the episode file, and how far in (seconds).
public sealed class LastWatch
{
    public string Folder { get; set; } = "";
    public string Path { get; set; } = "";
    public double Position { get; set; }
    public double Duration { get; set; }
}

// Player settings, saved as data\settings.json next to the exe.
public sealed class Settings
{
    public bool Autoplay { get; set; } = true;        // go to the next video when one ends
    public bool AutoPopOut { get; set; }              // start every video in the always-on-top pop-out
    public bool Upscale { get; set; } = true;         // AI upscaling of videos smaller than the screen (Upscaler.cs)
    public string UpscaleTier { get; set; } = "auto"; // "auto", "fast", "balanced", "quality"
    public string UpscaleLook { get; set; } = "sharp"; // "sharp" (AnimeJaNai V3 Sharp1) or "soft" (V3)
    public string UpscaleLiveAction { get; set; } = "rtx"; // TV shows and movies: "rtx" (NVIDIA RTX Video), "anime" (the anime model) or "off"
    public string UpscaleMeasuredGpu { get; set; } = ""; // the GPU "Detect best for this PC" measured on
    public Dictionary<string, double> UpscaleMeasured { get; set; } = new(); // tier key -> measured fps for 1080p on that GPU
    public bool Resume { get; set; } = true;          // continue where you left off
    public string AudioLang { get; set; } = "jpn";    // "" = file default, "jpn", "eng"
    public string SubLang { get; set; } = "eng";      // subtitle language: "" = file default, "eng", "jpn"
    public bool FullSubs { get; set; } = true;        // prefer full dialogue subtitles over "Signs & Songs" / forced tracks
    public bool Subtitles { get; set; } = true;       // show subtitles by default
    // the three above are for anime; normal TV shows and movies have their own
    public string TvAudioLang { get; set; } = "eng";
    public string TvSubLang { get; set; } = "eng";
    public bool TvSubtitles { get; set; }             // off: most live-action is in your language already
    public int SettingsVersion { get; set; }          // for one-time upgrades of older settings files
    public double SubScale { get; set; } = 1.0;
    public double SubPos { get; set; } = 100;         // mpv sub-pos: 100 = the usual place at the bottom, lower = higher up (drag them on the video)
    public double HideAfter { get; set; } = 2;        // seconds before the controls fade
    public bool Previews { get; set; } = true;        // timeline hover thumbnails
    public bool Debug { get; set; }                   // live playback details over the video
    public bool SkipButtons { get; set; } = true;     // "Skip opening" / "Skip ending" while one plays
    public bool AutoSkip { get; set; }                // skip openings and recaps without asking
    public bool OnlineSegments { get; set; } = true;  // look up opening/ending times (AniSkip, TheIntroDB, IntroDB)
    public bool DetectSegments { get; set; } = true;  // no chapters or online times: compare episodes' audio to find them
    public bool UpNext { get; set; } = true;          // "Up next" countdown when the ending starts
    public bool DiscordStatus { get; set; }           // show what you're watching on your Discord profile
    public string DiscordAppId { get; set; } = "";    // your Discord application's id (discord.com/developers)
    public string LibraryRoot { get; set; } = "";     // old single anime folder; moved into Libraries
    public List<LibraryFolder> Libraries { get; set; } = new(); // old unnamed locations; moved into Libs
    public List<MediaLibrary> Libs { get; set; } = new(); // in home page order
    public List<TagDef> Tags { get; set; } = new();
    public bool MovingBackground { get; set; } = true;
    public bool MovingAlways { get; set; } = true;    // keep drifting while another app is in front (off = pause to save CPU)
    public bool CoverGlow { get; set; } = true;
    public string Theme { get; set; } = "sakura";
    public string CustomColor { get; set; } = "#2BA8FF"; // the "custom" theme's accent
    public int Glass { get; set; } = 1;
    public int[]? PopRect { get; set; }               // last pop-out window: left, top, right, bottom (pixels)
    public HashSet<string> Favorites { get; set; } = new(StringComparer.OrdinalIgnoreCase);      // series folders
    public HashSet<string> FavoriteVideos { get; set; } = new(StringComparer.OrdinalIgnoreCase); // video files                // 0 clear, 1 balanced, 2 frosted

    // your own subtitle files per video (copies kept in data\subs), and per-video subtitle timing in seconds
    public Dictionary<string, List<string>> ExtraSubs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> SubDelays { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, int> InfoOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase); // folder -> AniList id you picked
    public Dictionary<string, string> Renames { get; set; } = new(StringComparer.OrdinalIgnoreCase); // folder -> the name you gave it (in the app only; the folder keeps its name)
    public Dictionary<string, string> MediaOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase); // TV/movie folder -> IMDb id you picked
    public LastWatch? LastWatched { get; set; }       // home page "Continue watching" (shows only)

    // All of the app's own data (settings, details, covers, previews, resume points) lives in
    // %LOCALAPPDATA%\Haku Player, like an installed app, so every copy of the exe shares it.
    // HAKU_DATA_DIR overrides it (test copies use that so they never touch your real data).
    public static readonly string DataDir = Environment.GetEnvironmentVariable("HAKU_DATA_DIR") is { Length: > 0 } custom ? custom
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Haku Player");
    static readonly string FilePath = Path.Combine(DataDir, "settings.json");

    // Older versions kept everything in a "data" folder next to the exe: copy it over once (the old folder stays as a backup),
    // and point the absolute paths saved inside it (banners, your subtitle copies) at the new place.
    static void MoveOldData()
    {
        var old = Path.Combine(AppContext.BaseDirectory, "data");
        try
        {
            Directory.CreateDirectory(DataDir);
            if (File.Exists(FilePath) || !File.Exists(Path.Combine(old, "settings.json"))) return;
            foreach (var f in Directory.EnumerateFiles(old, "*", SearchOption.AllDirectories))
            {
                var to = Path.Combine(DataDir, Path.GetRelativePath(old, f));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                if (!File.Exists(to)) File.Copy(f, to);
            }
            string Esc(string p) => JsonSerializer.Serialize(p)[1..^1]; // the path as it appears inside JSON
            foreach (var j in Directory.EnumerateFiles(DataDir, "*.json", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(j);
                if (text.Contains(Esc(old), StringComparison.OrdinalIgnoreCase))
                    File.WriteAllText(j, text.Replace(Esc(old), Esc(DataDir), StringComparison.OrdinalIgnoreCase));
            }
        }
        catch { } // worst case: the app starts fresh; the old folder is untouched
    }
    public static Settings Current { get; } = Load();

    static Settings Load()
    {
        MoveOldData();
        Settings s;
        try { s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { s = new(); }
        // v2: anime defaults to Japanese audio (older files saved "" = file default)
        if (s.SettingsVersion < 2) { if (s.AudioLang == "") s.AudioLang = "jpn"; s.SettingsVersion = 2; }
        // one anime folder -> a list of library locations
        if (s.Libraries.Count == 0 && s.SettingsVersion < 3 && s.LibraryRoot is { Length: > 0 } r) s.Libraries.Add(new LibraryFolder { Path = r, Kind = "anime" });
        // v3: unnamed locations -> two named libraries, the same two home tiles as before
        if (s.SettingsVersion < 3)
        {
            var shows = new MediaLibrary { Name = "Shows and Movies" };
            var themes = new MediaLibrary { Name = "Openings / Endings" };
            foreach (var lf in s.Libraries)
            {
                if (lf.Kind != "anime") { shows.Folders.Add(new LibraryFolder { Path = lf.Path, Kind = lf.Kind }); continue; }
                shows.Folders.Add(new LibraryFolder { Path = Path.Combine(lf.Path, "Anime Shows"), Kind = "anime" });
                themes.Folders.Add(new LibraryFolder { Path = Path.Combine(lf.Path, "Anime Openings"), Kind = "themes" });
            }
            s.Libs = new() { shows, themes };
            s.SettingsVersion = 3;
            s.Save(); // keeps the old list too, for an older version of the app
        }
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // write a temp file and swap it in, so a crash mid-save can't leave settings.json half-written
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, true);
        }
        catch { }
    }

    // Language defaults for a kind of video: anime uses its own set, TV shows and movies share the other.
    public (string Audio, string Subs, bool On) LangFor(string kind) =>
        kind == "anime" ? (AudioLang, SubLang, Subtitles) : (TvAudioLang, TvSubLang, TvSubtitles);

    public void SetLang(string kind, string? audio = null, string? subs = null, bool? on = null)
    {
        if (kind == "anime") { AudioLang = audio ?? AudioLang; SubLang = subs ?? SubLang; Subtitles = on ?? Subtitles; }
        else { TvAudioLang = audio ?? TvAudioLang; TvSubLang = subs ?? TvSubLang; TvSubtitles = on ?? TvSubtitles; }
    }

    // mpv language list for a language setting
    public static string Langs(string code) => code switch { "jpn" => "jpn,ja,japanese", "eng" => "eng,en,english", _ => "" };
}
