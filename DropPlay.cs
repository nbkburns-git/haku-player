using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace AnimePlayer;

// Drop video files (or folders of them) anywhere on the window to play them, in a library or not.
// A file that's in a library plays as that show's episode (names, next episode, progress); anything else
// plays as its own little playlist, in the order dropped (folders: their videos in name order).
public partial class MainWindow
{
    static List<string> DroppedVideos(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] paths
            ? paths.SelectMany(p => Directory.Exists(p) ? SafeVideos(p) : File.Exists(p) && IsVideo(p) ? new[] { p } : Array.Empty<string>()).ToList()
            : new();

    static IEnumerable<string> SafeVideos(string dir) { try { return VideosIn(dir).OrderBy(f => f, Natural.I).ToList(); } catch { return Array.Empty<string>(); } }

    void OnDragFiles(object s, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return; // e.g. a home tile being moved: not ours
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    void OnDragFilesEnter(object s, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && !e.Data.GetDataPresent("haku-tile")) Toast("Drop to play");
    }

    void OnDropFiles(object s, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Handled = true;
        var files = DroppedVideos(e);
        if (files.Count == 0) { Toast("No videos there that Haku Player can play"); return; }
        PlayFiles(files);
    }

    void PlayFiles(List<string> files)
    {
        Activate();
        if (!Player.IsVisible) returnTo = Home.IsVisible ? Home : Library.IsVisible ? Library : Detail.IsVisible ? Detail : Home;
        var same = StringComparer.OrdinalIgnoreCase;
        var home = files.Count == 1 ? Everything(lib).FirstOrDefault(x => !x.IsRandom && x.Videos.Any(v => same.Equals(v.Path, files[0]))) : null;
        if (home != null) { Play(home, home.Videos.FindIndex(v => same.Equals(v.Path, files[0]))); return; }

        var first = files[0];
        bool one = files.Count == 1;
        var name = CleanTitle(one ? Path.GetFileNameWithoutExtension(first) : Path.GetFileName(Path.GetDirectoryName(first)) ?? "").Title;
        var videos = files.Select((f, i) => WithSeason(new Video(f, one ? name : EpisodeTitle(f), i + 1))).ToList();
        // not in a library: no details to look up; a single file plays like a movie, several like episodes
        Play(new Entry(name.Length > 0 ? name : "Video", null, videos, one ? "videos" : "episodes", false, "", one ? "movie" : "tv"), 0);
    }
}
