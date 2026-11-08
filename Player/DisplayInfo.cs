using System;
using System.Runtime.InteropServices;
using Vortice.DXGI;

namespace AnimePlayer;

// The monitor a window is on, as Windows reports it: is HDR on, how bright it gets, and where Windows puts SDR white
// (Settings > Display > HDR > "SDR content brightness"). No UI in here: both the WPF app and the coming WinUI one use it
// (docs/winui3-migration-plan.md). HDR output itself needs the WinUI version; today it only shows in the player stats.
sealed record DisplayInfo(string Name, bool Hdr, int Bits, double PeakNits, double FullFrameNits, double SdrWhiteNits)
{
    public static DisplayInfo? For(IntPtr hwnd)
    {
        try
        {
            var mon = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
            using var f = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint a = 0; f.EnumAdapters1(a, out var ad).Success; a++)
            {
                using (ad)
                {
                    for (uint o = 0; ad.EnumOutputs(o, out var op).Success; o++)
                    {
                        using (op)
                        {
                            using var o6 = op.QueryInterface<IDXGIOutput6>();
                            var d = o6.Description1;
                            if (d.Monitor != mon) continue;
                            bool hdr = d.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020;
                            return new DisplayInfo(d.DeviceName, hdr, (int)d.BitsPerColor, d.MaxLuminance, d.MaxFullFrameLuminance,
                                hdr ? SdrWhite(d.DeviceName) ?? 80 : 80);
                        }
                    }
                }
            }
        }
        catch (Exception e) { System.Diagnostics.Debug.WriteLine("DisplayInfo: " + e.Message); }
        return null;
    }

    // e.g. "HDR on · peak 417 nits · SDR white 240 nits" or "SDR · peak 270 nits"
    public override string ToString() =>
        Hdr ? $"HDR on · peak {PeakNits:0} nits · SDR white {SdrWhiteNits:0} nits" : $"SDR · peak {PeakNits:0} nits";

    // Windows' SDR white level for the display with this GDI name (\\.\DISPLAY1), in nits; null if it can't be read.
    // QueryDisplayConfig paths are 72 bytes: source adapter LUID at 0, source id at 8, target adapter LUID at 20, target id at 28.
    static double? SdrWhite(string gdiName)
    {
        if (GetDisplayConfigBufferSizes(2 /* QDC_ONLY_ACTIVE_PATHS */, out int np, out int nm) != 0) return null;
        var paths = new byte[np * 72];
        var modes = new byte[nm * 64];
        if (QueryDisplayConfig(2, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return null;
        for (int i = 0; i < np; i++)
        {
            int p = i * 72;
            // DISPLAYCONFIG_SOURCE_DEVICE_NAME (type 1): header (type, size, adapter LUID, id) + 32 wide chars
            var name = new byte[84];
            BitConverter.TryWriteBytes(name.AsSpan(0), 1);
            BitConverter.TryWriteBytes(name.AsSpan(4), 84);
            Array.Copy(paths, p, name, 8, 8);
            Array.Copy(paths, p + 8, name, 16, 4);
            if (DisplayConfigGetDeviceInfo(name) != 0) continue;
            var src = System.Text.Encoding.Unicode.GetString(name, 20, 64).TrimEnd('\0');
            if (!string.Equals(src, gdiName, StringComparison.OrdinalIgnoreCase)) continue;
            // DISPLAYCONFIG_SDR_WHITE_LEVEL (type 11): header + a ULONG, in thousandths of 80 nits
            var white = new byte[24];
            BitConverter.TryWriteBytes(white.AsSpan(0), 11);
            BitConverter.TryWriteBytes(white.AsSpan(4), 24);
            Array.Copy(paths, p + 20, white, 8, 8);
            Array.Copy(paths, p + 28, white, 16, 4);
            if (DisplayConfigGetDeviceInfo(white) != 0) return null;
            return BitConverter.ToUInt32(white, 20) / 1000.0 * 80;
        }
        return null;
    }

    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out int numPaths, out int numModes);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref int numPaths, byte[] paths, ref int numModes, byte[] modes, IntPtr topology);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(byte[] packet);
}
