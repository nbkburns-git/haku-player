using System;
using System.Linq;

namespace AnimePlayer;

public partial class MainWindow
{
    double discordPos;
    DateTime discordAt;

    // What Discord shows: the show and episode (or the opening's name), with time left while playing.
    void DiscordUpdate()
    {
        if (playingEntry is not { } e || controls is not { CurrentFile.Length: > 0 } c) return;
        discordPos = c.Position; discordAt = DateTime.UtcNow;
        var v = e.Videos.FirstOrDefault(x => string.Equals(x.Path, c.CurrentFile, StringComparison.OrdinalIgnoreCase));
        if (v == null) return;
        bool theme = e.Unit == "themes";
        string details = theme ? v.Title : e.Name;
        string state = theme ? "Opening / ending" : e.Kind == "movie" ? "Movie" : v.Label;
        string? image = theme ? null : InfoService.Cached(e.InfoKey)?.CoverUrl;
        Discord.Watching(details, state, c.Paused, c.Position, c.Length, image);
    }
}
