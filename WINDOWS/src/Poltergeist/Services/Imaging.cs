using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace Poltergeist;

/// <summary>Moves pictures between Skia, GDI+ and WPF.</summary>
static class Imaging
{
    public static BitmapSource ToBitmapSource(SKBitmap bmp)
    {
        var fmt = bmp.AlphaType == SKAlphaType.Opaque ? System.Windows.Media.PixelFormats.Bgr32 : System.Windows.Media.PixelFormats.Pbgra32;
        var src = BitmapSource.Create(bmp.Width, bmp.Height, 96, 96, fmt, null, bmp.Bytes, bmp.RowBytes);
        src.Freeze();
        return src;
    }

    public static BitmapSource ToBitmapSource(SKImage img)
    {
        using var bmp = SKBitmap.FromImage(img);
        return ToBitmapSource(bmp);
    }

    public static SKBitmap FromGdi(System.Drawing.Bitmap gdi)
    {
        var bmp = new SKBitmap(gdi.Width, gdi.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var data = gdi.LockBits(new System.Drawing.Rectangle(0, 0, gdi.Width, gdi.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            var dst = bmp.GetPixels();
            for (int y = 0; y < gdi.Height; y++)
            {
                var row = new byte[gdi.Width * 4];
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                Marshal.Copy(row, 0, dst + y * bmp.RowBytes, row.Length);
            }
        }
        finally { gdi.UnlockBits(data); }
        return bmp;
    }

    public static byte[] EncodePng(SKBitmap bmp)
    {
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public static SKBitmap Crop(SKBitmap src, System.Drawing.Rectangle r)
    {
        r.Intersect(new System.Drawing.Rectangle(0, 0, src.Width, src.Height));
        var dst = new SKBitmap(r.Width, r.Height, src.ColorType, src.AlphaType);
        using var c = new SKCanvas(dst);
        c.DrawBitmap(src, new SKRect(r.X, r.Y, r.Right, r.Bottom), new SKRect(0, 0, r.Width, r.Height));
        return dst;
    }

    /// <summary>A small PNG (≤ <paramref name="side"/> px) of an image file, or null if it isn't one we can read.</summary>
    public static byte[]? Thumbnail(string file, int side = 160)
    {
        try
        {
            using var codec = SKCodec.Create(file);
            if (codec == null) return null;
            using var full = SKBitmap.Decode(codec);
            if (full == null) return null;
            float k = Math.Min(1f, side / (float)Math.Max(full.Width, full.Height));
            using var small = new SKBitmap(Math.Max(1, (int)(full.Width * k)), Math.Max(1, (int)(full.Height * k)));
            using (var c = new SKCanvas(small))
            using (var p = new SKPaint { FilterQuality = SKFilterQuality.High })
                c.DrawBitmap(full, new SKRect(0, 0, small.Width, small.Height), p);
            return EncodePng(small);
        }
        catch { return null; }
    }
}
