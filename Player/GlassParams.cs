using System;

namespace AnimePlayer;

// glass.glsl's params: the player bars' rectangles under the glass, and how the picture is shown (HDR). No UI in here.
static class GlassParams
{
    // Two numbers in -1..2 as one whole number below 2^24, so a float carries it exactly (unpacked in glass.glsl).
    public static long Pack(double u, double v) =>
        (long)Math.Round((Math.Clamp(u, -1, 2) + 1) / 3 * 4095) * 4096 + (long)Math.Round((Math.Clamp(v, -1, 2) + 1) / 3 * 4095);

    // One bar: "a_p=…,a_s=…,a_r=…," (x, y, w, h, corner radius in 0..1 of the picture, alpha 0..1); a hidden one sends zeros.
    public static void Append(System.Text.StringBuilder sb, char n, double x, double y, double w, double h, double radius, double alpha)
    {
        if (alpha <= 0 || w <= 0) { sb.Append(n).Append("_p=0,").Append(n).Append("_s=0,").Append(n).Append("_r=0,"); return; }
        sb.Append(n).Append("_p=").Append(Pack(x, y)).Append(',');
        sb.Append(n).Append("_s=").Append(Pack(w, h)).Append(',');
        sb.Append(n).Append("_r=").Append(Pack(radius, Math.Min(alpha, 1))).Append(',');
    }
}
