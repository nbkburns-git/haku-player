using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AnimePlayer;

// Finding this video's opening and ending: chapters, then online, then comparing with a sibling episode.
public partial class MainWindow
{
    int playingIndex = -1;
    List<Chapter> fileChapters = new();
    string? segmentsFor; // the file whose segments are being / have been looked up

    async Task FindSegments(Entry entry, Video v, double duration, int my)
    {
        if (segmentsFor == v.Path || duration <= 0) return;
        segmentsFor = v.Path;
        await Task.Delay(500); // the chapter list arrives with the file; give it a moment
        bool Still() => my == session && segmentsFor == v.Path && controls != null;
        if (!Still()) return;
        var found = Segments.FromChapters(fileChapters, duration);
        bool Need() => !Segments.Has(found, "op") || !Segments.Has(found, "ed");
        if (Need() && Settings.Current.OnlineSegments)
        {
            found = Segments.Merge(found, await Segments.Online(entry, v, duration));
            if (!Still()) return;
        }
        if (Segments.CachedDetection(v.Path) is { } cached) found = Segments.Merge(found, cached);
        controls!.SetSegments(found);
        // still missing one: compare with the next (or previous) episode of the same season
        if (Need() && Settings.Current.DetectSegments && entry.Kind != "movie" && Sibling(entry, v) is { } other && Segments.CachedDetection(v.Path) == null)
        {
            var detected = await Segments.Detect(v.Path, duration, other.Path);
            if (!Still()) return;
            controls!.SetSegments(Segments.Merge(found, detected));
        }
    }

    static Video? Sibling(Entry e, Video v)
    {
        var season = e.Videos.Where(x => x.Season == v.Season && x.Season >= 0).ToList();
        int i = season.IndexOf(v);
        return i < 0 ? null : i + 1 < season.Count ? season[i + 1] : i > 0 ? season[i - 1] : null;
    }
}
