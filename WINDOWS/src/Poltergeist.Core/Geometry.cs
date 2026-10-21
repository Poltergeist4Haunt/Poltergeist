namespace Poltergeist.Core;

public readonly record struct PointD(double X, double Y)
{
    public static PointD operator +(PointD a, PointD b) => new(a.X + b.X, a.Y + b.Y);
    public static PointD operator -(PointD a, PointD b) => new(a.X - b.X, a.Y - b.Y);
    public double DistanceTo(PointD o) => Math.Sqrt((X - o.X) * (X - o.X) + (Y - o.Y) * (Y - o.Y));
    public static PointD Lerp(PointD a, PointD b, double f) => new(a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f);
}

public readonly record struct SizeD(double Width, double Height);

public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double MinX => X;
    public double MinY => Y;
    public double MaxX => X + Width;
    public double MaxY => Y + Height;
    public double MidX => X + Width / 2;
    public double MidY => Y + Height / 2;

    /// <summary>The rectangle with these two opposite corners, in either order.</summary>
    public static RectD FromCorners(PointD a, PointD b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    public bool Contains(PointD p) => p.X >= MinX && p.X <= MaxX && p.Y >= MinY && p.Y <= MaxY;
    public RectD Inflate(double d) => new(X - d, Y - d, Width + 2 * d, Height + 2 * d);

    public RectD Intersect(RectD o)
    {
        double x0 = Math.Max(MinX, o.MinX), y0 = Math.Max(MinY, o.MinY);
        double x1 = Math.Min(MaxX, o.MaxX), y1 = Math.Min(MaxY, o.MaxY);
        return x1 > x0 && y1 > y0 ? new RectD(x0, y0, x1 - x0, y1 - y0) : new RectD(x0, y0, 0, 0);
    }
}

/// <summary>An RGBA colour. Haunt's palette lives in <see cref="Palette"/>.</summary>
public readonly record struct ColorRgba(byte R, byte G, byte B, byte A = 255)
{
    public static ColorRgba FromHex(uint rgb, byte a = 255) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, a);
    public ColorRgba WithAlpha(byte a) => this with { A = a };
    public string ToHex() => $"#{R:x2}{G:x2}{B:x2}";

    public static bool TryParse(string? s, out ColorRgba c)
    {
        c = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim().TrimStart('#');
        if (s.Length != 6 || !uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var v)) return false;
        c = FromHex(v);
        return true;
    }
}

public static class Palette
{
    public static readonly ColorRgba Accent = ColorRgba.FromHex(0xf27db3);
    public static readonly ColorRgba Red = ColorRgba.FromHex(0xff4d4d);
    public static readonly ColorRgba Yellow = ColorRgba.FromHex(0xffd60a);
    public static readonly ColorRgba Green = ColorRgba.FromHex(0x30d158);
    public static readonly ColorRgba White = ColorRgba.FromHex(0xffffff);
    public static readonly ColorRgba Black = ColorRgba.FromHex(0x000000);
    public static readonly ColorRgba Amber = ColorRgba.FromHex(0xffb020);
    public static readonly ColorRgba Blue = ColorRgba.FromHex(0x5ac8fa);

    /// <summary>The six preset colours of the editors, in order.</summary>
    public static readonly ColorRgba[] Presets = { Accent, Red, Yellow, Green, White, Black };
}
