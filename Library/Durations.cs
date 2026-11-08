using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace AnimePlayer;

// Running times for the library stats (Library/Stats.cs is the page): no UI in here, so the coming WinUI app shares it.
public partial class MainWindow
{
    // Running times come from ffprobe (it only reads each file's header), cached in durations.json by size + date.
    sealed record Probe(long Size, long Stamp, double Dur);
    static readonly string DurFile = Path.Combine(Settings.DataDir, "durations.json");
    static Dictionary<string, Probe>? durations;
    static bool probing;
    static int probeDone, probeTotal;

    static Dictionary<string, Probe> Durations()
    {
        if (durations != null) return durations;
        try { durations = JsonSerializer.Deserialize<Dictionary<string, Probe>>(File.ReadAllText(DurFile)); } catch { }
        return durations = durations != null ? new(durations, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
    }

    // Measures these files with ffprobe, 25 at a time off the UI thread; progress() after each batch (on the caller's thread).
    static async Task MeasureDurations(List<(string Path, long Size, long Stamp)> todo, Action progress)
    {
        var cache = Durations();
        foreach (var chunk in todo.Chunk(25))
        {
            var got = await Task.Run(() => chunk.Select(x => (x.Path, new Probe(x.Size, x.Stamp, Segments.Duration(x.Path)))).ToList());
            foreach (var (path, pr) in got)
            {
                cache[path] = pr;
                // resume points picked up from mpv had no length yet
                if (Watch.Get(path) is { Dur: <= 0 } r && pr.Dur > 0) r.Dur = pr.Dur;
            }
            probeDone += chunk.Length;
            progress();
        }
        var tmp = DurFile + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(cache));
        File.Move(tmp, DurFile, true);
        Watch.Save();
    }
}
