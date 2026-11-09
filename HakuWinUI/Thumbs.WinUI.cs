using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace AnimePlayer;

// WinUI's side of the timeline thumbnails (Thumbs.cs is shared): JPEG out with Windows' encoder (on Thumbs' own worker
// thread, so waiting on it there is fine), and pictures made on the UI thread (WinUI images can't be made elsewhere).
sealed partial class Thumbs
{
    private static partial byte[] EncodeJpeg(byte[] bgra, int w, int h) => EncodeAsync(bgra, w, h).GetAwaiter().GetResult();

    static async Task<byte[]> EncodeAsync(byte[] bgra, int w, int h)
    {
        using var ms = new InMemoryRandomAccessStream();
        var options = new BitmapPropertySet { { "ImageQuality", new BitmapTypedValue(0.85f, Windows.Foundation.PropertyType.Single) } };
        var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, ms, options);
        enc.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)w, (uint)h, 96, 96, bgra);
        await enc.FlushAsync();
        var bytes = new byte[ms.Size];
        using var reader = new DataReader(ms.GetInputStreamAt(0));
        await reader.LoadAsync((uint)ms.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    // The frame for second t as a picture, or null if not decoded yet. UI thread.
    public async Task<ImageSource?> Picture(double t)
    {
        if (Jpeg(t) is not { } jpg) return null;
        var bi = new BitmapImage();
        using var s = new MemoryStream(jpg).AsRandomAccessStream();
        await bi.SetSourceAsync(s);
        return bi;
    }
}
