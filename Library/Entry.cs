using System;
using System.Collections.Generic;

namespace AnimePlayer;

// Season/Ep: from "S02E05" or a "Season 2" folder (0 = unknown, -1 = extras); Num shows the episode within its season when known.
public record Video(string Path, string Title, int N, int Season = 0, int Ep = 0)
{
    public string Num => (Ep > 0 ? Ep : N).ToString("00");
    // with the episode code, for places without a season/number column (player title bar, Continue watching)
    public string Label => Season >= 0 && Ep > 0 && !MainWindow.EpisodeCodeRx().IsMatch(Title)
        ? $"S{Math.Max(1, Season):00}E{Ep:00} · {Title}" : Title;
}

// Kind: "anime", "tv" or "movie". Folder is the series folder (the first one, for a TV show split over several),
// or the file itself for a movie sitting loose in the movies folder.
// Tag: the tag of the library folder it came from (filter chips on the library page).
public record Entry(string Name, object? Cover, List<Video> Videos, string Unit, bool IsRandom = false, string Folder = "", string Kind = "anime", string Tag = "")
{
    // cache key for details: anime keeps its plain name (shared by a show and its openings)
    public string InfoKey => Kind == "anime" ? Name : $"{Kind}-{Name}";
    // what the app shows: your own name for it if you renamed it (Name stays the folder's, for matching and caches)
    public string Title => Folder.Length > 0 && Settings.Current.Renames.TryGetValue(Folder, out var t) && t.Length > 0 ? t : Name;
    public string Count => IsRandom ? (Videos.Count == 1 ? $"Play the 1 {Unit.TrimEnd('s')}" : $"Shuffle all {Videos.Count} {Unit}") : $"{Videos.Count} {(Videos.Count == 1 ? Unit.TrimEnd('s') : Unit)}";
}
