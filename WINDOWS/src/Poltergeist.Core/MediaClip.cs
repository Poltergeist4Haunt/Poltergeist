using System.Diagnostics;
using SkiaSharp;

namespace Poltergeist.Core;

public enum MediaKind { Image, Gif, Video }

/// <summary>An image, GIF or video brought in to place on a screenshot or recording.</summary>
public sealed class MediaClip : IDisposable
{
    public MediaKind Kind { get; }
    public string Name { get; }
    public string? Path { get; }
    public int PixelWidth { get; }
    public int PixelHeight { get; }
    /// <summary>Seconds. 0 for a still image; one loop for a GIF; the whole clip for a video.</summary>
    public double Duration { get; }
    public bool HasAudio { get; }
    public double Aspect => PixelHeight > 0 ? (double)PixelWidth / PixelHeight : 1;
    public bool IsAnimated => Kind != MediaKind.Image;

    readonly SKImage? _still;
    readonly List<(SKImage Image, double Start)> _gif = new();
    readonly object _lock = new();
    (double Time, SKImage Image)? _videoFrame;

    public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".ico" };
    public static readonly string[] VideoExtensions = { ".mp4", ".mov", ".m4v", ".webm", ".mkv", ".avi", ".wmv" };

    MediaClip(MediaKind kind, string name, string? path, int w, int h, double duration, bool hasAudio, SKImage? still)
    {
        Kind = kind; Name = name; Path = path; PixelWidth = w; PixelHeight = h; Duration = duration; HasAudio = hasAudio; _still = still;
    }

    public static MediaClip FromBitmap(SKBitmap bitmap, string name, string? path = null) =>
        new(MediaKind.Image, name, path, bitmap.Width, bitmap.Height, 0, false, SKImage.FromBitmap(bitmap));

    /// <summary>Loads a file, or returns null if it isn't something we can read.</summary>
    public static async Task<MediaClip?> LoadAsync(string path, bool allowVideo = true)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        var name = System.IO.Path.GetFileName(path);
        try
        {
            if (ext == ".gif") return LoadGif(path, name);
            if (ImageExtensions.Contains(ext)) return LoadImage(path, name);
            if (allowVideo && VideoExtensions.Contains(ext)) return await LoadVideoAsync(path, name);
        }
        catch { }
        return null;
    }

    static MediaClip? LoadImage(string path, string name)
    {
        using var codec = SKCodec.Create(path);
        if (codec == null) return null;
        using var bmp = SKBitmap.Decode(codec);
        if (bmp == null) return null;
        using var upright = ApplyOrigin(bmp, codec.EncodedOrigin);
        using var capped = Cap(upright, 2400);
        return FromBitmap(capped, name, path);
    }

    /// <summary>Applies the EXIF orientation so photos aren't sideways.</summary>
    public static SKBitmap ApplyOrigin(SKBitmap src, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft || origin == SKEncodedOrigin.Default) return src.Copy();
        bool swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var dst = new SKBitmap(swap ? src.Height : src.Width, swap ? src.Width : src.Height, src.ColorType, src.AlphaType);
        using var c = new SKCanvas(dst);
        var m = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, src.Width, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, src.Width, 0, -1, src.Height, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, src.Height, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, src.Height, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, src.Height, -1, 0, src.Width, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, src.Width, 0, 0, 1),
            _ => SKMatrix.Identity,
        };
        c.SetMatrix(m);
        c.DrawBitmap(src, 0, 0);
        return dst;
    }

    /// <summary>Scales down so neither side exceeds <paramref name="maxSide"/> (never up).</summary>
    public static SKBitmap Cap(SKBitmap src, int maxSide)
    {
        int longest = Math.Max(src.Width, src.Height);
        if (longest <= maxSide) return src.Copy();
        double k = (double)maxSide / longest;
        var info = new SKImageInfo(Math.Max(1, (int)(src.Width * k)), Math.Max(1, (int)(src.Height * k)), SKColorType.Bgra8888, SKAlphaType.Premul);
        return src.Resize(info, SKFilterQuality.High);
    }

    static MediaClip? LoadGif(string path, string name)
    {
        using var codec = SKCodec.Create(path);
        if (codec == null) return null;
        int count = codec.FrameCount;
        if (count <= 1) return LoadImage(path, name);

        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var frames = codec.FrameInfo;
        // Long GIFs are thinned out so decoded frames stay within ~250 MB.
        double perFrame = Math.Min(1.0, 640.0 / Math.Max(info.Width, info.Height));
        long bytes = Math.Max(1, (long)(info.Width * perFrame) * (long)(info.Height * perFrame) * 4);
        int budget = (int)Math.Max(24, 250_000_000 / bytes);
        int step = Math.Max(1, (int)Math.Ceiling((double)count / budget));

        var decoded = new SKBitmap[count];
        var clip = new List<(SKImage, double)>();
        double t = 0;
        try
        {
            for (int i = 0; i < count; i++)
            {
                var bmp = new SKBitmap(info);
                var opts = new SKCodecOptions(i);
                int req = frames[i].RequiredFrame;
                if (req >= 0 && decoded[req] != null)
                {
                    var prior = decoded[req].Bytes;                    // the frame this one is drawn on top of
                    System.Runtime.InteropServices.Marshal.Copy(prior, 0, bmp.GetPixels(), Math.Min(prior.Length, bmp.ByteCount));
                    opts = new SKCodecOptions(i, req);
                }
                if (codec.GetPixels(info, bmp.GetPixels(), opts) != SKCodecResult.Success) { bmp.Dispose(); break; }
                decoded[i] = bmp;
            }
            double delay = 0;
            for (int i = 0; i < count; i++)
            {
                if (decoded[i] == null) break;
                double d = frames[i].Duration / 1000.0;
                delay += d < 0.02 ? 0.1 : d;                       // browsers treat 0–1 cs delays as 100 ms
                if (i % step != 0) continue;
                using var small = Cap(decoded[i], 640);
                clip.Add((SKImage.FromBitmap(small), t));
                t += delay;
                delay = 0;
            }
        }
        finally { foreach (var b in decoded) b?.Dispose(); }
        if (clip.Count == 0 || t <= 0) return null;
        var first = clip[0].Item1;
        var mc = new MediaClip(MediaKind.Gif, name, path, first.Width, first.Height, t, false, null);
        mc._gif.AddRange(clip);
        return mc;
    }

    static async Task<MediaClip?> LoadVideoAsync(string path, string name)
    {
        var info = await Ffmpeg.ProbeAsync(path);
        if (info == null || info.Width <= 0 || info.Duration <= 0) return null;
        return new MediaClip(MediaKind.Video, name, path, info.Width, info.Height, info.Duration, info.HasAudio, null);
    }

    /// <summary>The picture at <paramref name="t"/> seconds after the clip started. GIFs loop; videos hold their last frame.</summary>
    public SKImage? FrameAt(double t)
    {
        switch (Kind)
        {
            case MediaKind.Image: return _still;
            case MediaKind.Gif:
            {
                if (_gif.Count == 0) return null;
                if (Duration <= 0) return _gif[0].Image;
                double pos = t % Duration;
                if (pos < 0) pos += Duration;
                int lo = 0, hi = _gif.Count - 1;
                while (lo < hi)                                   // last frame starting at or before `pos`
                {
                    int mid = (lo + hi + 1) / 2;
                    if (_gif[mid].Start <= pos) lo = mid; else hi = mid - 1;
                }
                return _gif[lo].Image;
            }
            default:
                return VideoThumbnail(t);
        }
    }

    /// <summary>One decoded frame of a video, for previews (cached; each call may start ffmpeg, so don't call per paint).</summary>
    SKImage? VideoThumbnail(double t)
    {
        t = Math.Min(Math.Max(0, t), Math.Max(0, Duration - 0.05));
        lock (_lock)
        {
            if (_videoFrame is { } c && Math.Abs(c.Time - t) < 1.0 / 30) return c.Image;
            var ff = Ffmpeg.LocateFfmpeg();
            if (ff == null || Path == null) return _videoFrame?.Image;
            var psi = Proc.Start(ff, new[]
            {
                "-v", "error", "-ss", Ffmpeg.N(t), "-i", Path, "-frames:v", "1",
                "-vf", "scale='min(960,iw)':-2", "-f", "image2pipe", "-c:v", "png", "-",
            });
            using var p = Process.Start(psi);
            if (p == null) return _videoFrame?.Image;
            p.ErrorDataReceived += (_, _) => { };
            using var ms = new MemoryStream();
            p.StandardOutput.BaseStream.CopyTo(ms);
            p.WaitForExit(5000);
            if (ms.Length == 0) return _videoFrame?.Image;
            var img = SKImage.FromEncodedData(ms.ToArray());
            if (img != null) _videoFrame = (t, img);
            return img ?? _videoFrame?.Image;
        }
    }

    public void Dispose()
    {
        _still?.Dispose();
        foreach (var f in _gif) f.Image.Dispose();
        _videoFrame?.Image.Dispose();
    }
}
