using SkiaSharp;

namespace Poltergeist.Core;

public static class SkExt
{
    public static SKColor ToSk(this ColorRgba c) => new(c.R, c.G, c.B, c.A);
    public static SKPoint ToSk(this PointD p) => new((float)p.X, (float)p.Y);
    public static SKRect ToSk(this RectD r) => new((float)r.MinX, (float)r.MinY, (float)r.MaxX, (float)r.MaxY);
    public static ColorRgba ToRgba(this SKColor c) => new(c.Red, c.Green, c.Blue, c.Alpha);
}

/// <summary>
/// Draws annotations in image-pixel space with a top-left origin (the same drawing is used by the editors' canvases, the saved
/// screenshot and every frame of an exported video, so they always look identical).
/// </summary>
public sealed class AnnotationRenderer
{
    /// <summary>The picture underneath: the screenshot or the current video frame. Blur samples it.</summary>
    public SKImage? Base { get; set; }
    public SizeD Size { get; }

    static readonly Lazy<SKTypeface> BoldFace = new(() =>
        SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) is { } t && t.FamilyName == "Segoe UI" ? t
        : SKTypeface.FromFamilyName("Helvetica Neue", SKFontStyle.Bold) ?? SKTypeface.Default);

    public AnnotationRenderer(SKImage image) { Base = image; Size = new SizeD(image.Width, image.Height); }
    public AnnotationRenderer(SizeD size) { Size = size; }

    /// <summary>Scales strokes and text with the picture so they look the same on 1x and Retina / 4K captures.</summary>
    public double Unit => Math.Max(1, Size.Width / 700);

    public static double BlurStrength(int level) => new[] { 0.6, 1.0, 1.6 }[Math.Min(Math.Max(level, 1), 3) - 1];

    public double StrokeWidth(Annotation a) => a.Kind == AnnotationKind.Marker ? Unit * (5 + 4 * a.Level) : Unit * (1 + a.Level);
    public float FontSize(int level) => (float)(Unit * (6 + 4 * level));

    // ---- drawing ------------------------------------------------------------------------------------------------------

    /// <summary>Draws the base picture, then every annotation in order, then the optional draft.</summary>
    public void DrawAll(SKCanvas canvas, IEnumerable<Annotation> list, Annotation? draft = null)
    {
        if (Base != null) canvas.DrawImage(Base, new SKRect(0, 0, (float)Size.Width, (float)Size.Height), new SKPaint { FilterQuality = SKFilterQuality.High });
        foreach (var a in list) Draw(canvas, a);
        if (draft != null) Draw(canvas, draft);
    }

    /// <summary>Video frames: blur regions first (they sample the frame), then everything else on top.</summary>
    public void DrawOverFrame(SKCanvas canvas, IEnumerable<Annotation> list)
    {
        var all = list.ToList();
        foreach (var a in all.Where(a => a.Kind == AnnotationKind.Blur)) Draw(canvas, a);
        foreach (var a in all.Where(a => a.Kind != AnnotationKind.Blur && a.Kind != AnnotationKind.Zoom)) Draw(canvas, a);
    }

    public void Draw(SKCanvas c, Annotation a)
    {
        if (a.Kind == AnnotationKind.Zoom) return;                  // a camera move, not something drawn
        if (a.Opacity >= 0.999) { DrawOpaque(c, a); return; }
        if (a.Opacity <= 0.001) return;
        using var layer = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(a.Opacity * 255)) };
        c.SaveLayer(layer);                                         // fade the whole object as one, so overlapping parts don't double up
        DrawOpaque(c, a);
        c.Restore();
    }

    void DrawOpaque(SKCanvas c, Annotation a)
    {
        float w = (float)(Unit * (1 + a.Level));
        switch (a.Kind)
        {
            case AnnotationKind.Arrow: DrawArrow(c, a, w); break;
            case AnnotationKind.Box:
                using (var p = StrokePaint(a.Color, w)) { p.StrokeJoin = SKStrokeJoin.Round; c.DrawRect(a.Rect.ToSk(), p); }
                break;
            case AnnotationKind.Circle:
                using (var p = StrokePaint(a.Color, w)) c.DrawOval(a.Rect.ToSk(), p);
                break;
            case AnnotationKind.Text: DrawText(c, a); break;
            case AnnotationKind.Blur: DrawBlur(c, a); break;
            case AnnotationKind.Media: DrawMedia(c, a); break;
            case AnnotationKind.Pen:
            case AnnotationKind.Marker:
            case AnnotationKind.Curve: DrawFreehand(c, a); break;
        }
    }

    static SKPaint StrokePaint(ColorRgba color, float width) => new()
    {
        Color = color.ToSk(), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = width,
    };

    static SKPaint FillPaint(ColorRgba color) => new() { Color = color.ToSk(), IsAntialias = true, Style = SKPaintStyle.Fill };

    void DrawArrow(SKCanvas c, Annotation a, float w)
    {
        double dx = a.P2.X - a.P1.X, dy = a.P2.Y - a.P1.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len <= 1) return;
        double ux = dx / len, uy = dy / len;
        double headLen = Math.Min(len, w * 4 + 6 * Unit);
        double headHalf = headLen * 0.5;
        var bx = a.P2.X - ux * headLen;
        var by = a.P2.Y - uy * headLen;

        using (var line = StrokePaint(a.Color, w))
        {
            line.StrokeCap = SKStrokeCap.Round;
            c.DrawLine(a.P1.ToSk(), new SKPoint((float)(bx + ux * headLen * 0.3), (float)(by + uy * headLen * 0.3)), line);
        }
        using var head = new SKPath();
        head.MoveTo(a.P2.ToSk());
        head.LineTo((float)(bx - uy * headHalf), (float)(by + ux * headHalf));
        head.LineTo((float)(bx + uy * headHalf), (float)(by - ux * headHalf));
        head.Close();
        using var fill = FillPaint(a.Color);
        c.DrawPath(head, fill);
    }

    void DrawText(SKCanvas c, Annotation a)
    {
        if (string.IsNullOrEmpty(a.Text)) return;
        using var font = new SKFont(BoldFace.Value, FontSize(a.Level)) { Edging = SKFontEdging.SubpixelAntialias };
        using var paint = FillPaint(a.Color);
        paint.ImageFilter = SKImageFilter.CreateDropShadow(0, (float)Unit, (float)(Unit * 2), (float)(Unit * 2), new SKColor(0, 0, 0, 140));
        var m = font.Metrics;
        c.DrawText(a.Text, (float)a.P1.X, (float)(a.P1.Y - m.Ascent), font, paint);   // P1 is the text's top-left corner
    }

    /// <summary>Size of a text annotation, for hit-testing and the selection box.</summary>
    public SizeD MeasureText(string text, int level)
    {
        using var font = new SKFont(BoldFace.Value, FontSize(level));
        using var paint = new SKPaint(font);
        var width = paint.MeasureText(string.IsNullOrEmpty(text) ? " " : text);
        var m = font.Metrics;
        return new SizeD(width, m.Descent - m.Ascent);
    }

    void DrawMedia(SKCanvas c, Annotation a)
    {
        if (a.Image == null) return;
        var box = a.Rect;
        if (box.Width <= 1 || box.Height <= 1) return;
        double aspect = (double)a.Image.Width / Math.Max(1, a.Image.Height);
        var fit = box;
        if (box.Width / box.Height > aspect) fit = new RectD(box.X + (box.Width - box.Height * aspect) / 2, box.Y, box.Height * aspect, box.Height);
        else fit = new RectD(box.X, box.Y + (box.Height - box.Width / aspect) / 2, box.Width, box.Width / aspect);
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
        c.DrawImage(a.Image, fit.ToSk(), paint);                                    // aspect-fit: resizing never stretches it
    }

    /// <summary>Pixelated (hides text for sure) or softly blurred, scaled to the picture size.</summary>
    void DrawBlur(SKCanvas c, Annotation a)
    {
        if (Base == null) return;
        var full = new RectD(0, 0, Size.Width, Size.Height);
        var r = a.Rect.Intersect(full);
        if (r.Width < 2 || r.Height < 2) return;
        var rect = new SKRect((float)Math.Floor(r.MinX), (float)Math.Floor(r.MinY), (float)Math.Ceiling(r.MaxX), (float)Math.Ceiling(r.MaxY));
        double k = BlurStrength(a.Level);
        if (a.Soft)
        {
            float sigma = (float)(Unit * 5 * k);
            using var paint = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp), FilterQuality = SKFilterQuality.High };
            c.Save();
            c.ClipRect(rect);
            c.DrawImage(Base, new SKRect(0, 0, (float)Size.Width, (float)Size.Height), paint);   // blurs across the box edge, like a real blur
            c.Restore();
        }
        else
        {
            double block = Math.Max(8, Unit * 6 * k);
            int sw = Math.Max(1, (int)Math.Ceiling(rect.Width / block)), sh = Math.Max(1, (int)Math.Ceiling(rect.Height / block));
            // average the region down to a grid of blocks, then draw it back up without smoothing
            using var small = new SKBitmap(sw, sh, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var sc = new SKCanvas(small))
            {
                using var p = new SKPaint { FilterQuality = SKFilterQuality.High };
                sc.DrawImage(Base, rect, new SKRect(0, 0, sw, sh), p);
            }
            using var smallImg = SKImage.FromBitmap(small);
            using var up = new SKPaint { FilterQuality = SKFilterQuality.None };
            c.DrawImage(smallImg, new SKRect(0, 0, sw, sh), rect, up);
        }
    }

    /// <summary>Pen, marker and curve arrow: a smooth line through the drawn points.</summary>
    void DrawFreehand(SKCanvas c, Annotation a)
    {
        var pts = MappedPath(a);
        if (pts.Count < 2) return;
        float w = (float)StrokeWidth(a);
        using var path = new SKPath();
        path.MoveTo(pts[0].ToSk());
        if (pts.Count == 2) path.LineTo(pts[1].ToSk());
        else
        {
            for (int i = 1; i < pts.Count - 1; i++)
            {
                var mid = new PointD((pts[i].X + pts[i + 1].X) / 2, (pts[i].Y + pts[i + 1].Y) / 2);
                path.QuadTo(pts[i].ToSk(), mid.ToSk());
            }
            path.LineTo(pts[^1].ToSk());
        }
        bool marker = a.Kind == AnnotationKind.Marker;
        using (var p = StrokePaint(marker ? a.Color.WithAlpha(102) : a.Color, w))
        {
            p.StrokeCap = marker ? SKStrokeCap.Butt : SKStrokeCap.Round;
            p.StrokeJoin = SKStrokeJoin.Round;
            c.DrawPath(path, p);                                                   // one shape: a marker crossing itself isn't darker
        }
        if (a.Kind != AnnotationKind.Curve) return;
        var end = pts[^1];
        double headLen = w * 4 + 6 * Unit;
        var from = pts[^2];
        for (int i = pts.Count - 2; i >= 0; i--)
            if (pts[i].DistanceTo(end) >= headLen * 0.8) { from = pts[i]; break; }
        double dx = end.X - from.X, dy = end.Y - from.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 0.5) return;
        double ux = dx / len, uy = dy / len;
        double bx = end.X - ux * headLen, by = end.Y - uy * headLen;
        using var head = new SKPath();
        head.MoveTo(end.ToSk());
        head.LineTo((float)(bx - uy * headLen * 0.5), (float)(by + ux * headLen * 0.5));
        head.LineTo((float)(bx + uy * headLen * 0.5), (float)(by - ux * headLen * 0.5));
        head.Close();
        using var fill = FillPaint(a.Color);
        c.DrawPath(head, fill);
    }

    // ---- geometry helpers ---------------------------------------------------------------------------------------------

    /// <summary>The freehand points mapped from 0…1 into the annotation's rectangle.</summary>
    public static List<PointD> MappedPath(Annotation a)
    {
        var box = a.Rect;
        return a.Path.Select(p => new PointD(box.MinX + p.X * box.Width, box.MinY + p.Y * box.Height)).ToList();
    }

    /// <summary>Is <paramref name="p"/> within <paramref name="tolerance"/> of the drawn line?</summary>
    public static bool NearPath(Annotation a, PointD p, double tolerance)
    {
        var pts = MappedPath(a);
        for (int i = 1; i < pts.Count; i++)
            if (DistanceToSegment(p, pts[i - 1], pts[i]) <= tolerance) return true;
        return false;
    }

    public static double DistanceToSegment(PointD p, PointD a, PointD b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len2 = dx * dx + dy * dy;
        double f = len2 > 0 ? Math.Min(1, Math.Max(0, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2)) : 0;
        return Math.Sqrt(Math.Pow(p.X - (a.X + f * dx), 2) + Math.Pow(p.Y - (a.Y + f * dy), 2));
    }

    /// <summary>The bounds used for the selection box and for clicking on an annotation.</summary>
    public RectD BoundsOf(Annotation a)
    {
        if (a.Kind == AnnotationKind.Text)
        {
            var s = MeasureText(a.Text, a.Level);
            return new RectD(a.P1.X, a.P1.Y, s.Width, s.Height);
        }
        return a.Rect;
    }

    /// <summary>Does a click at <paramref name="p"/> hit this annotation? (Lines and strokes need to be close; boxes can be clicked inside.)</summary>
    public bool Hits(Annotation a, PointD p, double tol)
    {
        if (a.IsFreehand) return NearPath(a, p, tol + StrokeWidth(a) / 2);
        if (a.Kind == AnnotationKind.Arrow)
            return DistanceToSegment(p, a.P1, a.P2) <= tol + Unit * (1 + a.Level);
        return BoundsOf(a).Inflate(tol).Contains(p);
    }
}
