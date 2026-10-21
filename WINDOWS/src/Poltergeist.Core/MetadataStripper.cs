using SkiaSharp;

namespace Poltergeist.Core;

/// <summary>Removes EXIF / GPS / XMP from JPEG and PNG photos before upload by re-encoding the pixels (other formats are left alone).</summary>
public static class MetadataStripper
{
    /// <summary>A cleaned copy in a temp folder (same file name), or null if there's nothing to do or it failed.</summary>
    public static string? Clean(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".jpg" or ".jpeg" or ".png")) return null;
        try
        {
            using var codec = SKCodec.Create(path);
            if (codec == null) return null;
            using var bmp = SKBitmap.Decode(codec);
            if (bmp == null) return null;
            using var upright = MediaClip.ApplyOrigin(bmp, codec.EncodedOrigin);     // bake the rotation in; the orientation tag goes away with the rest
            using var image = SKImage.FromBitmap(upright);
            var format = ext == ".png" ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg;
            using var data = image.Encode(format, ext == ".png" ? 100 : 97);
            if (data == null) return null;
            var dir = Path.Combine(Path.GetTempPath(), "poltergeist-clean-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            var outPath = Path.Combine(dir, Path.GetFileName(path));
            File.WriteAllBytes(outPath, data.ToArray());
            return outPath;
        }
        catch { return null; }
    }
}
