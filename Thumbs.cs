using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AnimePlayer;

// Timeline thumbnails: one exact frame per second, decoded on the GPU and cached on disk.
// Decoding starts where you're looking: hover 1:21 and it decodes from 1:21 onwards; jump elsewhere
// and it restarts there; once ahead is done it fills in backwards, nearest gap first.
sealed class Thumbs : IDisposable
{
    const int H = 216;
    int W = 384, FrameBytes = 384 * H * 4;
    readonly Task probe;
    static readonly string CacheDir = Path.Combine(Settings.DataDir, "thumbs");

    readonly string path, cacheFile;
    byte[]?[] slots = Array.Empty<byte[]?>(); // slots[i] = JPEG of second i (null = not yet, empty = no frame there)
    readonly AutoResetEvent wake = new(false);
    volatile int want = -1;                   // second someone wants to see
    int wantSeq;
    DateTime lastHover = DateTime.MinValue;
    Process? proc;
    volatile bool disposed;

    public Thumbs(string path)
    {
        this.path = path;
        var fi = new FileInfo(path);
        var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"v2|{path}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}")))[..16];
        cacheFile = Path.Combine(CacheDir, key + ".bin");
        probe = Task.Run(Probe);
        Task.Run(Run);
    }

    // Display aspect ratio (4:3, 16:9, ...), so thumbnails aren't stretched.
    public double Aspect { get; private set; } = 16.0 / 9;

    void Probe()
    {
        double dur = 0;
        try
        {
            var psi = new ProcessStartInfo("ffprobe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            foreach (var a in new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height,sample_aspect_ratio:format=duration", "-of", "csv=p=0", path })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var lines = p.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            p.WaitForExit(3000);
            var parts = lines[0].Split(',');
            double w = double.Parse(parts[0], CultureInfo.InvariantCulture), h = double.Parse(parts[1], CultureInfo.InvariantCulture), sar = 1;
            if (parts.Length > 2 && parts[2].Split(':') is { Length: 2 } r && double.TryParse(r[0], out var n) && double.TryParse(r[1], out var d) && n > 0 && d > 0)
                sar = n / d;
            if (w > 0 && h > 0) Aspect = w * sar / h;
            if (lines.Length > 1) double.TryParse(lines[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out dur);
        }
        catch { }
        W = Math.Max(2, (int)Math.Round(H * Aspect / 2) * 2);
        FrameBytes = W * H * 4;
        // unknown length: assume up to 3 h; reaching the end of the file marks the rest as empty
        lock (wake) slots = new byte[]?[dur > 0 ? (int)Math.Ceiling(dur) + 1 : 3 * 3600];
    }

    // For the debug panel: how much of the video has preview frames.
    public string Progress
    {
        get
        {
            lock (wake)
            {
                if (slots.Length == 0) return "starting";
                int done = slots.Count(s => s != null);
                return done == slots.Length ? $"all {slots.Length} s ready" : $"{done} of {slots.Length} s decoded";
            }
        }
    }

    // Exact frame for second t, or null if not decoded yet.
    public BitmapSource? Get(double t)
    {
        int i = Math.Max(0, (int)Math.Round(t));
        byte[]? jpg;
        lock (wake) jpg = i < slots.Length ? slots[i] : null;
        return jpg is { Length: > 0 } ? Decode(jpg) : null;
    }

    // Decode around second t first. Hovering wins; playback hints only steer it when nobody's hovering.
    public void Request(double t, bool hover = true)
    {
        if (!hover && (DateTime.Now - lastHover).TotalSeconds < 3) return;
        if (hover) lastHover = DateTime.Now;
        want = Math.Max(0, (int)Math.Round(t));
        Interlocked.Increment(ref wantSeq);
        wake.Set();
    }

    void Run()
    {
        try
        {
            probe.Wait();
            if (File.Exists(cacheFile) && Load()) return;
            wake.WaitOne(4000); // let the video get going first, unless someone hovers the timeline
            bool gpu = true, everGot = false;
            while (!disposed)
            {
                int start = NextStart();
                if (start < 0) break;
                int cur = start, seq = wantSeq;
                bool stopped = false;
                int got = RunFfmpeg(gpu, start, raw =>
                {
                    lock (wake) if (cur >= slots.Length || slots[cur] != null) { stopped = true; return false; } // reached decoded part
                    var j = Encode(raw);
                    lock (wake) slots[cur] = j;
                    cur++;
                    if (wantSeq != seq)
                    {
                        seq = wantSeq;
                        int w = want;
                        bool missing;
                        lock (wake) missing = w < slots.Length && slots[w] == null;
                        // they're looking somewhere this run won't reach soon: restart there
                        if (missing && (w < cur || w > cur + 8)) { stopped = true; return false; }
                    }
                    return true;
                });
                if (disposed) break;
                if (got > 0) everGot = true;
                if (got == 0 && gpu && !everGot) { gpu = false; continue; } // no NVIDIA GPU: CPU decode
                if (!stopped) // file ended (or nothing decodable here): nothing more to get in this gap
                    lock (wake) for (int i = cur; i < slots.Length && slots[i] == null; i++) slots[i] = Array.Empty<byte>();
            }
            bool complete;
            lock (wake) complete = slots.All(s => s != null);
            if (!disposed && complete) Save();
        }
        catch { } // previews are a nice-to-have; never crash playback
    }

    // Where to decode next: the wanted second, else the nearest gap. Gaps ahead are decoded forwards;
    // gaps behind in 60 s blocks ending at the gap, so filling works outwards from where you looked.
    int NextStart()
    {
        lock (wake)
        {
            int n = slots.Length, t = Math.Clamp(want, 0, Math.Max(0, n - 1));
            if (n == 0) return -1;
            if (slots[t] == null) return t;
            int f = -1, b = -1;
            for (int i = t + 1; i < n; i++) if (slots[i] == null) { f = i; break; }
            for (int i = t - 1; i >= 0; i--) if (slots[i] == null) { b = i; break; }
            if (f < 0 && b < 0) return -1;
            if (b < 0 || (f >= 0 && f - t <= t - b)) return f;
            int s = b;
            while (s > 0 && slots[s - 1] == null && b - s < 59) s--;
            return s;
        }
    }

    // Streams raw BGRA frames at 1 fps from `start` seconds; onFrame returns false to stop. Returns frames read.
    int RunFfmpeg(bool gpu, int start, Func<byte[], bool> onFrame)
    {
        var psi = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        // skip frames nothing else depends on (~45% less decoding; previews stay within a frame or two)
        var args = new List<string> { "-v", "quiet", "-nostdin", "-skip_frame:v", "noref" };
        if (gpu) args.AddRange(new[] { "-hwaccel", "cuda", "-hwaccel_output_format", "cuda" });
        if (start > 0) args.AddRange(new[] { "-ss", start.ToString(CultureInfo.InvariantCulture) }); // exact seek: first frame = start s
        args.AddRange(new[] { "-i", path, "-an", "-sn", "-vf",
            gpu ? $"fps=1,scale_cuda={W}:{H}:format=yuv420p,hwdownload,format=yuv420p" : $"fps=1,scale={W}:{H}" });
        args.AddRange(new[] { "-f", "rawvideo", "-pix_fmt", "bgra", "-" });
        foreach (var a in args) psi.ArgumentList.Add(a);

        Process p;
        try { p = Process.Start(psi)!; } catch { return 0; }
        proc = p;
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        int n = 0;
        using (p)
        {
            var s = p.StandardOutput.BaseStream;
            var buf = new byte[FrameBytes];
            while (!disposed)
            {
                int read = 0, r;
                while (read < FrameBytes && (r = s.Read(buf, read, FrameBytes - read)) > 0) read += r;
                if (read < FrameBytes) break;
                n++;
                if (!onFrame(buf)) break;
            }
            try { if (!p.HasExited) p.Kill(); } catch { }
        }
        return n;
    }

    byte[] Encode(byte[] raw)
    {
        var bmp = BitmapSource.Create(W, H, 96, 96, PixelFormats.Bgra32, null, raw, W * 4);
        var enc = new JpegBitmapEncoder { QualityLevel = 85 };
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    static BitmapSource Decode(byte[] jpg)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.StreamSource = new MemoryStream(jpg);
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    // Cache format: [int count] then [int length][jpeg bytes] per second (length 0 = no frame).
    bool Load()
    {
        try
        {
            using var r = new BinaryReader(File.OpenRead(cacheFile));
            int n = r.ReadInt32();
            var list = new byte[]?[n];
            for (int i = 0; i < n; i++) list[i] = r.ReadBytes(r.ReadInt32());
            lock (wake) slots = list;
            return n > 0;
        }
        catch { return false; }
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var tmp = cacheFile + ".tmp";
            using (var w = new BinaryWriter(File.Create(tmp)))
            {
                lock (wake)
                {
                    // trailing "no frame" seconds (past the real end) aren't worth keeping
                    int n = slots.Length;
                    while (n > 0 && slots[n - 1] is { Length: 0 }) n--;
                    w.Write(n);
                    for (int i = 0; i < n; i++) { w.Write(slots[i]!.Length); w.Write(slots[i]!); }
                }
            }
            File.Move(tmp, cacheFile, true);
        }
        catch { }
    }

    public void Dispose()
    {
        disposed = true;
        wake.Set();
        try { if (proc is { HasExited: false }) proc.Kill(); } catch { }
    }
}
