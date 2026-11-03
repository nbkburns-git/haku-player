using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Interop;

namespace AnimePlayer;

// The player's side of AI upscaling (Upscaler.cs): picks a tier for each video, builds its TensorRT engine the first time
// (the video plays normally meanwhile), adds mpv's vf_hakusr filter, keeps the player's AI button up to date, and turns
// upscaling off again if frames start dropping.
public partial class MainWindow
{
    int videoWidth;
    double videoFps;
    string? upscaleKey;      // model + size wanted for the current video ("" = none)
    bool upscaleActive;      // our filter is in mpv's chain
    bool upscaleBlocked;     // dropped frames on this video: leave it off
    long dropBase = -1;
    DateTime dropAt;
    DateTime guardFrom = DateTime.MaxValue; // inserting the filter (engine load) drops a few frames: count only after this

    // ---------- the AI buffer: a short pause at the start and after a timeline seek, so the AI is running before playback ----------

    bool aiHolding;        // we paused the video for the AI
    int aiHoldId;
    bool releaseOnRestart; // resume once mpv finishes the seek (its "playback-restart" event)

    void AiHold()
    {
        if (!aiHolding) Send(new object[] { "set_property", "pause", true });
        aiHolding = true;
        int id = ++aiHoldId;
        _ = Task.Delay(12000).ContinueWith(_ => Dispatcher.Invoke(() => { if (aiHoldId == id) AiRelease(0); })); // never stuck paused
    }

    async void AiRelease(int afterMs)
    {
        if (!aiHolding) return;
        int id = ++aiHoldId;
        if (afterMs > 0) await Task.Delay(afterMs);
        if (!aiHolding || id != aiHoldId) return;
        aiHolding = false;
        if (upscaleActive) guardFrom = DateTime.UtcNow.AddSeconds(3); // the first frames after a seek may drop: don't count them
        Send(new object[] { "set_property", "pause", false });
    }

    // From the player's timeline: pressed (pause while you pick a spot) and released (resume a second after the seek lands).
    void TimelineSeekStart() { if (upscaleActive && controls is { Paused: false }) AiHold(); }
    void TimelineSeekEnd() { if (aiHolding) releaseOnRestart = true; }

    // Any exact seek: tell the filter where it's going, so the frames decoded on the way there skip the AI (vf_hakusr skip_until).
    void BeforeSeek(double t, string mode)
    {
        if (upscaleActive) guardFrom = DateTime.UtcNow.AddSeconds(10); // until the seek lands (AiPlaybackRestart shortens it)
        if (upscaleActive && mode.Contains("exact"))
            Send(new object[] { "vf-command", Upscaler.Label, "skip_until", t.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) });
    }

    // mpv events: a seek finished (any seek: the first frames after it may drop, so the safety net waits); the pause changed
    // (pressing play during the buffer wins)
    void AiPlaybackRestart()
    {
        if (upscaleActive) guardFrom = DateTime.UtcNow.AddSeconds(3);
        if (releaseOnRestart) { releaseOnRestart = false; AiRelease(1000); }
    }
    void AiPauseChanged(bool paused) { if (!paused) { aiHolding = false; releaseOnRestart = false; } }

    // A new file in the playlist: decide again.
    void ResetUpscale()
    {
        guardFrom = DateTime.MaxValue;
        upscaleKey = null;
        upscaleBlocked = false;
        tierLimit = int.MaxValue;
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
        UpdateUpscale(my);
        Toast($"AI upscaling: {Upscaler.Tiers[tierLimit].Name} for this video, to keep up");
        return true;
    }

    // Settings changed: apply to the video that's playing.
    void ApplyUpscaleSetting()
    {
        if (mpv == null) return;
        upscaleKey = null;
        UpdateUpscale(session);
    }

    // The player button and Ctrl+U: AI upscaling on or off (saved, like the setting).
    void ToggleUpscaleNow()
    {
        var s = Settings.Current;
        s.Upscale = !s.Upscale;
        s.Save();
        upscaleBlocked = false; // a fresh try if you turn it back on
        if (!s.Upscale) Toast("AI upscaling off");
        ApplyUpscaleSetting();
        if (SettingsLayer.IsVisible) BuildGlobalSettings();
    }

    async void UpdateUpscale(int my)
    {
        if (videoWidth <= 0 || videoHeight <= 0) return;
        string why = "dropped frames on this video, so it turned itself off";
        var tier = upscaleBlocked ? null : Upscaler.Choose(videoWidth, videoHeight, videoFps, out why);
        if (tier != null && Settings.Current.UpscaleTier == "auto" && Array.IndexOf(Upscaler.Tiers, tier) > tierLimit)
            tier = Upscaler.Tiers[tierLimit]; // stepped down for this video
        tierIndex = tier == null ? -1 : Array.IndexOf(Upscaler.Tiers, tier);
        string look = Settings.Current.UpscaleLook;
        string model = tier == null ? "" : Upscaler.ModelFile(tier, look);
        int w = videoWidth, h = videoHeight;
        string key = tier == null ? "" : $"{model}|{w}x{h}";
        if (key == upscaleKey) return;
        upscaleKey = key;
        if (upscaleActive)
        {
            Send(new object[] { "vf", "remove", "@" + Upscaler.Label });
            upscaleActive = false;
            controls?.SetUpscale(0);
        }
        if (tier == null)
        {
            controls?.SetUpscaleButton(Settings.Current.Upscale, false, $"AI upscaling: {why}");
            if (w > 0 && h > 0) AiRelease(0); // not upscaling this video: no reason to wait
            return;
        }

        if (!File.Exists(Upscaler.EngineFile(model)))
        {
            AiRelease(0); // play normally while the engine is built (seconds); the buffer comes back when it's added
            Toast($"Preparing AI upscaling ({tier.Name}): once, about 15 seconds");
            controls?.SetUpscaleButton(true, false, $"AI upscaling: preparing {tier.Name} (once, about 15 seconds)");
        }
        string? err = await Upscaler.Prepare(model);
        if (my != session || upscaleKey != key) return;
        if (err != null)
        {
            Toast($"AI upscaling unavailable: {err}");
            controls?.SetUpscaleButton(true, false, $"AI upscaling unavailable: {err}");
            AiRelease(0);
            return;
        }

        if (controls is { Paused: false } || aiHolding) AiHold(); // the filter loads its engine before the first upscaled frame
        Send(new object[] { "vf", "add", Upscaler.Filter(model, videoFps) });
        upscaleActive = true;
        dropBase = -1;
        // it took once mpv's output is the model's size (the filter passes frames through when it can't upscale);
        // loading the engine and getting the first upscaled frame out takes a second or two
        bool took = false;
        for (int i = 0; i < 20 && !took; i++)
        {
            await Task.Delay(500);
            if (my != session || upscaleKey != key) return;
            string? ow = await Ask(new object[] { "get_property_string", "video-out-params/w" });
            took = int.TryParse(ow, out int outW) && outW >= w * 2 - 1;
        }
        if (took)
        {
            guardFrom = DateTime.UtcNow.AddSeconds(3);
            controls?.SetUpscale(h * 2);
            controls?.SetUpscaleButton(true, true, $"AI upscaling on: {tier.Name}, {(look == "soft" ? "soft" : "sharp")} look, {w}×{h} → {w * 2}×{h * 2} (Ctrl+U)");
            Toast($"AI upscaling on: {tier.Name}, {(look == "soft" ? "soft" : "sharp")} look");
            AiRelease(1000);
            PollAiBuffer(my, key);
        }
        else
        {
            Send(new object[] { "vf", "remove", "@" + Upscaler.Label });
            upscaleActive = false;
            AiRelease(0);
            controls?.SetUpscaleButton(true, false, "AI upscaling couldn't start for this video");
            Toast("AI upscaling couldn't start for this video");
        }
    }

    // The filter's queue of upscaled frames ready ahead of playback, for the timeline's green bar (4 times a second).
    async void PollAiBuffer(int my, string key)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        while (my == session && upscaleActive && upscaleKey == key)
        {
            string? frames = await Ask(new object[] { "get_property_string", $"vf-metadata/{Upscaler.Label}/frames" });
            string? until = await Ask(new object[] { "get_property_string", $"vf-metadata/{Upscaler.Label}/until" });
            string? cap = await Ask(new object[] { "get_property_string", $"vf-metadata/{Upscaler.Label}/cap" });
            if (my != session || !upscaleActive || upscaleKey != key) break;
            // the AI buffer nearly empty for 2 s while playing (and not just after a seek): this tier can't keep up
            if (int.TryParse(frames, out int ready) && ready <= 2 && controls is { Paused: false } && !aiHolding && DateTime.UtcNow > guardFrom)
            {
                if (lowSince == DateTime.MaxValue) lowSince = DateTime.UtcNow;
                else if ((DateTime.UtcNow - lowSince).TotalSeconds >= 2) { lowSince = DateTime.MaxValue; if (StepDown(my)) break; }
            }
            else lowSince = DateTime.MaxValue;
            controls?.SetAiBuffer(int.TryParse(frames, out int n) ? n : 0, int.TryParse(cap, out int c) ? c : 0,
                double.TryParse(until, System.Globalization.NumberStyles.Float, inv, out double u) ? u : -1);
            await Task.Delay(250);
        }
        if (my == session) controls?.SetAiBuffer(0, 0, -1);
    }

    // Safety net: 12 or more dropped frames within 10 seconds means the GPU isn't keeping up.
    void CheckUpscaleDrops(long drops, int my)
    {
        var now = DateTime.UtcNow;
        if (!upscaleActive || now < guardFrom) { dropBase = -1; return; } // drops in the grace period don't count later either
        if (dropBase < 0 || (now - dropAt).TotalSeconds > 10) { dropBase = drops; dropAt = now; return; }
        if (drops - dropBase < 12) return;
        dropBase = -1;
        if (StepDown(my)) return; // a faster tier first
        upscaleBlocked = true;
        UpdateUpscale(my);
        Toast("AI upscaling was dropping frames, so it's off for this video");
    }
}
