using System;
using System.IO;
using System.Threading.Tasks;

namespace AnimePlayer;

// What the upscaling needs from the player on screen (the WPF app's PlayerControls, the WinUI app's bar).
interface IUpscaleView
{
    bool Paused { get; }                 // paused (by you or by the AI buffer)
    double Position { get; }             // seconds
    void SetAiLoading(string? text, int frames = 0, int cap = 0, string? sub = null); // the loading panel (null: hide)
    void SetUpscale(int outHeight, string tag = "AI");                                  // the badge (0: none)
    void SetUpscaleButton(bool on, bool active, string tip);                           // the AI button
    void SetAiBuffer(int frames, int cap, double until);                                // the timeline's green bar
    void Toast(string text);
    int ScreenHeight();                  // the screen the player is on, in real pixels
}

// The player's side of AI upscaling (Upscaler.cs), shared by both apps (no UI in here): picks a tier for each video, builds
// its TensorRT engine the first time (the video plays normally meanwhile), adds mpv's vf_hakusr filter, keeps the AI button
// up to date, and turns upscaling off again if frames start dropping. One per playback session (mpv instance).
sealed class UpscaleSession
{
    readonly Action<object?[]> send;
    readonly Func<object?[], Task<string?>> ask;
    readonly IUpscaleView view;
    readonly Func<Entry?> entry;     // what's playing
    readonly Func<int> current;      // the app's current session number: work for an older one stops

    public int Width, Height;        // the video's size (from mpv's width / height)
    public double Fps;               // its frame rate (container-fps)
    public bool Active { get; private set; }   // our filter is in mpv's chain
    public bool Holding { get; private set; }  // we paused the video for the AI

    string? upscaleKey;      // model + size wanted for the current video ("" = none)
    bool upscaleBlocked;     // dropped frames on this video: leave it off
    long dropBase = -1;
    DateTime dropAt;
    DateTime guardFrom = DateTime.MaxValue; // inserting the filter (engine load) drops a few frames: count only after this

    public UpscaleSession(Action<object?[]> send, Func<object?[], Task<string?>> ask, IUpscaleView view, Func<Entry?> entry, Func<int> current)
    {
        this.send = send; this.ask = ask; this.view = view; this.entry = entry; this.current = current;
    }

    // ---------- the AI buffer: a short pause at the start and after a timeline seek, so the AI is running before playback ----------

    int aiHoldId;
    bool releaseOnRestart; // resume once mpv finishes the seek (its "playback-restart" event)

    public void Hold()
    {
        if (!Holding) send(new object?[] { "set_property", "pause", true });
        Holding = true;
        view.SetAiLoading(Active ? "AI buffering" : "Starting AI upscaling");
        ReleaseLater(++aiHoldId); // never stuck paused
    }

    // 12 s on: let go if this hold is still the current one (awaited on the UI thread)
    async void ReleaseLater(int id)
    {
        await Task.Delay(12000);
        if (aiHoldId == id) Release(0);
    }

    async void Release(int afterMs)
    {
        if (!Holding) return;
        int id = ++aiHoldId;
        if (afterMs > 0) await Task.Delay(afterMs);
        if (!Holding || id != aiHoldId) return;
        Holding = false;
        if (Active) guardFrom = DateTime.UtcNow.AddSeconds(3); // the first frames after a seek may drop: don't count them
        view.SetAiLoading(null);
        send(new object?[] { "set_property", "pause", false });
    }

    // From the player's timeline: pressed (pause while you pick a spot) and released (resume a second after the seek lands).
    public void TimelineSeekStart() { if (Active && !view.Paused) Hold(); }
    public void TimelineSeekEnd() { if (Holding) releaseOnRestart = true; }

    // Any exact seek: tell the filter where it's going, so the frames decoded on the way there skip the AI (vf_hakusr skip_until).
    public void BeforeSeek(double t, string mode)
    {
        if (Active) guardFrom = DateTime.UtcNow.AddSeconds(10); // until the seek lands (PlaybackRestart shortens it)
        if (Active && mode.Contains("exact"))
            send(new object?[] { "vf-command", Upscaler.Label, "skip_until", t.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) });
    }

    // mpv events: a seek finished (any seek: the first frames after it may drop, so the safety net waits); the pause changed
    // (pressing play during the buffer wins)
    public void PlaybackRestart()
    {
        if (Active) guardFrom = DateTime.UtcNow.AddSeconds(3);
        if (releaseOnRestart) { releaseOnRestart = false; Release(1000); }
    }
    public void PauseChanged(bool paused) { if (!paused) { Holding = false; releaseOnRestart = false; if (!aiStarting) view.SetAiLoading(null); } }
    bool aiStarting; // the filter is going in and its first upscaled frame isn't out yet (the loading panel stays meanwhile)

    // A new file in the playlist: decide again.
    public void Reset()
    {
        guardFrom = DateTime.MaxValue;
        upscaleKey = null;
        upscaleBlocked = false;
        tierLimit = int.MaxValue;
        aiStarting = false;
        dropBase = -1;
    }

    int tierLimit = int.MaxValue; // this video: Auto may use no tier slower than Tiers[tierLimit] (it couldn't keep up)
    int tierIndex = -1;           // the tier upscaling now (index into Upscaler.Tiers, fastest first)
    DateTime lowSince = DateTime.MaxValue;

    // Auto's pick can't keep up with this video (frames dropping, or the AI buffer running dry): use the next faster
    // tier for it. False when there's none (or you chose the tier yourself), so the caller turns upscaling off.
    bool StepDown(int my)
    {
        if (Settings.Current.UpscaleTier != "auto" || tierIndex <= 0) return false;
        tierLimit = tierIndex - 1;
        upscaleKey = null;
        Update(my);
        view.Toast($"AI upscaling: {Upscaler.Tiers[tierLimit].Name} for this video, to keep up");
        return true;
    }

    // Settings changed (or the button / Ctrl+U): decide again for the video that's playing.
    public void Apply() { upscaleKey = null; Update(current()); }

    // The player button and Ctrl+U: AI upscaling on or off (saved, like the setting).
    public void Toggle()
    {
        var s = Settings.Current;
        s.Upscale = !s.Upscale;
        s.Save();
        upscaleBlocked = false; // a fresh try if you turn it back on
        if (!s.Upscale) view.Toast("AI upscaling off");
        Apply();
    }

    // TV shows and movies: NVIDIA's RTX Video (made for live action), the anime model, or nothing (Settings.UpscaleLiveAction).
    // Cartoons in the TV or movie library (South Park, Rick and Morty: IMDb's "Animation" genre, in the show's details) are
    // drawn like anime, so they get the anime model.
    string LiveActionMode => entry() is not { Kind: "tv" or "movie" } e ? "anime"
        : Settings.Current.UpscaleLiveAction == "rtx" && IsAnimated(e) ? "anime"
        : Settings.Current.UpscaleLiveAction;

    public static bool IsAnimated(Entry e) =>
        InfoService.Cached(e.InfoKey)?.Genres is { } genres && Array.Exists(genres, g => string.Equals(g, "Animation", StringComparison.OrdinalIgnoreCase));

    public async void Update(int my)
    {
        if (Width <= 0 || Height <= 0) return;
        if (LiveActionMode == "rtx") { await UpdateRtx(my); return; }
        string why = "dropped frames on this video, so it turned itself off";
        var tier = upscaleBlocked ? null : Upscaler.Choose(Width, Height, Fps, out why);
        if (tier != null && LiveActionMode == "off") { tier = null; why = "off for TV shows and movies (Settings, AI upscaling)"; }
        if (tier != null && Settings.Current.UpscaleTier == "auto" && Array.IndexOf(Upscaler.Tiers, tier) > tierLimit)
            tier = Upscaler.Tiers[tierLimit]; // stepped down for this video
        tierIndex = tier == null ? -1 : Array.IndexOf(Upscaler.Tiers, tier);
        string look = Settings.Current.UpscaleLook;
        string model = tier == null ? "" : Upscaler.ModelFile(tier, look);
        int w = Width, h = Height;
        string key = tier == null ? "" : $"{model}|{w}x{h}";
        if (key == upscaleKey) return;
        upscaleKey = key;
        if (Active)
        {
            send(new object?[] { "vf", "remove", "@" + Upscaler.Label });
            Active = false;
            view.SetUpscale(0);
        }
        if (tier == null)
        {
            view.SetUpscaleButton(Settings.Current.Upscale, false, $"AI upscaling: {why}");
            aiStarting = false;
            if (w > 0 && h > 0) { Release(0); view.SetAiLoading(null); } // not upscaling this video: no reason to wait
            return;
        }

        if (!File.Exists(Upscaler.EngineFile(model)))
        {
            Release(0); // play normally while the engine is built (seconds); the buffer comes back when it's added
            // Adore is bigger: its engine takes about 1.5 minutes on a 4070, plus a 3 MB download the first time
            string how = tier.Key == "adore" ? "a minute or two" : "about 15 seconds";
            string doing = File.Exists(model) ? "Preparing" : "Downloading and preparing";
            view.SetUpscaleButton(true, false, $"AI upscaling: {doing.ToLowerInvariant()} {tier.Name} (once, {how})");
            view.SetAiLoading($"{doing} AI upscaling ({tier.Name})", sub: $"Once for your graphics card, {how}. Playing meanwhile.");
        }
        string? err = await Upscaler.Prepare(model);
        if (my != current() || upscaleKey != key) return;
        if (err != null)
        {
            view.Toast($"AI upscaling unavailable: {err}");
            view.SetUpscaleButton(true, false, $"AI upscaling unavailable: {err}");
            Release(0);
            view.SetAiLoading(null);
            return;
        }

        // the buffer holds a second of video: ask the frame rate if it hasn't arrived yet (it often comes after the size)
        if (Fps <= 0 && double.TryParse(await ask(new object?[] { "get_property_string", "container-fps" }), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fpsNow)) Fps = fpsNow;
        if (my != current() || upscaleKey != key) return;
        bool pausedByYou = view.Paused && !Holding;
        if (!pausedByYou) Hold(); // the filter loads its engine before the first upscaled frame
        aiStarting = true;
        view.SetAiLoading("Starting AI upscaling");
        send(new object?[] { "vf", "add", Upscaler.Filter(model, Fps) });
        // paused: mpv redraws the paused frame through the new filter by seeking to it, decoding from the keyframe before;
        // those frames are thrown away, so they skip the AI (otherwise the AI picture only appeared on pressing play)
        if (pausedByYou)
            send(new object?[] { "vf-command", Upscaler.Label, "skip_until", view.Position.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) });
        Active = true;
        dropBase = -1;
        // it took once mpv's output is the model's size (the filter passes frames through when it can't upscale);
        // loading the engine and getting the first upscaled frame out takes a second or two
        bool took = false;
        for (int i = 0; i < 20 && !took; i++)
        {
            await Task.Delay(500);
            if (my != current() || upscaleKey != key) return;
            string? ow = await ask(new object?[] { "get_property_string", "video-out-params/w" });
            took = int.TryParse(ow, out int outW) && outW >= w * 2 - 1;
            string? f0 = await ask(new object?[] { "get_property_string", $"vf-metadata/{Upscaler.Label}/frames" });
            string? c0 = await ask(new object?[] { "get_property_string", $"vf-metadata/{Upscaler.Label}/cap" });
            if (aiStarting && int.TryParse(c0, out int cc) && cc > 2) view.SetAiLoading("Starting AI upscaling", int.TryParse(f0, out int ff) ? ff : 0, cc);
        }
        aiStarting = false;
        if (took)
        {
            guardFrom = DateTime.UtcNow.AddSeconds(3);
            view.SetUpscale(h * 2);
            string what = tier.Key == "adore" ? "Adore" : $"{tier.Name}, {(look == "soft" ? "soft" : "sharp")} look"; // Adore has no looks
            view.SetUpscaleButton(true, true, $"AI upscaling on: {what}, {w}×{h} → {w * 2}×{h * 2} (Ctrl+U)");
            view.Toast($"AI upscaling on: {what}");
            if (Holding) Release(1000); else view.SetAiLoading(null); // paused by you: it's ready, nothing to wait for
            PollAiBuffer(my, key);
        }
        else
        {
            send(new object?[] { "vf", "remove", "@" + Upscaler.Label });
            Active = false;
            Release(0);
            view.SetAiLoading(null);
            view.SetUpscaleButton(true, false, "AI upscaling couldn't start for this video");
            view.Toast("AI upscaling couldn't start for this video");
        }
    }

    // RTX Video Super Resolution: NVIDIA's own live-action upscaler, built into the driver and reached through mpv's d3d11vpp
    // filter. Light on the GPU (no model of ours, no buffer), sized to fill the screen's height. Same filter label as the AI, so
    // turning it off, the button and the safety net work the same way.
    async Task UpdateRtx(int my)
    {
        var s = Settings.Current;
        int w = Width, h = Height;
        string? gamma = await ask(new object?[] { "get_property_string", "video-params/gamma" });
        if (my != current()) return;
        int screen = view.ScreenHeight();
        double scale = h > 0 && screen > 0 ? Math.Min(4.0, (double)screen / h) : 2.0;
        string? why = !s.Upscale ? "turned off"
            : upscaleBlocked ? "dropped frames on this video, so it turned itself off"
            : Upscaler.Speed <= 0 ? "RTX Video needs an NVIDIA RTX graphics card"
            : gamma is "pq" or "hlg" ? "HDR plays without upscaling for now"
            : scale < 1.2 ? $"not needed: {w}×{h} already fills your screen" : null;
        string key = why == null ? $"rtx|{w}x{h}|{scale:0.###}" : "";
        if (key == upscaleKey) return;
        upscaleKey = key;
        tierIndex = -1;
        if (Active)
        {
            send(new object?[] { "vf", "remove", "@" + Upscaler.Label });
            Active = false;
            view.SetUpscale(0);
        }
        if (why != null)
        {
            view.SetUpscaleButton(s.Upscale, false, $"Upscaling: {why}");
            aiStarting = false;
            Release(0);
            view.SetAiLoading(null);
            return;
        }
        bool pausedByYou = view.Paused && !Holding;
        if (!pausedByYou) Hold();
        aiStarting = true;
        view.SetAiLoading("Starting RTX Video", sub: "NVIDIA's upscaler for live action");
        // format=nv12: RTX Video takes 8-bit frames (10-bit video is converted on the way)
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        send(new object?[] { "vf", "add", $"@{Upscaler.Label}:d3d11vpp=scale={scale.ToString("0.###", inv)}:scaling-mode=nvidia:format=nv12" });
        Active = true;
        dropBase = -1;
        int ow = 0, oh = (int)Math.Round(h * scale);
        bool took = false;
        for (int i = 0; i < 20 && !took; i++)
        {
            await Task.Delay(500);
            if (my != current() || upscaleKey != key) return;
            took = int.TryParse(await ask(new object?[] { "get_property_string", "video-out-params/w" }), out ow) && ow >= w * scale - 2;
        }
        aiStarting = false;
        if (took)
        {
            guardFrom = DateTime.UtcNow.AddSeconds(3);
            view.SetUpscale(oh, "RTX");
            view.SetUpscaleButton(true, true, $"RTX Video upscaling on: {w}×{h} → {ow}×{oh}, NVIDIA's upscaler for live action (Ctrl+U)");
            view.Toast("RTX Video upscaling on");
            if (Holding) Release(500); else view.SetAiLoading(null);
        }
        else
        {
            send(new object?[] { "vf", "remove", "@" + Upscaler.Label });
            Active = false;
            Release(0);
            view.SetAiLoading(null);
            view.SetUpscaleButton(true, false, "RTX Video couldn't start for this video");
            view.Toast("RTX Video couldn't start for this video");
        }
    }

    // The filter's queue of upscaled frames ready ahead of playback, for the timeline's green bar (4 times a second).
    async void PollAiBuffer(int my, string key)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        while (my == current() && Active && upscaleKey == key)
        {
            string? frames = await ask(new object?[] { "get_property_string", $"vf-metadata/{Upscaler.Label}/frames" });
            string? until = await ask(new object?[] { "get_property_string", $"vf-metadata/{Upscaler.Label}/until" });
            string? cap = await ask(new object?[] { "get_property_string", $"vf-metadata/{Upscaler.Label}/cap" });
            if (my != current() || !Active || upscaleKey != key) break;
            // the AI buffer nearly empty for 2 s while playing (and not just after a seek): this tier can't keep up
            if (int.TryParse(frames, out int ready) && ready <= 2 && !view.Paused && !Holding && DateTime.UtcNow > guardFrom)
            {
                if (lowSince == DateTime.MaxValue) lowSince = DateTime.UtcNow;
                else if ((DateTime.UtcNow - lowSince).TotalSeconds >= 2) { lowSince = DateTime.MaxValue; if (StepDown(my)) break; }
            }
            else lowSince = DateTime.MaxValue;
            if (Holding && int.TryParse(cap, out int hc) && hc > 0) view.SetAiLoading("AI buffering", int.TryParse(frames, out int hf) ? hf : 0, hc);
            view.SetAiBuffer(int.TryParse(frames, out int n) ? n : 0, int.TryParse(cap, out int c) ? c : 0,
                double.TryParse(until, System.Globalization.NumberStyles.Float, inv, out double u) ? u : -1);
            await Task.Delay(250);
        }
        if (my == current()) view.SetAiBuffer(0, 0, -1);
    }

    // Safety net: 12 or more dropped frames within 10 seconds means the GPU isn't keeping up.
    public void CheckDrops(long drops, int my)
    {
        var now = DateTime.UtcNow;
        if (!Active || now < guardFrom) { dropBase = -1; return; } // drops in the grace period don't count later either
        if (dropBase < 0 || (now - dropAt).TotalSeconds > 10) { dropBase = drops; dropAt = now; return; }
        if (drops - dropBase < 12) return;
        dropBase = -1;
        if (StepDown(my)) return; // a faster tier first
        upscaleBlocked = true;
        Update(my);
        view.Toast("AI upscaling was dropping frames, so it's off for this video");
    }
}
