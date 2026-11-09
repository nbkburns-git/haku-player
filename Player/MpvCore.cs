using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace AnimePlayer;

// mpv running inside the app (libmpv), drawing with its Direct3D 11 render API into whatever texture it's given.
// This part has no UI in it: the screen side decides where frames go (MpvVideo: a WPF D3DImage; later a WinUI swap
// chain, docs/winui3-migration-plan.md) and calls NeedsFrame / RenderInto / ReportSwap on its UI thread.
// mpv is still driven over its JSON pipe (input-ipc-server), so the rest of the app talks to it the way it always did.
sealed class MpvCore
{
    public readonly ID3D11Device Device;
    public readonly ID3D11DeviceContext Context;
    public event Action? FrameReady;       // mpv has something new to draw (on mpv's own thread)
    public event Action? Shutdown;         // mpv shut down: quit, or the playlist ended (on mpv's event thread)
    public Task Stopped => stopped.Task;  // gone completely (after Free)
    public Task ShutDown => shutDown.Task; // mpv has quit (resume point saved): its render context can be freed now
    readonly TaskCompletionSource shutDown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Dead { get; private set; }
    readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

    IntPtr mpv, render;
    readonly RenderUpdate onUpdate; // kept alive: mpv holds a pointer to it

    string? playlist; // loaded by the constructor, or by Load() when the screen side has to be ready first

    // deferLoad: the playlist waits for Load(). The WinUI app makes its swap chain on mpv's device first: making it while
    // mpv's decoder was starting up on the same device hung both now and then.
    public MpvCore(IEnumerable<string> args, bool deferLoad = false)
    {
        onUpdate = _ => FrameReady?.Invoke();
        mpv = mpv_create();
        if (mpv == IntPtr.Zero) throw new InvalidOperationException("libmpv couldn't start");
        Set("vo", "libmpv");
        Set("config", "yes");                 // the user's mpv.conf, as mpv.exe would load it
        Set("input-default-bindings", "yes"); // keys we forward ("keypress") use mpv's own bindings
        Set("terminal", "no");
        if (Trace != null) { Set("log-file", Trace + ".mpv.log"); Set("msg-level", "all=v"); }
        foreach (var a in args)
        {
            var s = a.StartsWith("--") ? a[2..] : a;
            int eq = s.IndexOf('=');
            string name = eq < 0 ? s : s[..eq], value = eq < 0 ? "yes" : s[(eq + 1)..];
            if (name == "playlist") { playlist = value; continue; } // a command-line-only option: loaded below
            Set(name, value);
        }
        Check(mpv_initialize(mpv), "initialize");

        // Direct3D 11 for mpv, and for its hardware decoder too: a multithreaded device
        var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, flags, new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 }, out ID3D11Device? dev, out ID3D11DeviceContext? ctx).CheckError();
        Device = dev!; Context = ctx!;
        using (var mt = Device.QueryInterface<ID3D11Multithread>()) mt.SetMultithreadProtected(true);

        // the render context: mpv draws with our device
        var apiType = Marshal.StringToHGlobalAnsi("d3d11");
        var init = Marshal.AllocHGlobal(IntPtr.Size);
        Marshal.WriteIntPtr(init, Device.NativePointer);
        var ps = new[] { new RenderParam(1 /* API_TYPE */, apiType), new RenderParam(21 /* D3D11_INIT_PARAMS */, init), new RenderParam(0, IntPtr.Zero) };
        try { Check(mpv_render_context_create(out render, mpv, ps), "render context"); }
        finally { Marshal.FreeHGlobal(apiType); Marshal.FreeHGlobal(init); }
        mpv_render_context_set_update_callback(render, onUpdate, IntPtr.Zero);

        new Thread(EventLoop) { IsBackground = true, Name = "mpv events" }.Start();
        if (!deferLoad) Load();
    }

    // What every draw hands mpv: the texture to draw into, and "don't block". Made once and reused (each frame used to
    // allocate and free two bits of native memory and a new parameter array, on the UI thread); freed in Free.
    readonly IntPtr fboArg = Marshal.AllocHGlobal(IntPtr.Size), noWaitArg = AllocInt(0);
    RenderParam[]? drawParamsCache;
    RenderParam[] DrawParams => drawParamsCache ??= new[] { new RenderParam(22 /* D3D11_FBO */, fboArg), new RenderParam(12 /* BLOCK_FOR_TARGET_TIME */, noWaitArg), new RenderParam(0, IntPtr.Zero) };
    static IntPtr AllocInt(int v) { var p = Marshal.AllocHGlobal(4); Marshal.WriteInt32(p, v); return p; }

    // Starts playing the playlist (once).
    public void Load()
    {
        if (playlist == null || Dead) return;
        Command("loadlist", playlist);
        playlist = null;
    }

    // Does mpv have a new frame to draw? (Also lets mpv know we're ready for one.)
    public bool NeedsFrame()
    {
        if (Dead || render == IntPtr.Zero) return false;
        return (mpv_render_context_update(render) & 1 /* MPV_RENDER_UPDATE_FRAME */) != 0;
    }

    // Draws the current frame into tex, at tex's size, without waiting for its display time (this runs on a UI thread;
    // frames come when mpv asks anyway). Any 2D render-target texture works: BGRA8 today, 10-bit or FP16 for HDR.
    public void RenderInto(ID3D11Texture2D tex)
    {
        if (Dead || render == IntPtr.Zero) return;
        Marshal.WriteIntPtr(fboArg, tex.NativePointer);
        mpv_render_context_render(render, DrawParams);
    }

    // The frame drawn is on screen (or about to be): mpv's frame timing uses this.
    public void ReportSwap() { if (!Dead && render != IntPtr.Zero) mpv_render_context_report_swap(render); }

    // mpv's events: only shutdown matters here (everything else comes over the pipe)
    void EventLoop()
    {
        while (true)
        {
            var e = mpv_wait_event(mpv, -1);
            if (Marshal.ReadInt32(e) != 1 /* MPV_EVENT_SHUTDOWN */) continue;
            shutDown.TrySetResult();
            Shutdown?.Invoke();
            return;
        }
    }

    // Stop now (it's told to quit over the pipe first, so it saves its resume point; this is the fallback).
    public void Kill() { if (!Dead) Command("quit"); }

    // After shutdown, on the UI thread, once the screen side has let go of its textures.
    public void Free()
    {
        if (Dead) return;
        Dead = true;
        if (render != IntPtr.Zero) { mpv_render_context_free(render); render = IntPtr.Zero; }
        var h = mpv; mpv = IntPtr.Zero;
        // the core finishes (saving the resume point) off the UI thread
        Task.Run(() => { mpv_terminate_destroy(h); stopped.TrySetResult(); });
        Context.Dispose(); Device.Dispose();
        Marshal.FreeHGlobal(fboArg); Marshal.FreeHGlobal(noWaitArg);
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

    // ---- tracing ----

    // HAKU_TRACE=<file>: mpv writes a verbose log next to it, and a watchdog notes where the UI thread is stuck in a draw
    // (for hunting freezes; off normally).
    internal static readonly string? Trace = Environment.GetEnvironmentVariable("HAKU_TRACE") is { Length: > 0 } t ? t : null;

    // A timing line in the HAKU_TRACE file (tests and measuring; nothing is written normally).
    internal static void Note(string what)
    {
        if (Trace == null) return;
        try { System.IO.File.AppendAllText(Trace, $"{DateTime.Now:HH:mm:ss.fff} {what}\n"); } catch { }
    }

    static volatile string? step;
    static long stepAt;
    static Thread? watchdog;
    // Where the UI thread is in a draw (null: not drawing). Only when tracing.
    internal static void Step(string? s)
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
}
