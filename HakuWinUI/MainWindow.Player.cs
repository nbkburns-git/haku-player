using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AnimePlayer;

// The player's parts beyond the pill (phase 2): what's playing (a playlist of dropped / opened files, as the WPF app plays
// files that aren't in a library), its openings and endings (Segments: chapters, online times, then comparing with the
// next episode), the skip / Up next card, watch progress, timeline thumbnails, and the audio / subtitles / speed menu.
public partial class MainWindow
{
    readonly TopBar topBar = new();
    readonly PromptCard prompt = new();
    Entry? entry;                 // what's playing (its Videos are mpv's playlist)
    int playingIndex = -1;
    double position;
    List<Chapter> chapters = new();
    List<Segment> segments = new();
    string? segmentsFor;          // the file whose segments are being / have been looked up
    Thumbs? thumbs;
    int durationFor = -1;         // the playlist position the length (duration) was reported for
    JsonElement? tracks;          // mpv's track-list (a copy)
    double speed = 1;

    // the parts above, wired in by the constructor
    void SetupPlayerParts()
    {
        root.Children.Add(topBar.Panel);
        root.Children.Add(prompt.Panel);
        root.Children.Add(bar.Preview);
        topBar.Panel.Visibility = Visibility.Collapsed;
        topBar.Back = () => _ = StopVideo();
        topBar.PopOut = TogglePopOut;
        topBar.Panel.SizeChanged += (_, _) => SendGlass();
        prompt.Panel.SizeChanged += (_, _) => SendGlass();
        bar.Previous = () => Send("playlist-prev");
        bar.Next = () => Send("playlist-next");
        bar.Hover = t => _ = ShowPreview(t);
        topBar.Minimize = () => (AppWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter)?.Minimize();
        topBar.Maximize = ToggleMaximize;
        topBar.CloseApp = Close;
    }

    // Dropped or opened files: their own little playlist, in that order. Episode numbers come from the names.
    static Entry EntryFor(List<string> files)
    {
        var videos = files.Select((f, i) =>
        {
            var (s, e) = EpisodeOrder(f);
            return new Video(f, Friendly(f), i + 1, s == int.MaxValue ? -1 : s, s == int.MaxValue || e == 999 ? 0 : e);
        }).ToList();
        return new Entry(ShowGuess(files[0]), null, videos, "episodes", false, Path.GetDirectoryName(files[0]) ?? "", "anime");
    }

    // Folders become their videos (name order); other files are kept as they are.
    internal static List<string> VideosOf(IEnumerable<string> paths) => paths.SelectMany(p =>
        Directory.Exists(p) ? SafeList(() => Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories).Where(IsVideo).OrderBy(f => f, Natural.I).ToList())
        : File.Exists(p) && IsVideo(p) ? new List<string> { p } : new List<string>()).ToList();

    static List<string> SafeList(Func<List<string>> f) { try { return f(); } catch { return new(); } }

    // ---------- each file in the playlist ----------

    void FileChanged(int pos)
    {
        if (entry == null || pos < 0 || pos >= entry.Videos.Count) return;
        // a length that came before this (mpv often reports it before the playlist position) is this file's if no file was
        // known yet; one from the previous file is stale
        if (durationFor >= 0 && durationFor != pos) duration = 0;
        playingIndex = pos;
        var v = entry.Videos[pos];
        currentFile = v.Path; loadedFor = null; subDelay = 0;
        topBar.SetTitle(entry.Title, v.Label);
        Title = $"{v.Label} – Haku Player";
        chapters = new(); segments = new(); segmentsFor = null;
        ResetPrompt();
        up?.Reset(); // the next episode may be another size
        bar.SetMarks(segments, chapters);
        thumbs?.Dispose();
        thumbs = Settings.Current.Previews ? new Thumbs(v.Path) : null;
        if (duration > 0) _ = FindSegments(duration, session);
    }

    // Openings and endings: chapters first, then online times, then (still missing one) comparing with the next episode.
    async Task FindSegments(double length, int my)
    {
        try { await FindSegmentsNow(length, my); }
        catch (Exception ex) { MpvCore.Note("segments: " + ex); }
    }

    async Task FindSegmentsNow(double length, int my)
    {
        if (entry == null || playingIndex < 0 || playingIndex >= entry.Videos.Count || length <= 0) return;
        var e = entry; var v = e.Videos[playingIndex];
        if (segmentsFor == v.Path) return;
        segmentsFor = v.Path;
        await Task.Delay(500); // the chapter list arrives with the file; give it a moment
        bool Still() => my == session && segmentsFor == v.Path;
        if (!Still()) return;
        var found = Segments.FromChapters(chapters, length);
        bool Need() => !Segments.Has(found, "op") || !Segments.Has(found, "ed");
        if (Need() && Settings.Current.OnlineSegments)
        {
            found = Segments.Merge(found, await Segments.Online(e, v, length));
            if (!Still()) return;
        }
        if (Segments.CachedDetection(v.Path) is { } cached) found = Segments.Merge(found, cached);
        MpvCore.Note($"segments for {Path.GetFileName(v.Path)}: {string.Join(", ", found.Select(x => $"{x.Kind} {x.Start:0}-{x.End:0}"))} (chapters: {chapters.Count})");
        SetSegments(found);
        if (Need() && Settings.Current.DetectSegments && Segments.Sibling(e, v) is { } other && Segments.CachedDetection(v.Path) == null)
        {
            var detected = await Segments.Detect(v.Path, length, other.Path);
            if (!Still()) return;
            SetSegments(Segments.Merge(found, detected));
        }
    }

    void SetSegments(List<Segment> s) { segments = s; bar.SetMarks(segments, chapters); UpdatePrompt(); }

    double? EndingStart { get { foreach (var s in segments) if (s.Kind == "ed") return s.Start; return null; } }

    // ---------- Skip opening / Up next (as the WPF app's PlayerSegments) ----------

    string promptKey = "";
    double shownLeft = double.NaN, lastPromptTime;
    bool upNextDismissed, upNextFired;
    double? upNextFrom;
    readonly HashSet<Segment> autoSkipped = new();

    void ResetPrompt()
    {
        upNextDismissed = upNextFired = false; upNextFrom = null; autoSkipped.Clear();
        SetPrompt("", null);
    }

    Segment? SegmentAt(double t, double endPad = 0)
    {
        foreach (var s in segments) if (t >= s.Start && t < s.End - endPad) return s;
        return null;
    }

    static string KindName(string k) => k switch { "op" => "Opening", "ed" => "Ending", _ => "Recap" };

    // with every position update
    void UpdatePrompt()
    {
        var S = Settings.Current;
        if (duration <= 0 || entry == null) { SetPrompt("", null); return; }
        var seg = SegmentAt(position, endPad: 0.5);
        if (S.AutoSkip && seg is { Kind: "op" or "recap" } && autoSkipped.Add(seg)) { SeekTo(seg.End, "absolute+exact"); return; }

        string? next = playingIndex >= 0 && playingIndex + 1 < entry.Videos.Count ? entry.Videos[playingIndex + 1].Label : null;
        double at = EndingStart ?? duration - 20;
        bool jumped = Math.Abs(position - lastPromptTime) > 1.5; // a seek, not playback
        lastPromptTime = position;
        bool barShown = barTarget > 0;
        if (S.UpNext && next != null && !upNextDismissed && duration > 120 && position >= at && (S.Autoplay || barShown || position - at < 8))
        {
            SetPrompt("next", null, next);
            if (upNextFrom == null || jumped || position < upNextFrom) upNextFrom = position;
            double left = Math.Ceiling(10 - (position - upNextFrom.Value));
            if (left != shownLeft) { shownLeft = left; prompt.SetCountdown(S.Autoplay ? $"in {Math.Max(0, left)}" : ""); }
            if (S.Autoplay && left <= 0 && !upNextFired) { upNextFired = true; Send("playlist-next"); }
            return;
        }
        upNextFrom = null;
        if (S.SkipButtons && seg != null && !(seg.Kind == "ed" && seg.End >= duration - 3) && (barShown || position - seg.Start < 8))
        { SetPrompt($"skip:{seg.Kind}:{seg.Start}", seg); return; }
        SetPrompt("", null);
    }

    void SetPrompt(string key, Segment? seg, string? next = null)
    {
        if (key == promptKey) return;
        promptKey = key;
        shownLeft = double.NaN;
        if (key == "") { FadePrompt(false); return; }
        if (key == "next") prompt.ShowUpNext(next ?? "", Settings.Current.Autoplay, () => { upNextFired = true; Send("playlist-next"); }, () => { upNextDismissed = true; SetPrompt("", null); });
        else prompt.ShowSkip(KindName(seg!.Kind), () => { SeekTo(seg.End, "absolute+exact"); Activity(); });
        FadePrompt(true);
    }

    double promptTarget;
    bool promptFading;
    void FadePrompt(bool on)
    {
        promptTarget = on ? 1 : 0;
        if (on) prompt.Panel.Visibility = Visibility.Visible;
        if (promptFading) return;
        promptFading = true;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += PromptFade;
    }

    void PromptFade(object? s, object e)
    {
        double o = prompt.Panel.Opacity, step = 1.0 / 12; // ~200 ms
        o = promptTarget > o ? Math.Min(promptTarget, o + step) : Math.Max(promptTarget, o - step);
        prompt.Panel.Opacity = o;
        SendGlass();
        if (o != promptTarget) return;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= PromptFade;
        promptFading = false;
        if (o == 0) prompt.Panel.Visibility = Visibility.Collapsed;
    }

    // ---------- position: progress, the card ----------

    void PositionChanged(double t)
    {
        // Discord's "time left" is worked out from timestamps: refresh it after a seek
        if (!paused && Math.Abs(t - (discordPos + (DateTime.UtcNow - discordAt).TotalSeconds * speed)) > 5) DiscordUpdate();
        position = t;
        bar.SetTime(t);
        if (entry is { Unit: not "themes" } && playingIndex >= 0 && playingIndex < entry.Videos.Count && duration > 0)
            Watch.Tick(entry.Videos[playingIndex].Path, t, duration, EndingStart);
        UpdatePrompt();
    }

    // ---------- timeline preview ----------

    double previewSecond = -1;
    async Task ShowPreview(double? t)
    {
        if (t is not { } s || thumbs == null) return;
        thumbs.Request(s);
        int sec = (int)Math.Round(s);
        if (sec == previewSecond) return;
        var pic = await thumbs.Picture(s);
        if (pic != null) { previewSecond = sec; bar.PreviewImage.Source = pic; }
    }


    static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // "English · Full Subtitles", "Japanese", "Track 3"
    static string TrackName(JsonElement t)
    {
        var parts = new List<string>();
        var lang = Str(t, "lang");
        if (lang.Length > 0) parts.Add(Language(lang));
        var title = Str(t, "title");
        if (title.Length > 0) parts.Add(title);
        if (parts.Count == 0) parts.Add($"Track {(t.TryGetProperty("id", out var i) ? i.ToString() : "?")}");
        return string.Join(" · ", parts);
    }

    static string Language(string code) => code.ToLowerInvariant() switch
    {
        "jpn" or "ja" => "Japanese", "eng" or "en" => "English", "spa" or "es" => "Spanish", "fre" or "fra" or "fr" => "French",
        "ger" or "deu" or "de" => "German", "ita" or "it" => "Italian", "por" or "pt" => "Portuguese", "chi" or "zho" or "zh" => "Chinese",
        "kor" or "ko" => "Korean", "rus" or "ru" => "Russian", "ara" or "ar" => "Arabic", _ => code,
    };
}
