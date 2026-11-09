using System;
using Microsoft.UI.Windowing;

namespace AnimePlayer;

// AI upscaling in the WinUI player: the shared UpscaleSession does the work (tier, engine, filter, buffer, safety net), as
// in the WPF app; this shows what it says (AI button, badge, loading panel, green buffer, toasts). One per mpv.
public partial class MainWindow : IUpscaleView
{
    UpscaleSession? up;
    AiLoadingPanel aiLoading = null!;
    ToastBox toast = null!;
    string? aiTipNow;
    int upscaleHeight; string upscaleTag = "AI";

    void SetupUpscaleParts()
    {
        aiLoading = new AiLoadingPanel();
        toast = new ToastBox(DispatcherQueue);
        root.Children.Add(aiLoading.Panel);
        root.Children.Add(toast.Panel);
        bar.ToggleAi = ToggleAi;
        bar.SeekStarted = () => up?.TimelineSeekStart();
    }

    UpscaleSession NewUpscaleSession() => up = new UpscaleSession(c => Send(c), c => ipc?.Ask(c) ?? System.Threading.Tasks.Task.FromResult<string?>(null), this, () => entry, () => session);

    // The AI button and Ctrl+U
    void ToggleAi()
    {
        if (up != null) up.Toggle();
        else { Settings.Current.Upscale = !Settings.Current.Upscale; Settings.Current.Save(); Toast(Settings.Current.Upscale ? "AI upscaling on" : "AI upscaling off"); }
    }

    // Every seek goes through here: the AI filter is told where it's going (its frames on the way skip the AI)
    void SeekTo(double t, string mode)
    {
        up?.BeforeSeek(t, mode);
        Send("seek", t, mode);
    }

    public void Toast(string text)
    {
        toast.Panel.Margin = new Microsoft.UI.Xaml.Thickness(0, 0, 0, core != null ? 120 : 36); // over the player: above its pill
        toast.Show(text);
    }

    // ---- IUpscaleView ----
    bool IUpscaleView.Paused => paused;
    double IUpscaleView.Position => position;
    void IUpscaleView.SetAiLoading(string? text, int frames, int cap, string? sub)
    {
        aiLoading.Show(text, frames, cap, sub);
        bar.SetAiBusy(text != null);
        if (text == null && aiTipNow != null) bar.SetAiButton(aiOn, aiActive, aiTipNow);
    }
    bool aiOn, aiActive;
    void IUpscaleView.SetUpscale(int outHeight, string tag) { upscaleHeight = outHeight; upscaleTag = tag; UpdateQuality(); }
    void IUpscaleView.SetUpscaleButton(bool on, bool active, string tip) { aiOn = on; aiActive = active; aiTipNow = tip; if (!aiLoading.On) bar.SetAiButton(on, active, tip); }
    void IUpscaleView.SetAiBuffer(int frames, int cap, double until)
    {
        bar.SetAiBuffer(frames, cap, until);
        if (aiTipNow != null && !aiLoading.On) bar.SetAiTip(cap > 0 ? $"{aiTipNow}\n{frames} of {cap} frames ready ahead" : aiTipNow);
    }
    void IUpscaleView.Toast(string text) => Toast(text);
    int IUpscaleView.ScreenHeight()
    {
        try { return DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).OuterBounds.Height; }
        catch { return 0; }
    }

}
