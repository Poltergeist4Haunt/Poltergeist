using SkiaSharp;

namespace Poltergeist.Core;

/// <summary>Optional touch-ups for a screenshot: rounded corners, a border and a drop shadow.</summary>
public static class ImageEffects
{
    public static bool Wanted(AppSettings s) =>
        s.FxOn && (s.FxRound || s.FxBorder > 0 || s.FxShadow);

    public static SKBitmap Apply(SKBitmap src, AppSettings s)
    {
        int w = src.Width, h = src.Height;
        float radius = s.FxRound ? Math.Min(s.FxRadius, Math.Min(w, h) / 2f) : 0;
        int pad = s.FxShadow ? Math.Max(8, s.FxShadowSize * 2) : 0;

        var bmp = new SKBitmap(w + pad * 2, h + pad * 2, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var c = new SKCanvas(bmp);
        c.Clear(SKColors.Transparent);
        var rect = new SKRect(pad, pad, pad + w, pad + h);

        if (s.FxShadow)
        {
            using var shadow = new SKPaint
            {
                IsAntialias = true, Color = new SKColor(0, 0, 0, 150),
                ImageFilter = SKImageFilter.CreateBlur(s.FxShadowSize / 2f, s.FxShadowSize / 2f),
            };
            c.DrawRoundRect(new SKRect(rect.Left, rect.Top + s.FxShadowSize / 3f, rect.Right, rect.Bottom + s.FxShadowSize / 3f), radius, radius, shadow);
        }

        c.Save();
        using (var clip = new SKPath()) { clip.AddRoundRect(rect, radius, radius); c.ClipPath(clip, SKClipOperation.Intersect, true); }
        using (var p = new SKPaint { FilterQuality = SKFilterQuality.High }) c.DrawBitmap(src, rect, p);

        c.Restore();

        if (s.FxBorder > 0)
        {
            ColorRgba.TryParse(s.FxBorderColor, out var col);
            float bw = s.FxBorder;
            using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = bw, Color = col.ToSk() };
            var inner = new SKRect(rect.Left + bw / 2, rect.Top + bw / 2, rect.Right - bw / 2, rect.Bottom - bw / 2);
            c.DrawRoundRect(inner, Math.Max(0, radius - bw / 2), Math.Max(0, radius - bw / 2), line);
        }
        return bmp;
    }

    /// <summary>Rewrites a PNG file with the effects applied. Returns false if nothing was changed.</summary>
    public static bool ApplyToFile(string file, AppSettings s)
    {
        if (!Wanted(s)) return false;
        try
        {
            using var src = SKBitmap.Decode(file);
            if (src == null) return false;
            using var done = Apply(src, s);
            using var img = SKImage.FromBitmap(done);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(file, data.ToArray());
            return true;
        }
        catch { return false; }
    }
}
