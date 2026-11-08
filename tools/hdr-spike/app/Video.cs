using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace HdrSpike;

// libmpv drawing straight into a swap chain shown by a WinUI SwapChainPanel, on a render thread.
// Mode picks the swap chain: sdr = BGRA8 sRGB (what Haku shows today), pq = 10-bit PQ / BT.2020, fp16 = scRGB linear.
sealed unsafe class Video : IDisposable
{
    readonly SwapChainPanel panel;
    readonly TextBlock stats;
    readonly string mode = Environment.GetEnvironmentVariable("SPIKE_MODE") ?? "sdr";
    readonly string? dumpFile = Environment.GetEnvironmentVariable("SPIKE_DUMP");
    readonly string? captureFile = Environment.GetEnvironmentVariable("SPIKE_CAPTURE");
    IntPtr hwnd;
    readonly Format format;
    readonly ColorSpaceType colorSpace;
    readonly ID3D11Device dev;
    readonly ID3D11DeviceContext ctx;
    readonly IDXGISwapChain1 sc;
    readonly IntPtr mpv, render;
    readonly RenderUpdate onUpdate;
    readonly AutoResetEvent wake = new(false);
    readonly Thread thread;
    readonly Stopwatch clock = Stopwatch.StartNew();
    volatile bool stop;
    int bufW = 16, bufH = 16;
    // the size the panel wants, set on the UI thread, applied on the render thread
    readonly object sizeLock = new();
    int wantW = 16, wantH = 16; float scaleX = 1, scaleY = 1; long wantAt; bool sizeDirty;
    long firstFrameAt = -1;
    bool dumpPending;
    int frames, framesThisSecond; long secondStart;
    readonly List<(int w, long requested, long layout, long presented)> resizes = new();
    long markRequested; int markW;

    static void Log(string s) => App.Log(s);

    public Video(SwapChainPanel panel, IntPtr hwnd, TextBlock stats)
    {
        this.panel = panel; this.stats = stats; this.hwnd = hwnd;
        dumpPending = dumpFile != null;
        (format, colorSpace) = mode switch
        {
            "pq" => (Format.R10G10B10A2_UNorm, ColorSpaceType.RgbFullG2084NoneP2020),
            "fp16" => (Format.R16G16B16A16_Float, ColorSpaceType.RgbFullG10NoneP709),
            _ => (Format.B8G8R8A8_UNorm, ColorSpaceType.RgbFullG22NoneP709),
        };

        // same device setup as Haku's MpvVideo: D3D11, video support, multithread-protected (mpv's decoder shares it)
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 }, out ID3D11Device? d, out ID3D11DeviceContext? c).CheckError();
        dev = d!; ctx = c!;
        using (var mt = dev.QueryInterface<ID3D11Multithread>()) mt.SetMultithreadProtected(true);

        var display = DescribeDisplay(hwnd);
        Log("display: " + display.text);

        using var dxgiDev = dev.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDev.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();
        var desc = new SwapChainDescription1
        {
            Width = (uint)bufW, Height = (uint)bufH, Format = format, SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput, BufferCount = 2, Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential, AlphaMode = AlphaMode.Ignore,
        };
        sc = factory.CreateSwapChainForComposition(dev, desc, null);
        using (var sc3 = sc.QueryInterface<IDXGISwapChain3>())
        {
            var support = sc3.CheckColorSpaceSupport(colorSpace);
            Log($"swap chain {format}, colour space {colorSpace}: support {support}");
            sc3.SetColorSpace1(colorSpace);
        }

        // hand the swap chain to the panel (ISwapChainPanelNative::SetSwapChain, vtable slot 3)
        var unk = ((WinRT.IWinRTObject)panel).NativeObject.ThisPtr;
        var iid = new Guid("63aad0b8-7c24-40ff-85a8-640d944cc325");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unk, ref iid, out var native));
        try
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)(*(IntPtr**)native)[3];
            Marshal.ThrowExceptionForHR(fn(native, sc.NativePointer));
        }
        finally { Marshal.Release(native); }

        // mpv, set up like Haku (vo=libmpv, the user's mpv.conf, high-quality), plus the output colour for this mode
        mpv = mpv_create();
        Set("vo", "libmpv"); Set("config", "yes"); Set("terminal", "no"); Set("profile", "high-quality");
        Set("hwdec", Environment.GetEnvironmentVariable("SPIKE_HWDEC") ?? "auto-safe");
        Set("keep-open", "yes"); Set("loop-file", "inf"); Set("osd-level", "0");
        if (App.LogFile != null) { Set("log-file", App.LogFile + ".mpv.log"); Set("msg-level", "all=v"); }
        double peak = display.peak > 0 ? display.peak : 1000;
        if (mode == "pq") { Set("target-trc", "pq"); Set("target-prim", "bt.2020"); Set("target-peak", peak.ToString("0", CultureInfo.InvariantCulture)); }
        if (mode == "fp16") { Set("target-trc", "linear"); Set("target-prim", "bt.709"); Set("target-peak", peak.ToString("0", CultureInfo.InvariantCulture)); }
        if (Environment.GetEnvironmentVariable("SPIKE_GLASS") == "1")
        {
            Set("glsl-shaders", Environment.GetEnvironmentVariable("SPIKE_GLASSFILE") ?? Path.Combine(AppContext.BaseDirectory, "glass.glsl"));
            Set("glsl-shader-opts", GlassOpts(0.3, 0.3));
        }
        if (Environment.GetEnvironmentVariable("SPIKE_START") is { Length: > 0 } st) Set("start", st);
        if (dumpFile != null || captureFile != null) Set("pause", "yes");
        foreach (var kv in (Environment.GetEnvironmentVariable("SPIKE_OPTS") ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = kv.IndexOf('=');
            Set(kv[..eq], kv[(eq + 1)..]);
        }
        if (Environment.GetEnvironmentVariable("SPIKE_VF") is { Length: > 0 } vf) Set("vf", vf);
        Check(mpv_initialize(mpv), "initialize");

        var apiType = Marshal.StringToHGlobalAnsi("d3d11");
        var init = Marshal.AllocHGlobal(IntPtr.Size);
        Marshal.WriteIntPtr(init, dev.NativePointer);
        var ps = new[] { new RenderParam(1, apiType), new RenderParam(21, init), new RenderParam(0, IntPtr.Zero) };
        try { Check(mpv_render_context_create(out render, mpv, ps), "render context"); }
        finally { Marshal.FreeHGlobal(apiType); Marshal.FreeHGlobal(init); }
        onUpdate = _ => wake.Set();
        mpv_render_context_set_update_callback(render, onUpdate, IntPtr.Zero);

        if (Environment.GetEnvironmentVariable("SPIKE_FILE") is { Length: > 0 } file) Command("loadfile", file);
        Resize();
        if (Environment.GetEnvironmentVariable("SPIKE_GLASSANIM") == "1")
            new Thread(() =>
            {
                // moves the glass rectangle 30 times a second, like the bars sliding in, through mpv's property API
                var sw = Stopwatch.StartNew(); int n = 0;
                while (!stop)
                {
                    double x = 0.1 + 0.4 * (0.5 + 0.5 * Math.Sin(sw.Elapsed.TotalSeconds * 2));
                    mpv_set_property_string(mpv, "glsl-shader-opts", GlassOpts(x, 0.3)); n++;
                    Thread.Sleep(33);
                }
                Log($"glass moved {n} times");
            }) { IsBackground = true }.Start();
        if (captureFile != null) new Thread(Capture) { IsBackground = true }.Start();
        thread = new Thread(Loop) { IsBackground = true, Name = "render" };
        thread.Start();
        Log($"video ready (mode {mode})");
    }

    void Set(string name, string value)
    {
        int r = mpv_set_option_string(mpv, name, value);
        if (r < 0) Log($"mpv option {name}={value}: {Marshal.PtrToStringAnsi(mpv_error_string(r))}");
    }

    void Command(params string[] args)
    {
        var ptrs = args.Select(Marshal.StringToHGlobalAnsi).Append(IntPtr.Zero).ToArray();
        try { mpv_command(mpv, ptrs); } finally { foreach (var p in ptrs) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p); }
    }

    static void Check(int r, string what) { if (r < 0) throw new InvalidOperationException($"mpv {what}: {Marshal.PtrToStringAnsi(mpv_error_string(r))}"); }

    // UI thread: the panel's size in pixels (layout size × the panel's composition scale, i.e. DPI)
    public void Resize()
    {
        int w = Math.Max(1, (int)Math.Round(panel.ActualWidth * panel.CompositionScaleX));
        int h = Math.Max(1, (int)Math.Round(panel.ActualHeight * panel.CompositionScaleY));
        lock (sizeLock)
        {
            if (w == wantW && h == wantH && scaleX == panel.CompositionScaleX) return;
            wantW = w; wantH = h; scaleX = panel.CompositionScaleX; scaleY = panel.CompositionScaleY;
            wantAt = clock.ElapsedTicks; sizeDirty = true;
        }
        if (syncResize) DrawNowForResize(); else wake.Set();
    }

    public void MarkResize(int windowWidth) { markRequested = clock.ElapsedTicks; markW = windowWidth; }

    // Drawing state, shared by the render thread and (with SPIKE_SYNCRESIZE=1) the UI thread's resize draw.
    readonly object drawLock = new();
    readonly IntPtr fboStruct = Marshal.AllocHGlobal(IntPtr.Size);
    readonly IntPtr noWait = Marshal.AllocHGlobal(4);
    RenderParam[]? drawParams;
    readonly bool syncResize = Environment.GetEnvironmentVariable("SPIKE_SYNCRESIZE") == "1";
    double renderMsSum, presentMsSum; int timedFrames;

    void Loop()
    {
        Marshal.WriteInt32(noWait, 0);
        drawParams = new[] { new RenderParam(22, fboStruct), new RenderParam(12, noWait), new RenderParam(0, IntPtr.Zero) };
        try
        {
            while (!stop)
            {
                wake.WaitOne(100);
                if (stop) break;
                lock (drawLock) DrawOnce(false);
                if (dumpFile != null && !dumpPending) { panel.DispatcherQueue.TryEnqueue(() => (App.Current as App)?.Exit()); break; }
            }
        }
        catch (Exception e) { Log("RENDER THREAD: " + e); }
    }

    // UI thread, SPIKE_SYNCRESIZE=1: draw at the new size right now, inside the layout pass (what Haku's D3DImage path does).
    void DrawNowForResize()
    {
        if (drawParams == null) return;
        try { lock (drawLock) DrawOnce(true); } catch (Exception e) { Log("SYNC RESIZE: " + e); }
    }

    // Haku's packed glass params (glass.glsl): two numbers per float
    static string Pack(double u, double v) => (Math.Round((Math.Clamp(u, -1, 2) + 1) / 3 * 4095) * 4096 + Math.Round((Math.Clamp(v, -1, 2) + 1) / 3 * 4095)).ToString("0", CultureInfo.InvariantCulture);
    string GlassOpts(double x, double y) => $"a_p={Pack(x, y)},a_s={Pack(0.4, 0.25)},a_r={Pack(0.05, 1)},gblur=16,gpq={(mode == "pq" ? 1 : 0)}";
    double allRenderMs, maxRenderMs; int allFrames;

    void DrawOnce(bool force)
    {
        bool resized = false; long layoutAt = 0;
        lock (sizeLock)
        {
            if (sizeDirty)
            {
                sizeDirty = false; layoutAt = wantAt;
                if (wantW != bufW || wantH != bufH)
                {
                    sc.ResizeBuffers(2, (uint)wantW, (uint)wantH, format, SwapChainFlags.None).CheckError();
                    bufW = wantW; bufH = wantH; resized = true;
                }
                using var sc2 = sc.QueryInterface<IDXGISwapChain2>();
                sc2.MatrixTransform = Matrix3x2.CreateScale(1 / scaleX, 1 / scaleY); // the panel scales by DPI; undo it so 1 buffer px = 1 screen px
            }
        }
        ulong upd = mpv_render_context_update(render);
        bool dumpNow = dumpPending && firstFrameAt >= 0 && Ms(clock.ElapsedTicks - firstFrameAt) > 1500;
        if ((upd & 1) == 0 && !resized && !dumpNow && !force) return;

        long t0 = clock.ElapsedTicks;
        using (var bb = sc.GetBuffer<ID3D11Texture2D>(0))
        {
            Marshal.WriteIntPtr(fboStruct, bb.NativePointer);
            int r = mpv_render_context_render(render, drawParams!);
            if (r < 0) { Log("render failed: " + Marshal.PtrToStringAnsi(mpv_error_string(r))); return; }
            if (dumpNow) { Dump(bb); dumpPending = false; }
        }
        long t1 = clock.ElapsedTicks;
        { var ms = Ms(t1 - t0); if (firstFrameAt >= 0 && Ms(t0 - firstFrameAt) > 2000) { allRenderMs += ms; allFrames++; maxRenderMs = Math.Max(maxRenderMs, ms); } }
        sc.Present(1, PresentFlags.None);
        mpv_render_context_report_swap(render);
        long now = clock.ElapsedTicks;
        if (resized) { renderMsSum += Ms(t1 - t0); presentMsSum += Ms(now - t1); timedFrames++; }
        if (resized) lock (resizes) resizes.Add((markW, markRequested, layoutAt, now));
        if (firstFrameAt < 0) { firstFrameAt = now; Log($"first frame presented ({bufW}x{bufH})"); }
        frames++; framesThisSecond++;
        if (Ms(now - secondStart) >= 1000)
        {
            int fps = framesThisSecond; framesThisSecond = 0; secondStart = now;
            var t = mpv_get_property_string(mpv, "time-pos"); var tp = t != IntPtr.Zero ? Marshal.PtrToStringAnsi(t) : "?"; if (t != IntPtr.Zero) mpv_free(t);
            var hw = mpv_get_property_string(mpv, "hwdec-current"); var hws = hw != IntPtr.Zero ? Marshal.PtrToStringAnsi(hw) : "?"; if (hw != IntPtr.Zero) mpv_free(hw);
            panel.DispatcherQueue.TryEnqueue(() => stats.Text = $"{mode}  {format}  {bufW}x{bufH}  {fps} fps  t={tp}  hwdec={hws}");
        }
    }
    static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    public void ReportResizes()
    {
        lock (resizes)
        {
            foreach (var r in resizes)
                Log($"resize to window width {r.w}: layout {Ms(r.layout - r.requested):0.0} ms after the request, new-size frame presented {Ms(r.presented - r.layout):0.0} ms after layout");
            var lags = resizes.Where(r => r.requested > 0).Select(r => Ms(r.presented - r.layout)).OrderBy(x => x).ToList();
            if (timedFrames > 0) Log($"resize frames: mpv render {renderMsSum / timedFrames:0.0} ms, Present {presentMsSum / timedFrames:0.0} ms on average (sync={syncResize})");
            if (lags.Count > 0) Log($"resize lag layout->present: median {lags[lags.Count / 2]:0.0} ms, max {lags[^1]:0.0} ms over {lags.Count} resizes");
        }
    }

    // The presented frame as raw pixels: one JSON header line, then the rows.
    void Dump(ID3D11Texture2D bb)
    {
        var d = bb.Description;
        d.Usage = ResourceUsage.Staging; d.BindFlags = BindFlags.None; d.CPUAccessFlags = CpuAccessFlags.Read; d.MiscFlags = ResourceOptionFlags.None;
        using var staging = dev.CreateTexture2D(d);
        ctx.CopyResource(staging, bb);
        var m = ctx.Map(staging, 0, MapMode.Read);
        try
        {
            int bpp = format == Format.R16G16B16A16_Float ? 8 : 4;
            using var f = File.Create(dumpFile!);
            var header = System.Text.Encoding.ASCII.GetBytes($"{{\"mode\":\"{mode}\",\"format\":\"{format}\",\"w\":{d.Width},\"h\":{d.Height},\"bpp\":{bpp}}}\n");
            f.Write(header);
            for (int y = 0; y < d.Height; y++)
                f.Write(new ReadOnlySpan<byte>((byte*)m.DataPointer + y * m.RowPitch, (int)d.Width * bpp));
        }
        finally { ctx.Unmap(staging, 0); }
        Log($"dumped {d.Width}x{d.Height} {format} to {dumpFile}");
    }

    public void Dispose()
    {
        stop = true; wake.Set(); thread.Join(2000);
        if (allFrames > 0) Log($"render: {allFrames} frames after the first 2 s, average {allRenderMs / allFrames:0.00} ms, max {maxRenderMs:0.0} ms");
        mpv_render_context_free(render);
        mpv_terminate_destroy(mpv);
        sc.Dispose(); ctx.Dispose(); dev.Dispose();
    }

    // What Windows actually shows: the window's client area, captured from the composed desktop in FP16 scRGB
    // (Desktop Duplication), 2.5 s after the first frame. Written like Dump (mode "fp16"), then the app quits.
    void Capture()
    {
        try
        {
            while (firstFrameAt < 0 && !stop) Thread.Sleep(50);
            Thread.Sleep(2500);
            var mon = MonitorFromWindow(hwnd, 2);
            using var f = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            IDXGIOutput5? o5 = null; Vortice.RawRect desk = default;
            for (uint a = 0; o5 == null && f.EnumAdapters1(a, out var ad).Success; a++)
            {
                for (uint o = 0; o5 == null && ad.EnumOutputs(o, out var op).Success; o++)
                {
                    var o6 = op.QueryInterface<IDXGIOutput6>();
                    if (o6.Description1.Monitor == mon) { o5 = op.QueryInterface<IDXGIOutput5>(); desk = o6.Description1.DesktopCoordinates; }
                    o6.Dispose(); op.Dispose();
                }
                ad.Dispose();
            }
            if (o5 == null) { Log("capture: no output for the window"); return; }
            // the duplication must be made on the output's own adapter: our device is on the default adapter (the same GPU here)
            using var dup = o5.DuplicateOutput1(dev, 0, new[] { Format.R16G16B16A16_Float });
            o5.Dispose();
            GetClientRect(hwnd, out var cr); var tl = new POINT(); ClientToScreen(hwnd, ref tl);
            int x0 = tl.X - desk.Left, y0 = tl.Y - desk.Top, w = cr.Right, h = cr.Bottom;
            for (int tries = 0; tries < 40; tries++)
            {
                var res = dup.AcquireNextFrame(500, out var info, out var resource);
                if (res.Failure) continue;
                try
                {
                    if (info.LastPresentTime == 0 && tries < 39) continue; // no new desktop image in this one
                    using var tex = resource!.QueryInterface<ID3D11Texture2D>();
                    var d = tex.Description;
                    var sd = new Texture2DDescription { Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = d.Format, SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read };
                    using var staging = dev.CreateTexture2D(sd);
                    lock (drawLock) ctx.CopySubresourceRegion(staging, 0, 0, 0, 0, tex, 0, new Vortice.Mathematics.Box(x0, y0, 0, x0 + w, y0 + h, 1));
                    var m = ctx.Map(staging, 0, MapMode.Read);
                    try
                    {
                        using var fs = File.Create(captureFile!);
                        fs.Write(System.Text.Encoding.ASCII.GetBytes($"{{\"mode\":\"fp16\",\"format\":\"{d.Format}\",\"w\":{w},\"h\":{h},\"bpp\":8,\"captured\":true}}\n"));
                        for (int y = 0; y < h; y++) fs.Write(new ReadOnlySpan<byte>((byte*)m.DataPointer + y * m.RowPitch, w * 8));
                    }
                    finally { ctx.Unmap(staging, 0); }
                    Log($"captured the screen under the window: {w}x{h} at {x0},{y0} of a {d.Width}x{d.Height} {d.Format} desktop");
                    break;
                }
                finally { resource?.Dispose(); dup.ReleaseFrame(); }
            }
        }
        catch (Exception e) { Log("CAPTURE FAILED: " + e); }
        finally { panel.DispatcherQueue.TryEnqueue(() => (App.Current as App)?.Exit()); }
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECTI { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECTI r);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);

    // ---- the display the window is on: HDR state, peak brightness and Windows' SDR white level ----
    static (string text, double peak) DescribeDisplay(IntPtr hwnd)
    {
        var mon = MonitorFromWindow(hwnd, 2);
        using var f = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        var all = new List<string>();
        (string text, double peak) mine = ("not found", 0);
        for (uint a = 0; f.EnumAdapters1(a, out var ad).Success; a++)
        {
            for (uint o = 0; ad.EnumOutputs(o, out var op).Success; o++)
            {
                using var o6 = op.QueryInterface<IDXGIOutput6>();
                var d = o6.Description1;
                var r = d.DesktopCoordinates;
                double white = SdrWhiteNits(d.DeviceName);
                var s = $"{d.DeviceName} {r.Right - r.Left}x{r.Bottom - r.Top} at {r.Left},{r.Top}: {d.ColorSpace}, {d.BitsPerColor}-bit, peak {d.MaxLuminance:0} nits, full-frame {d.MaxFullFrameLuminance:0}, SDR white {white:0} nits";
                all.Add(s);
                if (d.Monitor == mon) mine = ("[window here] " + s, d.MaxLuminance);
                op.Dispose();
            }
            ad.Dispose();
        }
        return (mine.text + " | all: " + string.Join(" ; ", all), mine.peak);
    }

    static double SdrWhiteNits(string gdiName)
    {
        if (GetDisplayConfigBufferSizes(2, out var np, out var nm) != 0) return -1;
        var paths = new byte[np * 72]; var modes = new byte[nm * 64];
        fixed (byte* pp = paths, pm = modes)
        {
            if (QueryDisplayConfig(2, ref np, pp, ref nm, pm, IntPtr.Zero) != 0) return -1;
            for (int i = 0; i < np; i++)
            {
                byte* p = pp + i * 72;
                var name = stackalloc byte[84];
                new Span<byte>(name, 84).Clear();
                *(int*)name = 1; *(uint*)(name + 4) = 84; *(long*)(name + 8) = *(long*)p; *(uint*)(name + 16) = *(uint*)(p + 8);
                if (DisplayConfigGetDeviceInfo(name) != 0) continue;
                var src = new string((char*)(name + 20));
                if (!string.Equals(src, gdiName, StringComparison.OrdinalIgnoreCase)) continue;
                var white = stackalloc byte[24];
                *(int*)white = 11; *(uint*)(white + 4) = 24; *(long*)(white + 8) = *(long*)(p + 20); *(uint*)(white + 16) = *(uint*)(p + 28);
                if (DisplayConfigGetDeviceInfo(white) != 0) return -1;
                return *(uint*)(white + 20) / 1000.0 * 80;
            }
        }
        return -1;
    }

    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out int numPaths, out int numModes);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref int numPaths, byte* paths, ref int numModes, byte* modes, IntPtr topology);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(byte* packet);

    // ---- libmpv ----
    const string Lib = "libmpv-2.dll";
    [StructLayout(LayoutKind.Sequential)] readonly struct RenderParam(int type, IntPtr data) { public readonly int Type = type; public readonly IntPtr Data = data; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void RenderUpdate(IntPtr data);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr mpv_create();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern int mpv_initialize(IntPtr h);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern int mpv_set_option_string(IntPtr h, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern int mpv_command(IntPtr h, IntPtr[] args);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern int mpv_set_property_string(IntPtr h, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr mpv_get_property_string(IntPtr h, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern void mpv_free(IntPtr p);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr mpv_error_string(int e);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern void mpv_terminate_destroy(IntPtr h);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern int mpv_render_context_create(out IntPtr ctx, IntPtr h, RenderParam[] ps);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern void mpv_render_context_set_update_callback(IntPtr ctx, RenderUpdate cb, IntPtr data);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern ulong mpv_render_context_update(IntPtr ctx);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern int mpv_render_context_render(IntPtr ctx, RenderParam[] ps);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern void mpv_render_context_report_swap(IntPtr ctx);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern void mpv_render_context_free(IntPtr ctx);
}
