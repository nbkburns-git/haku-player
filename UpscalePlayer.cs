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

    // A new file in the playlist: decide again.
    void ResetUpscale()
    {
        guardFrom = DateTime.MaxValue;
        upscaleKey = null;
        upscaleBlocked = false;
        dropBase = -1;
    }

    int ScreenHeight()
    {
        try { return System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).Bounds.Height; } // physical pixels (per-monitor DPI aware)
        catch { return 0; }
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
        var tier = upscaleBlocked ? null : Upscaler.Choose(videoWidth, videoHeight, videoFps, ScreenHeight(), out why);
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
        if (tier == null) { controls?.SetUpscaleButton(Settings.Current.Upscale, false, $"AI upscaling: {why}"); return; }

        if (!File.Exists(Upscaler.EngineFile(model)))
        {
            Toast($"Preparing AI upscaling ({tier.Name}): once, about 15 seconds");
            controls?.SetUpscaleButton(true, false, $"AI upscaling: preparing {tier.Name} (once, about 15 seconds)");
        }
        string? err = await Upscaler.Prepare(model);
        if (my != session || upscaleKey != key) return;
        if (err != null)
        {
            Toast($"AI upscaling unavailable: {err}");
            controls?.SetUpscaleButton(true, false, $"AI upscaling unavailable: {err}");
            return;
        }

        Send(new object[] { "vf", "add", Upscaler.Filter(model) });
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
        }
        else
        {
            Send(new object[] { "vf", "remove", "@" + Upscaler.Label });
            upscaleActive = false;
            controls?.SetUpscaleButton(true, false, "AI upscaling couldn't start for this video");
            Toast("AI upscaling couldn't start for this video");
        }
    }

    // Safety net: 12 or more dropped frames within 10 seconds means the GPU isn't keeping up.
    void CheckUpscaleDrops(long drops, int my)
    {
        var now = DateTime.UtcNow;
        if (!upscaleActive || now < guardFrom) return;
        if (dropBase < 0 || (now - dropAt).TotalSeconds > 10) { dropBase = drops; dropAt = now; return; }
        if (drops - dropBase < 12) return;
        upscaleBlocked = true;
        UpdateUpscale(my);
        Toast("AI upscaling was dropping frames, so it's off for this video");
    }
}
