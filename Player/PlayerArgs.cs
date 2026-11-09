using System.Globalization;
using System.IO;

namespace AnimePlayer;

// mpv's options for one playback session (a playlist file, starting at index). No UI in here: both the WPF and the
// WinUI app start mpv with exactly these. unit: "episodes", "videos" or "themes"; kind: "anime", "tv" or "movie".
static class PlayerArgs
{
    public static readonly string GlassShader = Path.Combine(System.AppContext.BaseDirectory, "Assets", "glass.glsl");

    public static string[] Build(string pipe, string playlist, int index, string unit, string kind, double volume, bool aiStart)
    {
        var s = Settings.Current;
        var inv = CultureInfo.InvariantCulture;
        bool resume = s.Resume && unit != "themes";
        return new[]
        {
            "--drag-and-drop=no", // files dropped on the video come to us instead (DropPlay.cs)
            $@"--input-ipc-server=\\.\pipe\{pipe}",
            "--profile=high-quality",
            // decode on the GPU; AI upscaling needs D3D11 frames (vf_hakusr), which d3d11va gives
            s.Upscale && Upscaler.Installed ? "--hwdec=d3d11va,auto-safe" : "--hwdec=auto-safe",
            // shows resume where you left off; openings/endings always play from the start
            $"--save-position-on-quit={(resume ? "yes" : "no")}",
            $"--resume-playback={(resume ? "yes" : "no")}",
            $"--watch-later-dir={Path.Combine(Settings.DataDir, "watch_later")}",
            "--osc=no", "--osd-bar=no",         // our own control bar replaces mpv's
            "--cursor-autohide=no",           // matches the bar's 2s auto-hide
            $"--keep-open={(s.Autoplay ? "no" : "yes")}", // autoplay next, or stop at the end
            // language defaults: anime and TV/movies each have their own; our picker then prefers full subtitles over signs-only
            $"--alang={Settings.Langs(s.LangFor(kind).Audio)}",
            $"--slang={Settings.Langs(s.LangFor(kind).Subs)}",
            $"--sid={(s.LangFor(kind).On ? "auto" : "no")}",
            $"--sub-scale={s.SubScale.ToString(inv)}",
            $"--sub-pos={s.SubPos.ToString(inv)}",
            "--title=Haku Player",               // name shown in the Windows volume mixer
            "--audio-client-name=Haku Player",
            $"--glsl-shaders={GlassShader}",     // real-time liquid glass under the player bars
            "--border-background=blur",          // letterbox bars show the video, blurred, instead of black
            "--background-blur-radius=48",
            // volume is app-wide; subtitle timing is ours, per video; the AI filter and its start-up pause are Haku's to set
            "--watch-later-options-remove=volume,mute,speed,sub-delay,vf,pause",
            $"--pause={(aiStart ? "yes" : "no")}",
            // read ahead (shown in blue on the timeline): a minute or two of a typical episode; seeks inside it are instant
            "--cache=yes", "--demuxer-max-bytes=256MiB", "--demuxer-max-back-bytes=64MiB",
            $"--volume={volume.ToString(inv)}",
            $"--playlist-start={index}",
            $"--playlist={playlist}",
        };
    }
}
