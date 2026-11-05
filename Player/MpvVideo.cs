using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Direct3D9;
using Vortice.DXGI;

namespace AnimePlayer;

// mpv running inside the app (libmpv) and drawing the video into our own window, as an ordinary WPF image.
// Before, mpv.exe drew into a window of its own inside ours, with the bars in a third window on top: three pictures that
// each redrew on their own after a resize, so resizing never looked right. Now there's one picture: mpv draws each frame
// into a Direct3D 11 texture (libmpv's D3D11 render API), shared with the Direct3D 9 surface WPF shows in a D3DImage,
// on the UI thread, in the same frame as the layout. A resize draws the current frame at the new size right away.
// mpv is still driven over its JSON pipe (input-ipc-server), so the rest of the app talks to it the way it always did.
sealed class MpvVideo
{
    public readonly D3DImage Image = new();
    public event Action? Exited;           // mpv shut down (quit, or the playlist ended)
    public Task Stopped => stopped.Task;
    readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Dispatcher ui;

    IntPtr mpv, render;
    ID3D11Device? dev;
    ID3D11DeviceContext? ctx;
    ID3D11Query? doneQuery;
    IDirect3D9Ex? d9;
    IDirect3DDevice9Ex? dev9;
    ID3D11Texture2D? tex;
    IDirect3DTexture9? tex9;
    IDirect3DSurface9? surf;
    int texW, texH;     // the texture's size now
    int wantW, wantH;   // the size it should be (the video area, in pixels)
    int posted;         // a draw is already queued on the UI thread
    bool dead;
    readonly RenderUpdate onUpdate; // kept alive: mpv holds a pointer to it

    public MpvVideo(IEnumerable<string> args)
    {
        ui = Dispatcher.CurrentDispatcher;
        onUpdate = _ => QueueDraw();
        mpv = mpv_create();
        if (mpv == IntPtr.Zero) throw new InvalidOperationException("libmpv couldn't start");
        Set("vo", "libmpv");
        Set("config", "yes");                 // the user's mpv.conf, as mpv.exe would load it
        Set("input-default-bindings", "yes"); // keys we forward ("keypress") use mpv's own bindings
        Set("terminal", "no");
        if (Trace != null) { Set("log-file", Trace + ".mpv.log"); Set("msg-level", "all=v"); }
        string? playlist = null;
        foreach (var a in args)
        {
            var s = a.StartsWith("--") ? a[2..] : a;
            int eq = s.IndexOf('=');
            string name = eq < 0 ? s : s[..eq], value = eq < 0 ? "yes" : s[(eq + 1)..];
            if (name == "playlist") { playlist = value; continue; } // a command-line-only option: loaded below
            Set(name, value);
        }
        Check(mpv_initialize(mpv), "initialize");

        // Direct3D 11 for mpv (also its hardware decoder: a multithreaded device), Direct3D 9Ex for WPF
        var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, flags, new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 }, out dev, out ctx).CheckError();
        using (var mt = dev!.QueryInterface<ID3D11Multithread>()) mt.SetMultithreadProtected(true);
        doneQuery = dev.CreateQuery(new QueryDescription(Vortice.Direct3D11.QueryType.Event));
        d9 = D3D9.Direct3DCreate9Ex();
        var pp = new Vortice.Direct3D9.PresentParameters { Windowed = true, SwapEffect = Vortice.Direct3D9.SwapEffect.Discard, DeviceWindowHandle = GetDesktopWindow(), PresentationInterval = PresentInterval.Default };
        dev9 = d9.CreateDeviceEx(0, DeviceType.Hardware, IntPtr.Zero, CreateFlags.HardwareVertexProcessing | CreateFlags.Multithreaded | CreateFlags.FpuPreserve, pp);
        // a game in front keeps the GPU busy and Windows serves it first: our few small draws a frame then wait behind it
        // and frames drop (the pop-out over World of Warcraft). Asking for a higher GPU priority needs no admin rights.
        using (var dxgi = dev.QueryInterface<Vortice.DXGI.IDXGIDevice>())
        {
            var r11 = dxgi.SetGPUThreadPriority(7);
            bool ok9 = true; try { dev9.SetGPUThreadPriority(7); } catch { ok9 = false; }
            System.Diagnostics.Debug.Assert(r11.Success && ok9, $"GPU priority refused: {r11} / {ok9}"); // a refusal just plays as before
        }

        // the render context: mpv draws with our device
        var apiType = Marshal.StringToHGlobalAnsi("d3d11");
        var init = Marshal.AllocHGlobal(IntPtr.Size);
        Marshal.WriteIntPtr(init, dev.NativePointer);
        var ps = new[] { new RenderParam(1 /* API_TYPE */, apiType), new RenderParam(21 /* D3D11_INIT_PARAMS */, init), new RenderParam(0, IntPtr.Zero) };
        try { Check(mpv_render_context_create(out render, mpv, ps), "render context"); }
        finally { Marshal.FreeHGlobal(apiType); Marshal.FreeHGlobal(init); }
        mpv_render_context_set_update_callback(render, onUpdate, IntPtr.Zero);

        Image.IsFrontBufferAvailableChanged += (_, _) => { if (Image.IsFrontBufferAvailable) { texW = texH = 0; QueueDraw(); } };
        new Thread(EventLoop) { IsBackground = true, Name = "mpv events" }.Start();
        if (playlist != null) Command("loadlist", playlist);
    }

    // The video area's size in pixels: the next frame is drawn at that size (now, if it changed).
    public void SetSize(int w, int h)
    {
        w = Math.Max(1, w); h = Math.Max(1, h);
        if (w == wantW && h == wantH) return;
        wantW = w; wantH = h;
        Draw(); // in this very layout pass, so the picture and the layout change together
    }

    void QueueDraw()
    {
        if (Interlocked.Exchange(ref posted, 1) == 1) return;
        ui.BeginInvoke(DispatcherPriority.Render, () => { posted = 0; Draw(); });
    }

    void Draw()
    {
        if (dead || render == IntPtr.Zero || wantW <= 0 || !Image.IsFrontBufferAvailable) return;
        bool sized = texW != wantW || texH != wantH;
        Step("update");
        ulong upd = mpv_render_context_update(render);
        if (!sized && (upd & 1 /* MPV_RENDER_UPDATE_FRAME */) == 0) { Step(null); return; }
        Step("D3DImage.Lock");
        Image.Lock();
        try
        {
            if (sized) MakeTexture();
            var fbo = Marshal.AllocHGlobal(IntPtr.Size);
            var noWait = Marshal.AllocHGlobal(4);
            Marshal.WriteIntPtr(fbo, tex!.NativePointer);
            Marshal.WriteInt32(noWait, 0);
            try
            {
                // don't wait here for the frame's display time: this is the UI thread (frames come when mpv asks anyway)
                var ps = new[] { new RenderParam(22 /* D3D11_FBO */, fbo), new RenderParam(12 /* BLOCK_FOR_TARGET_TIME */, noWait), new RenderParam(0, IntPtr.Zero) };
                Step("mpv render");
                mpv_render_context_render(render, ps);
            }
            finally { Marshal.FreeHGlobal(fbo); Marshal.FreeHGlobal(noWait); }
            // WPF reads the texture through Direct3D 9: the frame must be finished first
            Step("wait for GPU");
            ctx!.End(doneQuery!);
            ctx.Flush();
            while (!ctx.IsDataAvailable(doneQuery!)) Thread.SpinWait(50);
            Image.AddDirtyRect(new Int32Rect(0, 0, texW, texH));
        }
        finally { Image.Unlock(); }
        Step("report swap");
        mpv_render_context_report_swap(render);
        Step(null);
    }

    // HAKU_TRACE=<file>: mpv writes a verbose log next to it, and a watchdog notes where the UI thread is stuck in Draw
    // (for hunting freezes; off normally).
    static readonly string? Trace = Environment.GetEnvironmentVariable("HAKU_TRACE") is { Length: > 0 } t ? t : null;
    static volatile string? step;
    static long stepAt;
    static Thread? watchdog;
    static void Step(string? s)
    {
        if (Trace == null) return;
        stepAt = Environment.TickCount64;
        step = s;
        if (watchdog != null) return;
        watchdog = new Thread(() =>
        {
            string? reported = null;
            while (true)
            {
                Thread.Sleep(500);
                var s2 = step; long ms = Environment.TickCount64 - stepAt;
                if (s2 != null && ms > 3000 && reported != s2 + stepAt)
                {
                    reported = s2 + stepAt;
                    System.IO.File.AppendAllText(Trace, $"{DateTime.Now:HH:mm:ss.fff} UI thread stuck {ms} ms in: {s2}\n");
                }
            }
        }) { IsBackground = true, Name = "draw watchdog" };
        watchdog.Start();
    }

    void MakeTexture()
    {
        surf?.Dispose(); tex9?.Dispose(); tex?.Dispose();
        texW = wantW; texH = wantH;
        tex = dev!.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)texW, Height = (uint)texH, MipLevels = 1, ArraySize = 1, Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource, MiscFlags = ResourceOptionFlags.Shared,
        });
        IntPtr shared;
        using (var r = tex.QueryInterface<IDXGIResource>()) shared = r.SharedHandle;
        tex9 = dev9!.CreateTexture((uint)texW, (uint)texH, 1, Vortice.Direct3D9.Usage.RenderTarget, Vortice.Direct3D9.Format.A8R8G8B8, Pool.Default, ref shared);
        surf = tex9.GetSurfaceLevel(0);
        Image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, surf.NativePointer);
    }

    // mpv's events: only shutdown matters here (everything else comes over the pipe)
    void EventLoop()
    {
        while (true)
        {
            var e = mpv_wait_event(mpv, -1);
            if (Marshal.ReadInt32(e) != 1 /* MPV_EVENT_SHUTDOWN */) continue;
            ui.BeginInvoke(() =>
            {
                Free();
                Exited?.Invoke();
            });
            return;
        }
    }

    // Stop now (it's told to quit over the pipe first, so it saves its resume point; this is the fallback).
    public void Kill() { if (!dead) { Command("quit"); } }

    void Free()
    {
        if (dead) return;
        dead = true;
        if (render != IntPtr.Zero) { mpv_render_context_free(render); render = IntPtr.Zero; }
        var h = mpv; mpv = IntPtr.Zero;
        // the core finishes (saving the resume point) off the UI thread
        Task.Run(() => { mpv_terminate_destroy(h); stopped.TrySetResult(); });
        Image.Lock(); Image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero); Image.Unlock();
        surf?.Dispose(); tex9?.Dispose(); tex?.Dispose();
        doneQuery?.Dispose(); ctx?.Dispose(); dev?.Dispose(); dev9?.Dispose(); d9?.Dispose();
    }

    void Set(string name, string value)
    {
        int r = mpv_set_option_string(mpv, name, value);
        if (r < 0) System.Diagnostics.Debug.WriteLine($"mpv option {name}={value}: error {r}");
    }

    void Command(params string[] args)
    {
        var ptrs = new IntPtr[args.Length + 1];
        try
        {
            for (int i = 0; i < args.Length; i++) ptrs[i] = Utf8(args[i]);
            mpv_command(mpv, ptrs);
        }
        finally { foreach (var p in ptrs) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p); }
    }

    static IntPtr Utf8(string s)
    {
        var b = System.Text.Encoding.UTF8.GetBytes(s + "\0");
        var p = Marshal.AllocHGlobal(b.Length);
        Marshal.Copy(b, 0, p, b.Length);
        return p;
    }

    static void Check(int r, string what) { if (r < 0) throw new InvalidOperationException($"libmpv {what} failed ({r})"); }

    // ---- libmpv ----
    [StructLayout(LayoutKind.Sequential)] struct RenderParam { public int Type; public IntPtr Data; public RenderParam(int t, IntPtr d) { Type = t; Data = d; } }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void RenderUpdate(IntPtr ctx);
    const string Lib = "libmpv-2.dll";
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr mpv_create();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern int mpv_initialize(IntPtr h);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern int mpv_set_option_string(IntPtr h, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern int mpv_command(IntPtr h, IntPtr[] args);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr mpv_wait_event(IntPtr h, double timeout);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern void mpv_terminate_destroy(IntPtr h);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern int mpv_render_context_create(out IntPtr ctx, IntPtr h, RenderParam[] ps);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern void mpv_render_context_set_update_callback(IntPtr ctx, RenderUpdate cb, IntPtr data);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern ulong mpv_render_context_update(IntPtr ctx);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern int mpv_render_context_render(IntPtr ctx, RenderParam[] ps);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern void mpv_render_context_report_swap(IntPtr ctx);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern void mpv_render_context_free(IntPtr ctx);
    [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();
}
