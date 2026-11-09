using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace AnimePlayer;

// A cover as the WinUI app keeps it: the file to show (the card-size copy in covers-small once it's made), and, between
// decoding a big original and saving its small copy, the shrunk pixels. Pictures are made on the UI thread from the file
// (Picture): WinUI images can't be made on the scan's worker threads, and BitmapImage decodes in the background anyway.
sealed class CoverImage
{
    public string Path;
    public SoftwareBitmap? Pixels; // only until SaveSmallCover has written them
    public bool Alpha;             // the image has an alpha channel (as the WPF app decides: by its format)
    public CoverImage(string path, SoftwareBitmap? pixels, bool alpha = false) { Path = path; Pixels = pixels; Alpha = alpha; }
}

public partial class MainWindow
{
    // Scan.cs: a cover file at this width (0: as it is, i.e. an existing small copy). Worker threads.
    private static partial object? DecodeImage(string? f, int width)
    {
        if (f == null || !File.Exists(f)) return null;
        if (width <= 0) return new CoverImage(f, null);
        try { return DecodeAsync(f, width).GetAwaiter().GetResult(); }
        catch { return new CoverImage(f, null); } // can't shrink it: show the original
    }

    static async Task<CoverImage> DecodeAsync(string f, int width)
    {
        using var fs = File.OpenRead(f);
        using var ras = fs.AsRandomAccessStream();
        var dec = await BitmapDecoder.CreateAsync(ras);
        uint w = (uint)width, h = (uint)Math.Max(1, Math.Round((double)dec.PixelHeight * width / Math.Max(1u, dec.PixelWidth)));
        if (dec.PixelWidth <= w) { w = dec.PixelWidth; h = dec.PixelHeight; }
        var px = await dec.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            new BitmapTransform { ScaledWidth = w, ScaledHeight = h, InterpolationMode = BitmapInterpolationMode.Fant },
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        return new CoverImage(f, px, dec.BitmapAlphaMode != BitmapAlphaMode.Ignore && dec.DecoderInformation.CodecId == BitmapDecoder.PngDecoderId);
    }

    // Scan.cs: the card-size copy, JPEG (PNG when the cover has see-through parts). Worker threads.
    private static partial void SaveSmallCover(object image, string jpg, string png)
    {
        if (image is not CoverImage { Pixels: { } px } c) return;
        try
        {
            bool alpha = c.Alpha;
            var target = alpha ? png : jpg;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            var tmp = target + "." + Environment.CurrentManagedThreadId + ".tmp"; // scans run in parallel
            EncodeAsync(px, alpha, tmp).GetAwaiter().GetResult();
            File.Move(tmp, target, true);
            c.Path = target;
        }
        catch { } // no copy: decoded from the original again next time
        finally { c.Pixels?.Dispose(); c.Pixels = null; }
    }

    static async Task EncodeAsync(SoftwareBitmap px, bool png, string file)
    {
        using var fs = File.Create(file);
        using var ras = fs.AsRandomAccessStream();
        var options = png ? null : new BitmapPropertySet { { "ImageQuality", new BitmapTypedValue(0.92f, Windows.Foundation.PropertyType.Single) } };
        var enc = options == null ? await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ras) : await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, ras, options);
        enc.SetSoftwareBitmap(png ? px : SoftwareBitmap.Convert(px, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore));
        await enc.FlushAsync();
    }

    // A cover (Entry.Cover) as a picture, for the UI thread. Decoded in the background at about this width.
    static ImageSource? Picture(object? cover, int width = 420) =>
        cover is CoverImage c ? new BitmapImage(new Uri(c.Path)) { DecodePixelWidth = width } : null;

    static ImageSource? PictureOf(string? file, int width) =>
        file != null && File.Exists(file) ? new BitmapImage(new Uri(file)) { DecodePixelWidth = width } : null;

}
