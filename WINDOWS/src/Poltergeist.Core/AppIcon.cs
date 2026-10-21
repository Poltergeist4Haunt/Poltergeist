using SkiaSharp;

namespace Poltergeist.Core;

/// <summary>
/// The Poltergeist ghost (identical to the Mac menu-bar icon: an 18×18 box with two eyes cut out) and the app icon built from it:
/// the ghost in Haunt pink on a dark rounded tile.
/// </summary>
public static class AppIcon
{
    /// <summary>SVG path data, y pointing down. Fill with the even-odd rule so the eyes stay open.</summary>
    public const string GhostPath =
        "M3,15 L3,9 C3,1 15,1 15,9 L15,15 C14.4,17.7 11.6,17.7 11,15 C10.4,17.7 7.6,17.7 7,15 C6.4,17.7 3.6,17.7 3,15 Z " +
        "M5.6,7.9 A1.2,1.5 0 1 0 8,7.9 A1.2,1.5 0 1 0 5.6,7.9 Z M10,7.9 A1.2,1.5 0 1 0 12.4,7.9 A1.2,1.5 0 1 0 10,7.9 Z";

    public static SKPath Ghost()
    {
        var p = SKPath.ParseSvgPathData(GhostPath);
        p.FillType = SKPathFillType.EvenOdd;
        return p;
    }

    /// <summary>The ghost alone, in one colour, on a transparent square (used for the tray icon).</summary>
    public static SKBitmap RenderGhost(int px, SKColor color, bool recordingDot = false)
    {
        var bmp = new SKBitmap(px, px, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var c = new SKCanvas(bmp);
        c.Clear(SKColors.Transparent);
        using var path = Ghost();
        c.Scale(px / 18f);
        using var paint = new SKPaint { IsAntialias = true, Color = color };
        c.DrawPath(path, paint);
        if (recordingDot)
        {
            using var halo = new SKPaint { IsAntialias = true, BlendMode = SKBlendMode.Clear };
            c.DrawCircle(14.6f, 3.6f, 4.0f, halo);
            using var dot = new SKPaint { IsAntialias = true, Color = new SKColor(0xff, 0x45, 0x3a) };
            c.DrawCircle(14.6f, 3.6f, 3.0f, dot);
        }
        return bmp;
    }

    public static SKBitmap Render(int px)
    {
        var bmp = new SKBitmap(px, px, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var c = new SKCanvas(bmp);
        c.Clear(SKColors.Transparent);
        float s = px;
        var tile = new SKRect(s * 0.06f, s * 0.06f, s * 0.94f, s * 0.94f);
        float r = tile.Width * 0.2237f;
        using (var bg = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, tile.Top), new SKPoint(0, tile.Bottom),
                new[] { new SKColor(33, 31, 38), new SKColor(10, 10, 13) }, null, SKShaderTileMode.Clamp),
        }) c.DrawRoundRect(tile, r, r, bg);
        c.Save();
        using (var clip = new SKPath()) { clip.AddRoundRect(tile, r, r); c.ClipPath(clip, SKClipOperation.Intersect, true); }
        using (var glow = new SKPaint
        {
            Shader = SKShader.CreateRadialGradient(new SKPoint(s * 0.5f, s * 0.48f), s * 0.4f,
                new[] { new SKColor(242, 125, 179, 56), new SKColor(242, 125, 179, 0) }, null, SKShaderTileMode.Clamp),
        }) c.DrawRect(tile, glow);
        c.Restore();
        using (var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1, s * 0.004f), Color = new SKColor(255, 255, 255, 20) })
            c.DrawRoundRect(tile, r, r, edge);

        float size = tile.Width * 0.58f, k = size / 18f;
        c.Translate(s * 0.5f - size / 2, s * 0.5f - size / 2 - s * 0.006f);
        c.Scale(k, k);
        using var ghost = Ghost();
        using var fill = new SKPaint { IsAntialias = true, Color = new SKColor(0xf2, 0x7d, 0xb3) };
        c.DrawPath(ghost, fill);
        return bmp;
    }

    /// <summary>Writes a multi-size .ico (PNG-compressed entries, which Windows Vista and later read).</summary>
    public static void WriteIco(string file, params int[] sizes)
    {
        if (sizes.Length == 0) sizes = new[] { 16, 24, 32, 48, 64, 128, 256 };
        var images = sizes.Select(s =>
        {
            using var bmp = Render(s);
            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            return (Size: s, Png: data.ToArray());
        }).ToList();
        using var fs = File.Create(file);
        using var w = new BinaryWriter(fs);
        w.Write((short)0); w.Write((short)1); w.Write((short)images.Count);
        int offset = 6 + 16 * images.Count;
        foreach (var (size, png) in images)
        {
            w.Write((byte)(size >= 256 ? 0 : size)); w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
            w.Write(png.Length); w.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in images) w.Write(png);
    }
}
