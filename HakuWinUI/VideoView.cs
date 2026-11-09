using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Xaml.Controls;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace AnimePlayer;

// The video in a WinUI SwapChainPanel: mpv (MpvCore) draws straight into the swap chain's back buffer.
//   HDR display: 10-bit PQ / BT.2020 (Windows shows it as HDR). Otherwise 8-bit sRGB, as the WPF app.
// Frames are drawn on a render thread of our own (Present waits for the screen there, not on the UI thread). A resize
// draws at the new size right away, inside the layout pass, so the picture and the layout change together (measured in
// tools\hdr-spike: 2-3 ms behind the layout). Calls into mpv's renderer are serialized by drawLock.
sealed unsafe class VideoView : IDisposable
{
    public readonly SwapChainPanel Panel = new();
    public bool Hdr { get; private set; }
    public event Action? FirstFrame;           // on the render thread

    readonly MpvCore core;
    readonly object drawLock = new();
    readonly AutoResetEvent wake = new(false);
    readonly Thread thread;
    volatile bool stop;
    IDXGISwapChain1? sc;
    Format format;
    int bufW, bufH;                       // the swap chain's size
    int wantW = 1, wantH = 1;             // the panel's size in pixels
    float scaleX = 1, scaleY = 1;         // the panel's DPI scale
    bool sizeDirty = true, firstDone;

    public VideoView(MpvCore core, bool hdr)
    {
        this.core = core;
        Hdr = hdr;
        core.FrameReady += () => wake.Set();
        Panel.SizeChanged += (_, _) => Resize();
        Panel.CompositionScaleChanged += (_, _) => Resize();
        Panel.Loaded += (_, _) => Resize();
        MakeSwapChain();
        thread = new Thread(Loop) { IsBackground = true, Name = "video" };
        thread.Start();
    }

    // HDR on/off (the window moved to another monitor, or Windows' HDR was switched): a new swap chain, so Windows picks
    // up the new format (changing it in place leaves the white level stale: microsoft-ui-xaml #2761). UI thread.
    public void SetHdr(bool on)
    {
        if (on == Hdr) return;
        lock (drawLock)
        {
            SetPanelSwapChain(IntPtr.Zero);
            sc?.Dispose(); sc = null;
            Hdr = on;
            MakeSwapChain();
            DrawOnce(true);
        }
    }

    void MakeSwapChain()
    {
        ColorSpaceType cs;
        (format, cs) = Hdr ? (Format.R10G10B10A2_UNorm, ColorSpaceType.RgbFullG2084NoneP2020) : (Format.B8G8R8A8_UNorm, ColorSpaceType.RgbFullG22NoneP709);
        using var dxgiDev = core.Device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDev.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();
        bufW = Math.Max(1, wantW); bufH = Math.Max(1, wantH);
        sc = factory.CreateSwapChainForComposition(core.Device, new SwapChainDescription1
        {
            Width = (uint)bufW, Height = (uint)bufH, Format = format, SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput, BufferCount = 2, Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential, AlphaMode = AlphaMode.Ignore,
        }, null);
        using (var sc3 = sc.QueryInterface<IDXGISwapChain3>()) sc3.SetColorSpace1(cs);
        sizeDirty = true;
        SetPanelSwapChain(sc.NativePointer);
    }

    // ISwapChainPanelNative::SetSwapChain (vtable slot 3); UI thread
    void SetPanelSwapChain(IntPtr swapChain)
    {
        var unk = ((WinRT.IWinRTObject)Panel).NativeObject.ThisPtr;
        var iid = new Guid("63aad0b8-7c24-40ff-85a8-640d944cc325");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unk, ref iid, out var native));
        try { Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)(*(IntPtr**)native)[3])(native, swapChain)); }
        finally { Marshal.Release(native); }
    }

    // UI thread: the panel's size in pixels changed (layout size x DPI scale): draw at that size now
    void Resize()
    {
        if (Panel.ActualWidth < 1 || Panel.ActualHeight < 1) return; // not laid out yet: nothing to draw into
        int w = Math.Max(1, (int)Math.Round(Panel.ActualWidth * Panel.CompositionScaleX));
        int h = Math.Max(1, (int)Math.Round(Panel.ActualHeight * Panel.CompositionScaleY));
        if (w == wantW && h == wantH && scaleX == Panel.CompositionScaleX && scaleY == Panel.CompositionScaleY) return;
        wantW = w; wantH = h; scaleX = Panel.CompositionScaleX; scaleY = Panel.CompositionScaleY;
        sizeDirty = true;
        if (stop) return;
        // drawn right here when the render thread isn't drawing; otherwise it draws the new size next. Never wait for it:
        // its Present on a SwapChainPanel can need this (UI) thread, and waiting here deadlocked the app now and then
        if (!Monitor.TryEnter(drawLock)) { wake.Set(); return; }
        try { DrawOnce(true); }
        catch (Exception e) { MpvCore.Note("video resize: " + e.Message); }
        finally { Monitor.Exit(drawLock); }
    }

    void Loop()
    {
        try
        {
            while (!stop)
            {
                wake.WaitOne(250);
                if (stop) break;
                lock (drawLock) DrawOnce(false);
            }
        }
        catch (Exception e) { MpvCore.Note("video thread: " + e); }
    }

    // Draws if mpv has a new frame (or force: a new size or swap chain). Under drawLock.
    void DrawOnce(bool force)
    {
        if (core.Dead || sc == null || wantW <= 1) return; // wait for the first layout
        if (sizeDirty)
        {
            sizeDirty = false;
            if (wantW != bufW || wantH != bufH)
            {
                sc.ResizeBuffers(2, (uint)wantW, (uint)wantH, format, SwapChainFlags.None).CheckError();
                bufW = wantW; bufH = wantH;
            }
            // the panel scales its content by the DPI scale; undo that so one buffer pixel is one screen pixel
            using var sc2 = sc.QueryInterface<IDXGISwapChain2>();
            sc2.MatrixTransform = Matrix3x2.CreateScale(1 / scaleX, 1 / scaleY);
            force = true;
        }
        if (!core.NeedsFrame() && !force) return;
        using (var bb = sc.GetBuffer<ID3D11Texture2D>(0)) core.RenderInto(bb);
        sc.Present(1, PresentFlags.None);
        core.ReportSwap();
        if (!firstDone) { firstDone = true; MpvCore.Note($"first frame ({bufW}x{bufH}, {(Hdr ? "HDR10" : "SDR")})"); FirstFrame?.Invoke(); }
    }

    // Before MpvCore.Free: the swap chain lets go of mpv's device. UI thread.
    public void Dispose()
    {
        stop = true; wake.Set();
        thread.Join(2000);
        lock (drawLock)
        {
            try { SetPanelSwapChain(IntPtr.Zero); } catch { }
            sc?.Dispose(); sc = null;
        }
    }
}
