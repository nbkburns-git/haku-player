using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace AnimePlayer;

public partial class MainWindow
{
    double discordPos;
    DateTime discordAt;

    // What Discord shows: the show and episode (or the show and which opening), its cover, and time left while playing.
    void DiscordUpdate()
    {
        if (playingEntry is not { } e || controls is not { CurrentFile.Length: > 0 } c) return;
        discordPos = c.Position; discordAt = DateTime.UtcNow;
        var v = e.Videos.FirstOrDefault(x => string.Equals(x.Path, c.CurrentFile, StringComparison.OrdinalIgnoreCase));
        if (v == null) return;
        string details = e.Name, state = e.Kind == "movie" ? "Movie" : v.Label;
        if (e.Unit == "themes") (details, state) = ThemeText(v.Title, e.Name);
        string? image = InfoService.Cached(e.InfoKey)?.CoverUrl; // openings share the show's details
        Discord.Watching(details, state, c.Paused, c.Position, c.Length, image);
    }

    // "Code Geass R2 – OP1" -> ("Code Geass R2", "Opening 1"); "Bleach – ED12v2" -> ("Bleach", "Ending 12")
    internal static (string, string) ThemeText(string title, string show)
    {
        var m = Regex.Match(title, @"^(.*?)\s*[–-]\s*(OP|ED)(\d*)", RegexOptions.IgnoreCase);
        if (!m.Success) return (title, "Opening / ending");
        string kind = m.Groups[2].Value.Equals("OP", StringComparison.OrdinalIgnoreCase) ? "Opening" : "Ending";
        string name = m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : show;
        return (name, m.Groups[3].Value.Length > 0 ? $"{kind} {int.Parse(m.Groups[3].Value)}" : kind);
    }
}
