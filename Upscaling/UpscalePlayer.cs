using System;
using System.Threading.Tasks;
using System.Windows.Interop;

namespace AnimePlayer;

// The WPF player's side of AI upscaling: the shared UpscaleSession does the work (tier, engine, filter, buffer, safety net);
// this hands it mpv's numbers and shows what it says on the player (PlayerControls). One session per mpv.
public partial class MainWindow : IUpscaleView
{
    int videoWidth;
    double videoFps;
    UpscaleSession? up;
    bool upscaleActive => up?.Active == true;   // our filter is in mpv's chain
    bool aiHolding => up?.Holding == true;       // we paused the video for the AI

    UpscaleSession NewUpscaleSession() => up = new UpscaleSession(c => Send(c!), Ask!, this, () => playingEntry, () => session);

    void AiHold() => up?.Hold();
    void TimelineSeekStart() => up?.TimelineSeekStart();
    void TimelineSeekEnd() => up?.TimelineSeekEnd();
    void BeforeSeek(double t, string mode) => up?.BeforeSeek(t, mode);
    void AiPlaybackRestart() => up?.PlaybackRestart();
    void AiPauseChanged(bool paused) => up?.PauseChanged(paused);
    void ResetUpscale() => up?.Reset();
    void CheckUpscaleDrops(long drops, int my) => up?.CheckDrops(drops, my);

    void UpdateUpscale(int my)
    {
        if (up == null) return;
        up.Width = videoWidth; up.Height = videoHeight; up.Fps = videoFps;
        up.Update(my);
    }

    // Settings changed: apply to the video that's playing.
    void ApplyUpscaleSetting()
    {
        if (mpv == null || up == null) return;
        up.Width = videoWidth; up.Height = videoHeight; up.Fps = videoFps;
        up.Apply();
    }

    // The player button and Ctrl+U: AI upscaling on or off (saved, like the setting).
    void ToggleUpscaleNow()
    {
        if (up != null) { up.Width = videoWidth; up.Height = videoHeight; up.Fps = videoFps; up.Toggle(); }
        else { Settings.Current.Upscale = !Settings.Current.Upscale; Settings.Current.Save(); }
        if (SettingsLayer.IsVisible) BuildGlobalSettings();
    }

    internal static bool IsAnimated(Entry e) => UpscaleSession.IsAnimated(e);

    // ---- IUpscaleView: the player on screen ----
    bool IUpscaleView.Paused => controls is { Paused: true };
    double IUpscaleView.Position => controls?.Position ?? 0;
    void IUpscaleView.SetAiLoading(string? text, int frames, int cap, string? sub) => controls?.SetAiLoading(text, frames, cap, sub);
    void IUpscaleView.SetUpscale(int outHeight, string tag) => controls?.SetUpscale(outHeight, tag);
    void IUpscaleView.SetUpscaleButton(bool on, bool active, string tip) => controls?.SetUpscaleButton(on, active, tip);
    void IUpscaleView.SetAiBuffer(int frames, int cap, double until) => controls?.SetAiBuffer(frames, cap, until);
    void IUpscaleView.Toast(string text) => Toast(text);

    // The height of the screen Haku is on, in real pixels (Haku is per-monitor DPI aware, so this is exact).
    int IUpscaleView.ScreenHeight()
    {
        try { return System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).Bounds.Height; }
        catch { return 0; }
    }
}
