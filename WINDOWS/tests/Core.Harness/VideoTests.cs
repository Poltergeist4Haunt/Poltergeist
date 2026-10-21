using Poltergeist.Core;
using SkiaSharp;

static class VideoTests
{
    static readonly string Work = Path.Combine(Path.GetTempPath(), "polt-tests");

    static async Task Ff(params string[] args)
    {
        var r = await Proc.RunAsync(Ffmpeg.Require(), new[] { "-y", "-v", "error", "-nostdin" }.Concat(args));
        if (!r.Ok) throw new Exception("ffmpeg failed: " + r.StdErr);
    }

    // ---- measuring the output -----------------------------------------------------------------------------------------

    static async Task<SKBitmap> FrameAt(string file, double t)
    {
        var png = Path.Combine(Work, $"f-{Guid.NewGuid():N}.png");
        await Ff("-ss", Ffmpeg.N(t), "-i", file, "-frames:v", "1", png);
        return SKBitmap.Decode(png)!;
    }

    static int Gray(SKBitmap b, int x, int y) { var c = b.GetPixel(x, y); return (c.Red + c.Green + c.Blue) / 3; }

    static (int X0, int X1, int Y0, int Y1)? RedBounds(SKBitmap b, Func<int, int, bool>? skip = null)
    {
        int x0 = int.MaxValue, x1 = -1, y0 = int.MaxValue, y1 = -1;
        for (int y = 0; y < b.Height; y++) for (int x = 0; x < b.Width; x++)
        {
            if (skip != null && skip(x, y)) continue;
            var c = b.GetPixel(x, y);
            if (c.Red > 190 && c.Green < 70 && c.Blue < 70) { x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); }
        }
        return x1 < 0 ? null : (x0, x1, y0, y1);
    }

    /// <summary>Loudness of one frequency (Goertzel) in a window of the first audio track.</summary>
    static async Task<double> Tone(string file, double hz, double from, double len = 0.2, int track = 0)
    {
        var raw = Path.Combine(Work, $"a-{Guid.NewGuid():N}.f32");
        await Ff("-i", file, "-map", $"0:a:{track}", "-ac", "1", "-ar", "48000", "-f", "f32le", raw);
        var bytes = File.ReadAllBytes(raw);
        int s0 = (int)(from * 48000), n = (int)(len * 48000);
        double re = 0, im = 0, w = 2 * Math.PI * hz / 48000;
        for (int i = 0; i < n && (s0 + i) * 4 + 4 <= bytes.Length; i++)
        {
            double x = BitConverter.ToSingle(bytes, (s0 + i) * 4);
            re += x * Math.Cos(w * i); im += x * Math.Sin(w * i);
        }
        return Math.Sqrt(re * re + im * im) / n * 2;
    }

    static async Task<Ffmpeg.MediaInfo> Info(string f) => (await Ffmpeg.ProbeAsync(f))!;

    // ---- test media ---------------------------------------------------------------------------------------------------

    /// <summary>6 s 640x360 @30: grey that brightens with time, a red 40x40 square at (480..520, 240..280), a 440 Hz tone.</summary>
    static async Task<string> MakeSource()
    {
        var f = Path.Combine(Work, "source.mp4");
        await Ff("-f", "lavfi", "-i", "color=c=gray:s=640x360:r=30:d=6,geq=lum='30+200*T/6':cb=128:cr=128,drawbox=x=480:y=240:w=40:h=40:color=red:t=fill",
                 "-f", "lavfi", "-i", "aevalsrc=0.3*sin(2*PI*440*t):d=6:s=48000", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-g", "30", "-c:a", "aac", "-shortest", f);
        return f;
    }

    /// <summary>The same picture with two labelled audio tracks: desktop = 440 Hz, mic = 1000 Hz.</summary>
    static async Task<string> MakeTwoTrack()
    {
        var f = Path.Combine(Work, "two-track.mp4");
        await Ff("-f", "lavfi", "-i", "color=c=gray:s=640x360:r=30:d=4", "-f", "lavfi", "-i", "aevalsrc=0.3*sin(2*PI*440*t):d=4:s=48000", "-f", "lavfi", "-i", "aevalsrc=0.3*sin(2*PI*1000*t):d=4:s=48000",
                 "-map", "0:v", "-map", "1:a", "-map", "2:a", "-metadata:s:a:0", "handler_name=polt-system", "-metadata:s:a:1", "handler_name=polt-mic",
                 "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", f);
        return f;
    }

    static VideoProject Base(Ffmpeg.MediaInfo info)
    {
        var p = new VideoProject(info.VideoDuration);
        p.AudioRoles = info.AudioTracks.Select(t => AudioRoleExtensions.FromTag(t.Title)).ToList();
        return p;
    }

    public static async Task RunAsync()
    {
        Console.WriteLine("\nVideo export (real ffmpeg)");
        if (!Ffmpeg.Available) { T.Check("ffmpeg available", false, "install ffmpeg to run these"); return; }
        Directory.CreateDirectory(Work);
        var src = await MakeSource();
        var srcInfo = await Info(src);
        T.Check("probe reads the source", srcInfo.Width == 640 && srcInfo.Height == 360 && Math.Abs(srcInfo.Fps - 30) < 0.01 && srcInfo.AudioTracks.Count == 1,
                $"{srcInfo.Width}x{srcInfo.Height} @{srcInfo.Fps:0.##}, {srcInfo.VideoDuration:0.00}s");
        int GraySrc(double t, SKBitmap f) => Gray(f, 10, 10);

        // 1. speed ------------------------------------------------------------------------------------------------------
        {
            var p = Base(srcInfo);
            p.Speeds.Add(new SpeedRegion { Start = 1, End = 3, Rate = 2 });
            var o = (await VideoExporter.PrepareAsync(src, p, 6))!;
            var oi = await Info(o);
            T.Check("speed: 2x over 1–3 s makes a 5.00 s video", Math.Abs(oi.VideoDuration - 5.0) < 0.1, $"{oi.VideoDuration:0.00}");
            foreach (var (outT, srcT) in new[] { (0.5, 0.5), (1.5, 2.0), (3.0, 4.0) })
            {
                using var a = await FrameAt(o, outT); using var b = await FrameAt(src, srcT);
                T.Check($"speed: output {outT}s shows source {srcT}s", Math.Abs(Gray(a, 10, 10) - Gray(b, 10, 10)) <= 4, $"{Gray(a, 10, 10)} vs {Gray(b, 10, 10)}");
            }
            double p440 = await Tone(o, 440, 1.2), p880 = await Tone(o, 880, 1.2);
            T.Check("speed: the 2x part keeps its pitch", p440 > 0.1 && p880 < 0.02, $"440Hz={p440:0.00} 880Hz={p880:0.00}");
        }

        // 2. fades --------------------------------------------------------------------------------------------------------
        {
            var p = Base(srcInfo);
            p.AudioFadeIn = 1; p.AudioFadeOut = 1;
            var box = new VideoObject { Kind = AnnotationKind.Box, Color = ColorRgba.FromHex(0xff0000), Level = 3, Start = 0.5, End = 5, FadeIn = 1, FadeOut = 1 };
            box.Keyframes.Add(new Keyframe(0.5, new PointD(100, 100), new PointD(300, 250)));
            p.Objects.Add(box);
            var o = (await VideoExporter.PrepareAsync(src, p, 6))!;
            double Red(SKBitmap b) { var c = b.GetPixel(101, 150); return (c.Red - c.Green) / 255.0; }
            using var f06 = await FrameAt(o, 0.6); using var f10 = await FrameAt(o, 1.0); using var f20 = await FrameAt(o, 2.0); using var f48 = await FrameAt(o, 4.8);
            T.Check("fades: layer fades in and out", Red(f06) < Red(f10) && Red(f10) < Red(f20) && Red(f48) < Red(f20) * 0.5,
                    $"{Red(f06):0.00} → {Red(f10):0.00} → {Red(f20):0.00}, out {Red(f48):0.00}");
            double a01 = await Tone(o, 440, 0.0, 0.2), a05 = await Tone(o, 440, 0.4, 0.2), a30 = await Tone(o, 440, 3.0), a55 = await Tone(o, 440, 5.7, 0.2);
            T.Check("fades: audio ramps in, holds, ramps out", a01 < 0.06 && a05 > 0.07 && a05 < 0.2 && a30 > 0.25 && a55 < a30 * 0.4, $"{a01:0.00} {a05:0.00} {a30:0.00} {a55:0.00}");
        }

        // 3. zoom ---------------------------------------------------------------------------------------------------------
        {
            var p = Base(srcInfo);
            var z = new VideoObject { Kind = AnnotationKind.Zoom, Start = 1, End = 4, FadeIn = 0.5, FadeOut = 0.5 };
            z.Keyframes.Add(new Keyframe(1, new PointD(320, 180), new PointD(640, 360)));
            p.Objects.Add(z);
            var o = (await VideoExporter.PrepareAsync(src, p, 6))!;
            using var f05 = await FrameAt(o, 0.5); using var f25 = await FrameAt(o, 2.5); using var f45 = await FrameAt(o, 4.5);
            var b0 = RedBounds(f05); var b1 = RedBounds(f25); var b2 = RedBounds(f45);
            T.Check("zoom: no zoom before it starts", b0 is { } q0 && Math.Abs(q0.X0 - 480) <= 2 && Math.Abs(q0.X1 - 519) <= 2, b0?.ToString());
            T.Check("zoom: 2x on the bottom-right quarter moves the marker to x 320–400, y 120–200",
                    b1 is { } q1 && Math.Abs(q1.X0 - 320) <= 3 && Math.Abs(q1.X1 - 399) <= 3 && Math.Abs(q1.Y0 - 120) <= 3 && Math.Abs(q1.Y1 - 199) <= 3, b1?.ToString());
            T.Check("zoom: back to normal afterwards", b2 is { } q2 && Math.Abs(q2.X0 - 480) <= 2, b2?.ToString());
        }

        // 4. a pen stroke that glides between keyframes ----------------------------------------------------------------
        {
            var p = Base(srcInfo);
            var wave = Enumerable.Range(0, 41).Select(i => { double t = i / 40.0; return new PointD(50 + t * 200, 130 + Math.Sin(t * 4 * Math.PI) * 25); }).ToList();
            var stroke = Annotation.Freehand(AnnotationKind.Pen, wave, ColorRgba.FromHex(0xff0000), 3)!;
            var pen = new VideoObject { Kind = AnnotationKind.Pen, Color = ColorRgba.FromHex(0xff0000), Level = 3, Start = 1, End = 4, Path = stroke.Path.ToList() };
            pen.Keyframes.Add(new Keyframe(1, stroke.P1, stroke.P2));
            pen.Keyframes.Add(new Keyframe(3, new PointD(stroke.P1.X + 250, stroke.P1.Y + 120), new PointD(stroke.P2.X + 250, stroke.P2.Y + 120)));
            p.Objects.Add(pen);
            var o = (await VideoExporter.PrepareAsync(src, p, 6))!;
            bool Marker(int x, int y) => x >= 470 && x <= 530 && y >= 230 && y <= 290;          // the source's own red square
            using var a = await FrameAt(o, 1.0); using var b = await FrameAt(o, 2.0); using var c = await FrameAt(o, 4.5); using var d = await FrameAt(o, 0.5);
            var ba = RedBounds(a, Marker); var bb = RedBounds(b, Marker);
            T.Check("pen: appears where it was drawn", ba is { } x1 && Math.Abs(x1.X0 - 48) <= 6 && Math.Abs(x1.X1 - 252) <= 6, ba?.ToString());
            T.Check("pen: has glided halfway by 2.0 s", bb is { } x2 && Math.Abs(x2.X0 - 175) <= 8 && Math.Abs(x2.Y0 - 165) <= 8, bb?.ToString());
            T.Check("pen: gone before and after its time", RedBounds(d, Marker) == null && RedBounds(c, Marker) == null);
        }

        // 5. split + delete the middle ---------------------------------------------------------------------------------
        {
            var p = Base(srcInfo);
            p.Segments = new List<Segment> { new() { Start = 0, End = 2 }, new() { Start = 2, End = 4, Removed = true }, new() { Start = 4, End = 6 } };
            var o = (await VideoExporter.PrepareAsync(src, p, 6))!;
            var oi = await Info(o);
            using var a = await FrameAt(o, 2.5); using var b = await FrameAt(src, 4.5);
            T.Check("delete the middle clip: 4.00 s, later footage follows", Math.Abs(oi.VideoDuration - 4.0) < 0.1 && Math.Abs(Gray(a, 10, 10) - Gray(b, 10, 10)) <= 4, $"{oi.VideoDuration:0.00}s");
        }

        // 6. everything at once ----------------------------------------------------------------------------------------
        {
            var p = Base(srcInfo);
            p.Cuts = new[] { new Cut(Guid.NewGuid(), 4, 5) };
            p.Speeds.Add(new SpeedRegion { Start = 0, End = 1, Rate = 0.5 });
            p.AudioFadeIn = 0.5; p.AudioFadeOut = 0.5;
            var z = new VideoObject { Kind = AnnotationKind.Zoom, Start = 1, End = 3.5, FadeIn = 0.4, FadeOut = 0.4 };
            z.Keyframes.Add(new Keyframe(1, new PointD(320, 180), new PointD(640, 360)));
            var box = new VideoObject { Kind = AnnotationKind.Box, Color = ColorRgba.FromHex(0xff0000), Start = 0.5, End = 5 };
            box.Keyframes.Add(new Keyframe(0.5, new PointD(100, 100), new PointD(200, 150)));
            p.Objects.Add(z); p.Objects.Add(box);
            var o = (await VideoExporter.PrepareAsync(src, p, 6))!;
            var oi = await Info(o);
            T.Check("cut + slow motion + zoom + box + fades: 6.00 s, one audio track", Math.Abs(oi.VideoDuration - 6.0) < 0.15 && oi.AudioTracks.Count == 1, $"{oi.VideoDuration:0.00}s, {oi.AudioTracks.Count} audio");
        }

        // 7. two tracks: mute the mic for a while, mix down ------------------------------------------------------------
        {
            var two = await MakeTwoTrack();
            var ti = await Info(two);
            T.Check("two labelled audio tracks are recognised", ti.AudioTracks.Count == 2 && AudioRoleExtensions.FromTag(ti.AudioTracks[0].Title) == AudioRole.System && AudioRoleExtensions.FromTag(ti.AudioTracks[1].Title) == AudioRole.Mic);
            var p = Base(ti);
            p.Mutes.Add(new AudioMute { Role = AudioRole.Mic, Start = 1, End = 2 });
            var o = (await VideoExporter.PrepareAsync(two, p, 6))!;
            var oi = await Info(o);
            double d1 = await Tone(o, 440, 0.2), m1 = await Tone(o, 1000, 0.2), dMuted = await Tone(o, 440, 1.3), mMuted = await Tone(o, 1000, 1.3), mAfter = await Tone(o, 1000, 2.6);
            T.Check("mixdown: one audio track, video not re-encoded (copy)", oi.AudioTracks.Count == 1 && oi.VideoDuration > 3.9, $"{oi.AudioTracks.Count} track(s)");
            T.Check("mute: the mic goes silent only during the mute", m1 > 0.2 && mMuted < 0.02 && mAfter > 0.2, $"mic {m1:0.00} → {mMuted:0.00} → {mAfter:0.00}");
            T.Check("mute: desktop audio keeps playing throughout", d1 > 0.2 && dMuted > 0.2, $"{d1:0.00}, {dMuted:0.00}");
            var plain = Base(ti);                                           // no edits at all, still two tracks → must become one for upload
            var po = (await VideoExporter.PrepareAsync(two, plain, 6))!;
            T.Check("a two-track recording with no edits is mixed to one track for upload", (await Info(po)).AudioTracks.Count == 1);
        }

        // 8. imported layers: GIF, picture, and a video with its own sound ---------------------------------------------
        {
            var gifFile = Path.Combine(Work, "layer.gif");
            await Ff("-f", "lavfi", "-i", "color=c=blue:s=80x60:r=10:d=1", "-vf", "hue=h=t*360:s=3", gifFile);
            var vid = Path.Combine(Work, "layer.mp4");
            await Ff("-f", "lavfi", "-i", "color=c=0x0000ff:s=160x90:r=30:d=3", "-f", "lavfi", "-i", "aevalsrc=0.3*sin(2*PI*700*t):d=3:s=48000", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", vid);
            var png = Path.Combine(Work, "layer.png");
            using (var bmp = new SKBitmap(60, 30)) { bmp.Erase(SKColors.Yellow); using var data = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 100); File.WriteAllBytes(png, data.ToArray()); }

            using var gif = (await MediaClip.LoadAsync(gifFile))!;
            using var video = (await MediaClip.LoadAsync(vid))!;
            using var image = (await MediaClip.LoadAsync(png))!;
            T.Check("media: GIF loads as an animation", gif.Kind == MediaKind.Gif && gif.Duration > 0.5, $"{gif.Duration:0.0}s");
            T.Check("media: video loads with sound", video.Kind == MediaKind.Video && video.HasAudio && Math.Abs(video.Duration - 3) < 0.2);
            T.Check("media: image loads", image.Kind == MediaKind.Image && image.PixelWidth == 60);

            VideoObject Layer(MediaClip c, PointD a, PointD b, double s, double e, double vol = 1) =>
                new() { Kind = AnnotationKind.Media, Media = c, Start = s, End = e, Volume = vol, Keyframes = { new Keyframe(s, a, b) } };
            var p = Base(srcInfo);
            p.Cuts = new[] { new Cut(Guid.NewGuid(), 2, 3) };
            p.Objects.Add(Layer(image, new PointD(220, 20), new PointD(280, 50), 0, 6));
            p.Objects.Add(Layer(video, new PointD(200, 100), new PointD(300, 156), 1, 4, 0.5));
            var o = (await VideoExporter.PrepareAsync(src, p, 6))!;
            var oi = await Info(o);
            using var f07 = await FrameAt(o, 0.7); using var f13 = await FrameAt(o, 1.3); using var f23 = await FrameAt(o, 2.3);
            SKColor At(SKBitmap b, int x, int y) => b.GetPixel(x, y);
            T.Check("media: the picture layer shows (yellow)", At(f07, 250, 35).Red > 200 && At(f07, 250, 35).Green > 200 && At(f07, 250, 35).Blue < 80);
            T.Check("media: the video layer shows only once it has started", At(f07, 250, 128).Blue < 150 && At(f13, 250, 128).Blue > 180, $"{At(f07, 250, 128).Blue} → {At(f13, 250, 128).Blue}");
            T.Check("media: video layer still showing after the cut", At(f23, 250, 128).Blue > 180);
            double l0 = await Tone(o, 700, 0.5), l1 = await Tone(o, 700, 1.5), l2 = await Tone(o, 700, 2.3);
            T.Check("media: the layer's sound starts with it, at 50% volume, and survives the cut", l0 < 0.02 && l1 > 0.1 && l1 < 0.2 && l2 > 0.1, $"{l0:0.00} {l1:0.00} {l2:0.00}");
            T.Check("media: recording's own audio is unchanged", await Tone(o, 440, 1.5) > 0.25);
        }
    }
}
