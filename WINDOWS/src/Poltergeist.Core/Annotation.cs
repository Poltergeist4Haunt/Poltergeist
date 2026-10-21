using SkiaSharp;

namespace Poltergeist.Core;

public enum AnnotationKind { Arrow, Box, Circle, Text, Blur, Media, Zoom, Pen, Marker, Curve }

public enum HandleTarget { Body, P1, P2 }

/// <summary>
/// One drawn thing: arrow, shape, text, blur box, pasted picture, freehand stroke, or a zoom region.
/// Coordinates are image / video pixels with the origin top-left.
/// </summary>
public sealed record Annotation
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public AnnotationKind Kind { get; init; }
    public PointD P1 { get; init; }
    public PointD P2 { get; init; }
    public string Text { get; init; } = "";
    public ColorRgba Color { get; init; } = Palette.Accent;
    /// <summary>Size / strength: 1 (S), 2 (M) or 3 (L).</summary>
    public int Level { get; init; } = 2;
    /// <summary>For <see cref="AnnotationKind.Media"/>: the picture, fitted inside the rectangle P1–P2.</summary>
    public SKImage? Image { get; init; }
    public double Opacity { get; init; } = 1;
    /// <summary>For blur: a smooth blur instead of pixels.</summary>
    public bool Soft { get; init; }
    /// <summary>
    /// For freehand kinds (pen, marker, curve arrow): the drawn path as points in 0…1 relative to the rectangle P1–P2, so
    /// moving, resizing and animating the rectangle moves, resizes and animates the drawing.
    /// </summary>
    public IReadOnlyList<PointD> Path { get; init; } = Array.Empty<PointD>();

    public RectD Rect => RectD.FromCorners(P1, P2);

    public bool IsFreehand => Kind is AnnotationKind.Pen or AnnotationKind.Marker or AnnotationKind.Curve;

    /// <summary>Moves the whole thing (Body) or one end / corner. Pictures keep their proportions when a corner is dragged.</summary>
    public Annotation Moved(HandleTarget target, double dx, double dy)
    {
        var p1 = P1;
        var p2 = P2;
        if (target != HandleTarget.P2) p1 = new PointD(P1.X + dx, P1.Y + dy);
        if (target != HandleTarget.P1) p2 = new PointD(P2.X + dx, P2.Y + dy);
        if (Kind == AnnotationKind.Media && target != HandleTarget.Body && Image is { Height: > 0 } img)
            LockAspect(ref p1, ref p2, target, (double)img.Width / img.Height);
        return this with { P1 = p1, P2 = p2 };
    }

    /// <summary>Resizes the rectangle by the corner being dragged while keeping its width : height ratio.</summary>
    public static void LockAspect(ref PointD p1, ref PointD p2, HandleTarget moving, double aspect)
    {
        var fixedPt = moving == HandleTarget.P1 ? p2 : p1;
        var m = moving == HandleTarget.P1 ? p1 : p2;
        double dx = m.X - fixedPt.X, dy = m.Y - fixedPt.Y;
        double w = Math.Max(Math.Max(Math.Abs(dx), Math.Abs(dy) * aspect), 4);
        double h = w / aspect;
        var outPt = new PointD(fixedPt.X + (dx < 0 ? -w : w), fixedPt.Y + (dy < 0 ? -h : h));
        if (moving == HandleTarget.P1) p1 = outPt; else p2 = outPt;
    }

    /// <summary>A freehand drawing from raw points (image coordinates), or null if there is nothing to draw.</summary>
    public static Annotation? Freehand(AnnotationKind kind, IReadOnlyList<PointD> points, ColorRgba color, int level)
    {
        if (points.Count < 2) return null;
        double minX = points[0].X, maxX = minX, minY = points[0].Y, maxY = minY;
        foreach (var q in points)
        {
            minX = Math.Min(minX, q.X); maxX = Math.Max(maxX, q.X);
            minY = Math.Min(minY, q.Y); maxY = Math.Max(maxY, q.Y);
        }
        const double minSide = 8;                       // a straight stroke still needs a rectangle to live in
        if (maxX - minX < minSide) { var c = (minX + maxX) / 2; minX = c - minSide / 2; maxX = c + minSide / 2; }
        if (maxY - minY < minSide) { var c = (minY + maxY) / 2; minY = c - minSide / 2; maxY = c + minSide / 2; }
        double w = maxX - minX, h = maxY - minY;
        return new Annotation
        {
            Kind = kind,
            P1 = new PointD(minX, minY),
            P2 = new PointD(maxX, maxY),
            Color = color,
            Level = level,
            Path = points.Select(p => new PointD((p.X - minX) / w, (p.Y - minY) / h)).ToList(),
        };
    }
}
