using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using Vortice.DXGI;

namespace AnimePlayer;

// Real-time AI upscaling: mpv's vf_hakusr filter (Haku's own libmpv build) runs AnimeJaNai models with TensorRT through
// haku_sr.dll, keeping every frame on the GPU, before subtitles and the glass shader are drawn (docs/realtime-upscaling-plan.md).
// Everything it needs lives in %LOCALAPPDATA%\Haku Upscaler: haku_sr.dll, the TensorRT runtime, the models, and a cache of
// TensorRT engines (one per model, built the first time it's used, about 20 s; each takes any size up to 1920x1088).
static class Upscaler
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Haku Upscaler");
    public static string Lib => Path.Combine(Dir, "haku_sr.dll");
    static string Models => Path.Combine(Dir, "models");
    static string Engines => Path.Combine(Dir, "engines");

    // Tiers, fastest first. fps: measured on an RTX 4070 through the whole pipeline, 1920x1080 -> 3840x2160 (FP16, size-range engine).
    public record Tier(string Key, string Name, string Size, double Fps4070);
    public static readonly Tier[] Tiers =
    {
        new("fast", "Fast", "SuperUltraCompact", 89.3),
        new("balanced", "Balanced", "UltraCompact", 31.6),
        new("quality", "Quality", "Compact", 17.2),
    };

    public static string ModelFile(Tier t, string look) =>
        Path.Combine(Models, $"2x_AnimeJaNai_HD_V3{(look == "soft" ? "" : "Sharp1")}_{t.Size}_fp16.onnx");

    // One engine per model: it takes any video up to MaxW x MaxH (haku_sr.h HSR_MAX_W/H).
    public const int MaxW = 1920, MaxH = 1088;
    public static string EngineFile(string model) => Path.Combine(Engines, $"{Path.GetFileNameWithoutExtension(model)}.engine");

    public static bool Installed => File.Exists(Lib);

    // ---------- the GPU ----------

    static string? gpu;
    static double speed = -1;

    // The main GPU's name, as mpv uses it (adapter 0).
    public static string Gpu
    {
        get
        {
            if (gpu != null) return gpu;
            try
            {
                using var f = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
                if (f.EnumAdapters1(0, out var a).Success)
                    using (a) gpu = a.Description1.VendorId == 0x10DE ? a.Description1.Description : "";
            }
            catch { }
            return gpu ??= "";
        }
    }

    // Rough speed relative to an RTX 4070 for this kind of work (tensor cores, FP16). 0: no TensorRT-capable NVIDIA RTX GPU.
    // shortcut: a name table, not a measurement; upgrade to a quick benchmark per GPU if guesses turn out wrong.
    public static double Speed
    {
        get
        {
            if (speed >= 0) return speed;
            var m = Regex.Match(Gpu, @"RTX\s*(?:A)?(\d{4})\s*(Ti\s*SUPER|Ti|SUPER)?", RegexOptions.IgnoreCase);
            if (!m.Success) return speed = Gpu.Contains("RTX", StringComparison.OrdinalIgnoreCase) ? 0.5 : 0;
            int n = int.Parse(m.Groups[1].Value);
            string v = m.Groups[2].Value.ToUpperInvariant().Replace(" ", "");
            var table = new Dictionary<int, double>
            {
                [2060] = 0.4, [2070] = 0.5, [2080] = 0.6,
                [3050] = 0.3, [3060] = 0.55, [3070] = 0.75, [3080] = 0.95, [3090] = 1.0,
                [4050] = 0.4, [4060] = 0.6, [4070] = 1.0, [4080] = 1.6, [4090] = 2.2,
                [5050] = 0.5, [5060] = 0.7, [5070] = 1.1, [5080] = 1.8, [5090] = 2.8,
            };
            double s = table.TryGetValue(n, out var t) ? t : n >= 6000 ? 3.0 : 0.5;
            if (v == "TI") s *= 1.15; else if (v == "SUPER") s *= 1.1; else if (v == "TISUPER") s *= 1.2;
            return speed = s;
        }
    }

    // The best tier that keeps up with this video (15% headroom), or null if none would.
    public static Tier? Auto(int w, int h, double fps) => AutoFor(w, h, fps, Speed);

    internal static Tier? AutoFor(int w, int h, double fps, double speed)
    {
        if (speed <= 0) return null;
        double pixels = (double)w * h / (1920.0 * 1080.0);
        double need = Math.Max(fps, 23.976) * 1.15;
        Tier? best = null;
        foreach (var t in Tiers)
            if (t.Fps4070 * speed / pixels >= need) best = t;
        return best;
    }

    // What to use for this video, or null with the reason (for the player button's tooltip).
    public static Tier? Choose(int w, int h, double fps, int screenHeight, out string why)
    {
        var s = Settings.Current;
        why = "";
        if (!s.Upscale) { why = "turned off"; return null; }
        if (!Installed) { why = "the Haku Upscaler pack isn't installed"; return null; }
        if (Speed <= 0) { why = "needs an NVIDIA RTX graphics card"; return null; }
        if (w <= 0 || h <= 0) { why = "waiting for the video"; return null; }
        if (screenHeight < h * 1.25) { why = "not needed: this video is already about your screen's size"; return null; }
        if (w > MaxW || h > MaxH) { why = $"not needed: {w}×{h} is close to your screen's size"; return null; }
        var t = s.UpscaleTier == "auto" ? Auto(w, h, fps) : Array.Find(Tiers, x => x.Key == s.UpscaleTier) ?? Auto(w, h, fps);
        if (t == null) why = "this video is too big or fast for your graphics card to upscale in real time";
        return t;
    }

    // Estimated speed of a tier on this PC for 1080p video (from the RTX 4070 measurements).
    public static double EstimateFps(Tier t) => t.Fps4070 * Speed;

    public static bool Prepared(Tier t, string look) => File.Exists(EngineFile(ModelFile(t, look)));

    // ---------- engines ----------

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int BuildFn([MarshalAs(UnmanagedType.LPWStr)] string onnx, [MarshalAs(UnmanagedType.LPWStr)] string engine, int w, int h);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr ErrFn();
    static BuildFn? build;
    static ErrFn? lastError;
    static readonly Dictionary<string, Task<string?>> building = new();

    // Build the engine for this model if it isn't cached yet (once per model, about 20 s). Returns null when ready, else why not.
    public static Task<string?> Prepare(string model)
    {
        string engine = EngineFile(model);
        if (File.Exists(engine)) return Task.FromResult<string?>(null);
        lock (building)
        {
            if (building.TryGetValue(engine, out var running)) return running;
            var t = Task.Run(() =>
            {
                try
                {
                    if (!File.Exists(model)) return $"model missing: {Path.GetFileName(model)}";
                    if (build == null)
                    {
                        IntPtr lib = NativeLibrary.Load(Lib);
                        build = Marshal.GetDelegateForFunctionPointer<BuildFn>(NativeLibrary.GetExport(lib, "hsr_build_engine"));
                        lastError = Marshal.GetDelegateForFunctionPointer<ErrFn>(NativeLibrary.GetExport(lib, "hsr_last_error"));
                    }
                    Directory.CreateDirectory(Engines);
                    return build(model, engine, 1920, 1080) == 0 ? null : Marshal.PtrToStringAnsi(lastError!()) ?? "engine build failed";
                }
                catch (Exception ex) { return ex.Message; }
                finally { lock (building) building.Remove(engine); }
            });
            building[engine] = t;
            return t;
        }
    }

    // ---------- mpv ----------

    public const string Label = "hakusr";

    // mpv's "%n%" quoting: n is the length in bytes, so paths with spaces, colons and backslashes pass through untouched.
    static string Q(string s) => $"%{Encoding.UTF8.GetByteCount(s)}%{s}";

    public static string Filter(string model) =>
        $"@{Label}:hakusr=lib={Q(Lib)}:engine={Q(EngineFile(model))}";
}
