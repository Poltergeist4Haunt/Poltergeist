using Poltergeist.Core;
using SkiaSharp;

static class T
{
    public static int Pass, Fail;
    public static void Check(string name, bool ok, string? detail = null)
    {
        if (ok) Pass++; else Fail++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail != null ? "  — " + detail : "")}");
    }
    public static bool Near(double a, double b, double tol = 1e-6) => Math.Abs(a - b) <= tol;
}

static class Program
{
    static string Out(string name) => Path.Combine(AppContext.BaseDirectory, name);

    static async Task<int> Main(string[] args)
    {
        string which = args.Length > 0 ? args[0] : "all";
        if (which == "icon") return MakeIcons(args.Length > 1 ? args[1] : ".");
        if (which is "all" or "model") ModelTests();
        if (which is "all" or "render") { RenderTests(); EffectTests(); }
        if (which is "all" or "video") await VideoTests.RunAsync();
        if (which is "all" or "haunt") await HauntTests.RunAsync();
        Console.WriteLine($"\n{T.Pass} passed, {T.Fail} failed");
        return T.Fail == 0 ? 0 : 1;
    }

    static void EffectTests()
    {
        Console.WriteLine("Image effects");
        var cfg = new AppSettings { FxOn = true, FxRound = true, FxRadius = 20, FxBorder = 4, FxShadow = true, FxShadowSize = 10, FxWatermark = "poltergeist" };
        using var src = new SkiaSharp.SKBitmap(200, 100);
        using (var c = new SkiaSharp.SKCanvas(src)) c.Clear(new SkiaSharp.SKColor(30, 120, 200));
        using var fx = ImageEffects.Apply(src, cfg);
        T.Check("shadow adds a margin around the shot", fx.Width == 200 + 40 && fx.Height == 100 + 40);
        T.Check("rounded corners cut the corner transparent", fx.GetPixel(20, 20).Alpha < 40);
        T.Check("the middle of the picture is kept", fx.GetPixel(fx.Width / 2, fx.Height / 2).Blue > 150);
        T.Check("effects off means nothing to do", !ImageEffects.Wanted(new AppSettings()));
    }

    /// <summary>Writes Poltergeist.ico (and PNG previews) from the ghost drawing in Core.</summary>
    static int MakeIcons(string dir)
    {
        Directory.CreateDirectory(dir);
        AppIcon.WriteIco(Path.Combine(dir, "Poltergeist.ico"));
        foreach (var (name, bmp) in new[]
        {
            ("app-256.png", AppIcon.Render(256)),
            ("tray-white-64.png", AppIcon.RenderGhost(64, SkiaSharp.SKColors.White)),
            ("tray-rec-64.png", AppIcon.RenderGhost(64, SkiaSharp.SKColors.White, true)),
        })
        {
            using (bmp) File.WriteAllBytes(Path.Combine(dir, name), SkiaSharp.SKImage.FromBitmap(bmp).Encode(SkiaSharp.SKEncodedImageFormat.Png, 100).ToArray());
        }
        Console.WriteLine("Wrote icons to " + Path.GetFullPath(dir));
        return 0;
    }

    // -------------------------------------------------------------------------------------------------------------------

    static void ModelTests()
    {
        Console.WriteLine("Timeline model");
        var p = new VideoProject(6);
        T.Check("starts as one clip covering everything", p.Segments.Count == 1 && p.OutputDuration == 6 && !p.NeedsRender);

        p.Cuts = new[] { new Cut(Guid.NewGuid(), 2, 4) };
        T.Check("a cut removes its stretch", T.Near(p.OutputDuration, 4) && p.NeedsRender);
        T.Check("cuts become removed clips", p.Segments.Count == 3 && p.Segments[1].Removed);
        T.Check("output time skips the cut", T.Near(p.OutputTime(1), 1) && T.Near(p.OutputTime(3), 2) && T.Near(p.OutputTime(5), 3));

        var speed = new VideoProject(6);
        speed.Speeds.Add(new SpeedRegion { Start = 1, End = 3, Rate = 2 });
        var pieces = speed.Pieces();
        T.Check("2x speed over 1–3 s", pieces.Count == 3 && T.Near(pieces[1].CompStart, 1) && T.Near(pieces[1].CompEnd, 2) && T.Near(speed.OutputDuration, 5));
        var map = new TimeMap(pieces);
        T.Check("output → source time through a speed change", T.Near(map.SourceTime(0.5), 0.5) && T.Near(map.SourceTime(1.5), 2.0) && T.Near(map.SourceTime(3.0), 4.0));
        T.Check("rate lookup", T.Near(speed.RateAt(2), 2) && T.Near(speed.RateAt(0.5), 1) && T.Near(speed.RateAt(4), 1));

        var combo = new VideoProject(6);
        combo.Cuts = new[] { new Cut(Guid.NewGuid(), 4, 5) };
        combo.Speeds.Add(new SpeedRegion { Start = 0, End = 1, Rate = 0.5 });
        T.Check("cut + slow motion → 6.00 s", T.Near(combo.OutputDuration, 6), $"{combo.OutputDuration:0.00}");

        var trimmed = new VideoProject(6) { TrimStart = 1, TrimEnd = 5 };
        T.Check("trim", T.Near(trimmed.OutputDuration, 4) && trimmed.IsTrimmed);

        // mutes follow cuts and speed
        var m = new VideoProject(6);
        m.Cuts = new[] { new Cut(Guid.NewGuid(), 2, 3) };
        m.Mutes.Add(new AudioMute { Role = AudioRole.Mic, Start = 1, End = 4 });
        var mr = m.MutedRanges(AudioRole.Mic, true);
        T.Check("a mute spanning a cut is split around it", mr.Count == 2 && T.Near(mr[0].Start, 1) && T.Near(mr[0].End, 2) && T.Near(mr[1].Start, 2) && T.Near(mr[1].End, 3));

        // objects
        var o = new VideoObject { Kind = AnnotationKind.Box, Start = 1, End = 4 };
        o.Keyframes.Add(new Keyframe(1, new PointD(40, 220), new PointD(120, 280)));
        o.Keyframes.Add(new Keyframe(3, new PointD(440, 220), new PointD(520, 280)));
        var g = o.GeometryAt(2);
        T.Check("keyframes glide linearly", T.Near(g.P1.X, 240) && T.Near(g.P2.X, 320));
        T.Check("holds before / after the keyframes", T.Near(o.GeometryAt(0).P1.X, 40) && T.Near(o.GeometryAt(3.9).P1.X, 440));
        T.Check("visibility is half-open", o.IsVisible(1) && o.IsVisible(3.99) && !o.IsVisible(4) && !o.IsVisible(0.99));
        o.FadeIn = 0.5; o.FadeOut = 0.5;
        T.Check("fades", T.Near(o.OpacityAt(1.25), 0.5) && T.Near(o.OpacityAt(2), 1) && T.Near(o.OpacityAt(3.75), 0.5));
        var moved = o.Moved(HandleTarget.Body, 10, 5, 2, animate: true);
        T.Check("dragging with Animate on adds a keyframe", moved.Keyframes.Count == 3 && T.Near(moved.GeometryAt(2).P1.X, 250));
        var shifted = o.Moved(HandleTarget.Body, 10, 5, 2, animate: false);
        T.Check("dragging with Animate off shifts the whole path", shifted.Keyframes.Count == 2 && T.Near(shifted.Keyframes[0].P1.X, 50) && T.Near(shifted.Keyframes[1].P1.X, 450));

        // zoom
        var z = new VideoProject(6);
        var zo = new VideoObject { Kind = AnnotationKind.Zoom, Start = 1, End = 4, FadeIn = 0.5, FadeOut = 0.5 };
        zo.Keyframes.Add(new Keyframe(1, new PointD(320, 180), new PointD(640, 360)));
        z.Objects.Add(zo);
        var size = new SizeD(640, 360);
        T.Check("no zoom outside its time", z.ZoomRect(0.5, size) == null && z.ZoomRect(4.5, size) == null);
        var full = z.ZoomRect(2.5, size)!.Value;
        T.Check("fully zoomed on the bottom-right quarter", T.Near(full.X, 320) && T.Near(full.Y, 180) && T.Near(full.Width, 320) && T.Near(full.Height, 180));
        var mid = z.ZoomRect(1.25, size)!.Value;
        T.Check("eases in (halfway through the ease = halfway zoomed)", mid.Width < 640 && mid.Width > 320 && T.Near(mid.Width, 480, 1));

        // freehand
        var stroke = Annotation.Freehand(AnnotationKind.Pen, new[] { new PointD(10, 50), new PointD(200, 50) }, Palette.Red, 2)!;
        T.Check("a straight stroke still gets a usable rectangle", stroke.P2.Y - stroke.P1.Y >= 8 && stroke.Path.All(q => q.X is >= 0 and <= 1 && q.Y is >= 0 and <= 1));
        T.Check("one point makes no stroke", Annotation.Freehand(AnnotationKind.Pen, new[] { new PointD(1, 1) }, Palette.Red, 2) == null);
        var resized = stroke.Moved(HandleTarget.P2, 100, 0);
        T.Check("resizing the rectangle rescales the stroke", T.Near(resized.Rect.Width, stroke.Rect.Width + 100));

        // picture keeps its proportions when a corner is dragged
        using var bmp = new SKBitmap(100, 50);
        var pic = new Annotation { Kind = AnnotationKind.Media, P1 = new PointD(0, 0), P2 = new PointD(100, 50), Image = SKImage.FromBitmap(bmp) };
        var big = pic.Moved(HandleTarget.P2, 300, 20);
        T.Check("picture corner drag keeps 2:1", T.Near(big.Rect.Width / big.Rect.Height, 2, 1e-9), $"{big.Rect.Width}x{big.Rect.Height}");
    }

    // -------------------------------------------------------------------------------------------------------------------

    static void RenderTests()
    {
        Console.WriteLine("\nDrawing engine");
        const int W = 1400, H = 800;
        using var baseBmp = new SKBitmap(W, H);
        using (var c = new SKCanvas(baseBmp))
        {
            using var grad = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(W, H), new[] { new SKColor(30, 40, 90), new SKColor(220, 120, 170) }, SKShaderTileMode.Clamp);
            c.DrawRect(0, 0, W, H, new SKPaint { Shader = grad });
            using var font = new SKFont(SKTypeface.FromFamilyName("Helvetica Neue", SKFontStyle.Bold), 56);
            using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
            c.DrawText("user: nico_secret", 120, 150, font, white);
            c.DrawText("token: haunt_aB3dE9xYz", 120, 230, font, white);
            c.DrawText("Important text to highlight", 120, 450, font, white);
        }
        using var baseImg = SKImage.FromBitmap(baseBmp);
        var r = new AnnotationRenderer(baseImg);

        var wave = Enumerable.Range(0, 61).Select(i => { double t = i / 60.0; return new PointD(100 + t * 500, 640 + Math.Sin(t * 6 * Math.PI) * 40); }).ToList();
        var marker = Enumerable.Range(0, 21).Select(i => new PointD(110 + i * 41, 420 + (i % 2) * 4)).ToList();
        var arc = Enumerable.Range(0, 41).Select(i => { double ang = Math.PI * (1.1 + 0.8 * i / 40.0); return new PointD(1000 + Math.Cos(ang) * 200, 650 + Math.Sin(ang) * 200); }).ToList();
        var list = new List<Annotation>
        {
            new() { Kind = AnnotationKind.Blur, P1 = new PointD(100, 90), P2 = new PointD(780, 175), Level = 2, Soft = false },
            new() { Kind = AnnotationKind.Blur, P1 = new PointD(100, 180), P2 = new PointD(780, 260), Level = 2, Soft = true },
            new() { Kind = AnnotationKind.Arrow, P1 = new PointD(900, 120), P2 = new PointD(1250, 300), Color = Palette.Yellow, Level = 3 },
            new() { Kind = AnnotationKind.Box, P1 = new PointD(880, 340), P2 = new PointD(1100, 440), Color = Palette.Red },
            new() { Kind = AnnotationKind.Circle, P1 = new PointD(1130, 340), P2 = new PointD(1330, 440), Color = Palette.Green },
            new() { Kind = AnnotationKind.Text, P1 = new PointD(900, 480), Text = "Look here!", Color = Palette.White, Level = 3 },
            Annotation.Freehand(AnnotationKind.Marker, marker, Palette.Yellow, 2)!,
            Annotation.Freehand(AnnotationKind.Pen, wave, Palette.Red, 2)!,
            Annotation.Freehand(AnnotationKind.Curve, arc, Palette.Accent, 3)!,
        };
        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        r.DrawAll(surface.Canvas, list);
        using var snap = surface.Snapshot();
        using var png = snap.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(Out("render-test.png"), png.ToArray());
        Console.WriteLine($"  wrote {Out("render-test.png")}");

        using var bmpOut = SKBitmap.FromImage(snap);
        SKColor Px(int x, int y) => bmpOut.GetPixel(x, y);
        T.Check("pen line is drawn in its colour", Px(100, 640).Red > 200 && Px(100, 640).Green < 120);
        T.Check("untouched background stays", Px(700, 760).Equals(baseBmp.GetPixel(700, 760)));
        // blur: the region must no longer contain the sharp text (high local contrast removed)
        double Contrast(SKBitmap b, int x0, int y0, int w, int h)
        {
            double sum = 0; int n = 0;
            for (int y = y0; y < y0 + h; y += 2) for (int x = x0 + 1; x < x0 + w; x += 2)
            {
                var a = b.GetPixel(x, y); var c = b.GetPixel(x - 1, y);
                sum += Math.Abs(a.Red - c.Red) + Math.Abs(a.Green - c.Green); n++;
            }
            return sum / n;
        }
        double before = Contrast(baseBmp, 120, 100, 650, 70), px = Contrast(bmpOut, 120, 100, 650, 70);
        double soft0 = Contrast(baseBmp, 120, 190, 650, 60), soft = Contrast(bmpOut, 120, 190, 650, 60);
        T.Check("pixelated region lost the sharp text", px < before * 0.5, $"{before:0.0} → {px:0.0}");
        T.Check("soft-blurred region lost the sharp text", soft < soft0 * 0.3, $"{soft0:0.0} → {soft:0.0}");
        T.Check("hit-testing: on the stroke / off it", r.Hits(list[7], new PointD(100, 640), 6) && !r.Hits(list[7], new PointD(400, 760), 6));
        T.Check("text measurement", r.MeasureText("Look here!", 3).Width > 100);
    }
}
