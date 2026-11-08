using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AnimePlayer;

// WPF's side of the timeline thumbnails (Thumbs.cs has no UI in it): JPEG in and out, frozen pictures.
sealed partial class Thumbs
{
    // Exact frame for second t, or null if not decoded yet (decodes a JPEG every call: check Has first).
    public BitmapSource? Get(double t) => Jpeg(t) is { } jpg ? Decode(jpg) : null;

    private static partial byte[] EncodeJpeg(byte[] bgra, int w, int h)
    {
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
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
}
