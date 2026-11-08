using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Vortice.Direct3D11;
using Vortice.Direct3D9;
using Vortice.DXGI;

namespace AnimePlayer;

// The video as an ordinary WPF image. mpv (MpvCore) draws each frame into a Direct3D 11 texture, shared with the
// Direct3D 9 surface WPF shows in a D3DImage, on the UI thread, in the same frame as the layout: a resize draws the
// current frame at the new size right away.
// Before, mpv.exe drew into a window of its own inside ours, with the bars in a third window on top: three pictures that
// each redrew on their own after a resize, so resizing never looked right. Now there's one picture.
sealed class MpvVideo
{
    public readonly D3DImage Image = new();
    public event Action? Exited;           // mpv shut down (quit, or the playlist ended)
    public Task Stopped => core.Stopped;
    readonly MpvCore core;
    readonly Dispatcher ui;

    readonly ID3D11Query doneQuery;
    readonly IDirect3D9Ex d9;
    readonly IDirect3DDevice9Ex dev9;
    ID3D11Texture2D? tex;
    IDirect3DTexture9? tex9;
    IDirect3DSurface9? surf;
    int texW, texH;     // the texture's size now
    int wantW, wantH;   // the size it should be (the video area, in pixels)
    int posted;         // a draw is already queued on the UI thread

    public MpvVideo(IEnumerable<string> args)
    {
        ui = Dispatcher.CurrentDispatcher;
        core = new MpvCore(args);
        doneQuery = core.Device.CreateQuery(new QueryDescription(Vortice.Direct3D11.QueryType.Event));
        // Direct3D 9Ex for WPF
        d9 = D3D9.Direct3DCreate9Ex();
        var pp = new Vortice.Direct3D9.PresentParameters { Windowed = true, SwapEffect = Vortice.Direct3D9.SwapEffect.Discard, DeviceWindowHandle = GetDesktopWindow(), PresentationInterval = PresentInterval.Default };
        dev9 = d9.CreateDeviceEx(0, DeviceType.Hardware, IntPtr.Zero, CreateFlags.HardwareVertexProcessing | CreateFlags.Multithreaded | CreateFlags.FpuPreserve, pp);

        core.FrameReady += QueueDraw;
        core.Shutdown += () => ui.BeginInvoke(() => { Free(); Exited?.Invoke(); });
        Image.IsFrontBufferAvailableChanged += (_, _) => { if (Image.IsFrontBufferAvailable) { texW = texH = 0; QueueDraw(); } };
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
        if (core.Dead || wantW <= 0 || !Image.IsFrontBufferAvailable) return;
        bool sized = texW != wantW || texH != wantH;
        MpvCore.Step("update");
        if (!core.NeedsFrame() && !sized) { MpvCore.Step(null); return; }
        MpvCore.Step("D3DImage.Lock");
        Image.Lock();
        try
        {
            if (sized) MakeTexture();
            MpvCore.Step("mpv render");
            core.RenderInto(tex!);
            // WPF reads the texture through Direct3D 9: the frame must be finished first
            MpvCore.Step("wait for GPU");
            var ctx = core.Context;
            ctx.End(doneQuery);
            ctx.Flush();
            while (!ctx.IsDataAvailable(doneQuery)) Thread.SpinWait(50);
            Image.AddDirtyRect(new Int32Rect(0, 0, texW, texH));
        }
        finally { Image.Unlock(); }
        MpvCore.Step("report swap");
        core.ReportSwap();
        MpvCore.Step(null);
    }

    void MakeTexture()
    {
        surf?.Dispose(); tex9?.Dispose(); tex?.Dispose();
        texW = wantW; texH = wantH;
        tex = core.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)texW, Height = (uint)texH, MipLevels = 1, ArraySize = 1, Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource, MiscFlags = ResourceOptionFlags.Shared,
        });
        IntPtr shared;
        using (var r = tex.QueryInterface<IDXGIResource>()) shared = r.SharedHandle;
        tex9 = dev9.CreateTexture((uint)texW, (uint)texH, 1, Vortice.Direct3D9.Usage.RenderTarget, Vortice.Direct3D9.Format.A8R8G8B8, Pool.Default, ref shared);
        surf = tex9.GetSurfaceLevel(0);
        Image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, surf.NativePointer);
    }

    // Stop now (it's told to quit over the pipe first, so it saves its resume point; this is the fallback).
    public void Kill() => core.Kill();

    void Free()
    {
        if (core.Dead) return;
        Image.Lock(); Image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero); Image.Unlock();
        surf?.Dispose(); tex9?.Dispose(); tex?.Dispose();
        doneQuery.Dispose(); dev9.Dispose(); d9.Dispose();
        core.Free();
    }

    [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();
}
