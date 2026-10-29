using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace AnimePlayer;

// One video's watch state. Pos/Dur in seconds; Seconds = time actually spent watching it (for stats).
public sealed class WatchRec
{
    public double Pos { get; set; }
    public double Dur { get; set; }
    public bool Done { get; set; }
    public DateTime Last { get; set; }
    public double Seconds { get; set; }
    public int Plays { get; set; }
}

public sealed class WatchData
{
    public Dictionary<string, WatchRec> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> Days { get; set; } = new(); // "yyyy-MM-dd" -> seconds watched that day
    public bool Seeded { get; set; }
}

// Watch history (watched.json in the data folder): per-episode progress and watched ticks, Continue watching, stats.
static class Watch
{
    static readonly string FilePath = Path.Combine(Settings.DataDir, "watched.json");
    public static readonly WatchData Data = Load();
    public static event Action? Changed; // a video became watched / unwatched (lists refresh their ticks)

    static WatchData Load()
    {
        try { return JsonSerializer.Deserialize<WatchData>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public static WatchRec? Get(string path) => Data.Files.GetValueOrDefault(path);
    public static bool IsDone(string path) => Get(path)?.Done == true;

    // 0..1 for a started, unfinished video (the bar under its row); 0 otherwise
    public static double Progress(string path) =>
        Get(path) is { Done: false, Dur: > 0 } r && r.Pos > 5 ? Math.Clamp(r.Pos / r.Dur, 0, 1) : 0;

    // Where a video counts as watched: the ending's start when known, else 90% of the way through.
    public static double DoneAt(double dur, double? endingStart) => endingStart is double e && e > dur * 0.5 ? e : dur * 0.9;

    static string? tickPath;
    static double tickPos = -1;

    // Called with every position update while something plays.
    public static void Tick(string path, double pos, double dur, double? endingStart)
    {
        if (dur <= 0) return;
        if (!Data.Files.TryGetValue(path, out var r)) Data.Files[path] = r = new WatchRec();
        double step = tickPath == path ? pos - tickPos : 0;
        if (step is > 0 and < 2.5) // normal playback (any speed); seeks are bigger jumps
        {
            r.Seconds += step;
            var day = DateTime.Now.ToString("yyyy-MM-dd");
            Data.Days[day] = Data.Days.GetValueOrDefault(day) + step;
        }
        tickPath = path; tickPos = pos;
        double at = DoneAt(dur, endingStart);
        bool crossed = r.Pos < at && pos >= at;
        r.Pos = pos; r.Dur = dur; r.Last = DateTime.Now;
        if (crossed) { r.Plays++; if (!r.Done) { r.Done = true; Changed?.Invoke(); } }
        SaveSoon();
    }

    // Ticked or unticked by hand. Also forgets mpv's resume point, so an unwatched episode starts from the top.
    public static void SetDone(IEnumerable<string> paths, bool done)
    {
        foreach (var p in paths)
        {
            if (!Data.Files.TryGetValue(p, out var r)) Data.Files[p] = r = new WatchRec();
            r.Done = done;
            r.Pos = 0;
            r.Last = DateTime.Now;
            try { File.Delete(ResumeFile(p)); } catch { }
        }
        Changed?.Invoke();
        Save();
    }

    // mpv's resume file for a video: the MD5 of its path, in watch_later
    public static string ResumeFile(string path) =>
        Path.Combine(Settings.DataDir, "watch_later", Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(path))));

    // First run with watch history: pick up the resume points mpv already saved, so Continue watching isn't empty.
    public static void Seed(IEnumerable<Video> videos)
    {
        if (Data.Seeded) return;
        foreach (var v in videos)
        {
            var f = ResumeFile(v.Path);
            if (Data.Files.ContainsKey(v.Path) || !File.Exists(f)) continue;
            try
            {
                var start = File.ReadLines(f).FirstOrDefault(l => l.StartsWith("start="));
                if (start != null && double.TryParse(start[6..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pos) && pos > 5)
                    Data.Files[v.Path] = new WatchRec { Pos = pos, Last = File.GetLastWriteTime(f) }; // length filled in by Stats/Probe or the next play
            }
            catch { }
        }
        if (Settings.Current.LastWatched is { Path.Length: > 0 } lw && !Data.Files.ContainsKey(lw.Path))
            Data.Files[lw.Path] = new WatchRec { Pos = lw.Position, Dur = lw.Duration, Last = DateTime.Now };
        Data.Seeded = true;
        Save();
    }

    static DispatcherTimer? saveTimer;
    static void SaveSoon()
    {
        if (saveTimer == null)
        {
            saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            saveTimer.Tick += (_, _) => { saveTimer.Stop(); Save(); };
        }
        if (!saveTimer.IsEnabled) saveTimer.Start();
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Settings.DataDir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Data));
            File.Move(tmp, FilePath, true);
        }
        catch { }
    }
}

// Watched ticks and progress for the episode rows and cover cards.
public sealed class WatchConverter : System.Windows.Data.IValueConverter
{
    static readonly System.Windows.Media.Brush Faint = Freeze(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)));
    static System.Windows.Media.Brush Freeze(System.Windows.Media.Brush b) { b.Freeze(); return b; }

    public object Convert(object value, Type t, object? p, System.Globalization.CultureInfo c)
    {
        if (value is Entry e)
        {
            if (e.IsRandom || e.Unit == "themes") return e.Count;
            int done = e.Videos.Count(v => Watch.IsDone(v.Path));
            if (e.Kind == "movie") return done > 0 ? "Watched" : e.Videos.Count > 1 ? e.Count : Watch.Progress(e.Videos[0].Path) > 0 ? "In progress" : "Movie";
            return done == 0 ? e.Count : done >= e.Videos.Count ? $"{e.Count} · all watched" : $"{e.Count} · {done} watched";
        }
        if (value is not Video v) return System.Windows.DependencyProperty.UnsetValue;
        bool isDone = Watch.IsDone(v.Path);
        return (p as string) switch
        {
            "progress" => Watch.Progress(v.Path),                                   // ScaleX of the bar under the row
            "bar" => Watch.Progress(v.Path) > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed,
            "dim" => isDone ? 0.55 : 1.0,                                          // watched rows fade back a little
            "color" => isDone ? System.Windows.Application.Current.Resources["Accent"] : Faint,
            "tip" => isDone ? "Watched · click to mark as unwatched" : "Mark as watched",
            _ => System.Windows.DependencyProperty.UnsetValue,
        };
    }
    public object ConvertBack(object v, Type t, object? p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
}
