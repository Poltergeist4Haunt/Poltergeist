using System.Runtime.InteropServices;
using System.Text;
using SkiaSharp;

namespace Poltergeist.Core;

public sealed class ExportException : Exception
{
    public ExportException(string message) : base(message) { }
}

/// <summary>
/// Turns a <see cref="VideoProject"/> into a finished MP4: <see cref="PrepareAsync"/> picks the cheapest way that still does
/// everything asked (copy, trim, audio-only remux, or a full render).
/// </summary>
public static class VideoExporter
{
    static string NewWorkDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "poltergeist-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// The file to upload / copy for this project: the original if nothing changed (returns null), otherwise a new file.
    /// Plain trims and audio-only changes don't re-encode the video.
    /// </summary>
    public static async Task<string?> PrepareAsync(string source, VideoProject project, double bitrateMbps,
                                                   Action<double>? progress = null, CancellationToken ct = default)
    {
        if (project.NeedsRender) return await RenderAsync(source, project, bitrateMbps, progress, ct);
        if (project.NeedsAudioMix)
            return project.IsTrimmed ? await RenderAsync(source, project, bitrateMbps, progress, ct)     // keeps audio and video in step
                                     : await RemuxWithMixedAudioAsync(source, project, progress, ct);
        if (project.IsTrimmed) return await TrimCopyAsync(source, project.TrimStart, project.TrimEnd, ct);
        return null;
    }

    // ---- plain trim (no re-encode) -----------------------------------------------------------------------------------

    public static async Task<string> TrimCopyAsync(string source, double start, double end, CancellationToken ct = default)
    {
        var outFile = Path.Combine(NewWorkDir(), Path.GetFileName(source));
        var r = await Proc.RunAsync(Ffmpeg.Require(), new[]
        {
            "-y", "-v", "error", "-nostdin", "-ss", Ffmpeg.N(start), "-to", Ffmpeg.N(end), "-i", source,
            "-c", "copy", "-map", "0", "-avoid_negative_ts", "make_zero", "-movflags", "+faststart", outFile,
        }, ct);
        if (!r.Ok || !File.Exists(outFile)) throw new ExportException("Couldn't trim the video. " + Tail(r.StdErr));
        return outFile;
    }

    // ---- several audio tracks → one, video copied -------------------------------------------------------------------

    public static async Task<string> RemuxWithMixedAudioAsync(string source, VideoProject project, Action<double>? progress, CancellationToken ct)
    {
        var info = await Ffmpeg.ProbeAsync(source, ct) ?? throw new ExportException("Couldn't read the recording.");
        var dir = NewWorkDir();
        var audioPath = Path.Combine(dir, "audio.f32");
        var outFile = Path.Combine(dir, Path.GetFileName(source));
        long samples = await AudioProcessor.RenderAsync(source, info, project, audioPath, p => progress?.Invoke(p * 0.8), ct);
        var args = new List<string> { "-y", "-v", "error", "-nostdin", "-i", source };
        if (samples > 0) args.AddRange(new[] { "-f", "f32le", "-ar", "48000", "-ac", "2", "-i", audioPath });
        args.AddRange(new[] { "-map", "0:v", "-c:v", "copy" });
        if (samples > 0) args.AddRange(new[] { "-map", "1:a", "-c:a", "aac", "-b:a", "160k", "-shortest" });
        args.AddRange(new[] { "-movflags", "+faststart", outFile });
        var r = await Proc.RunAsync(Ffmpeg.Require(), args, ct);
        TryDelete(audioPath);
        if (!r.Ok || !File.Exists(outFile)) throw new ExportException("Couldn't mix the audio. " + Tail(r.StdErr));
        progress?.Invoke(1);
        return outFile;
    }

    // ---- full render -------------------------------------------------------------------------------------------------

    static string VideoGraph(IReadOnlyList<Piece> pieces, double fps, bool bgra)
    {
        var g = new StringBuilder();
        string f = Ffmpeg.N(fps);
        for (int k = 0; k < pieces.Count; k++)
            g.Append($"[0:v]trim=start={Ffmpeg.N(pieces[k].SrcStart)}:end={Ffmpeg.N(pieces[k].SrcEnd)}," +
                     $"setpts=(PTS-STARTPTS)/{Ffmpeg.N(pieces[k].Rate)},fps={f}[v{k}];");
        if (pieces.Count == 1) g.Append("[v0]null[vc];");
        else
        {
            for (int k = 0; k < pieces.Count; k++) g.Append($"[v{k}]");
            g.Append($"concat=n={pieces.Count}:v=1:a=0[vc];");
        }
        g.Append($"[vc]fps={f}{(bgra ? ",format=bgra" : "")}[v]");
        return g.ToString();
    }

    /// <summary>Re-encodes with cuts, speed changes, annotations, blur, zoom, imported layers and the processed audio.</summary>
    public static async Task<string> RenderAsync(string source, VideoProject project, double bitrateMbps,
                                                 Action<double>? progress, CancellationToken ct)
    {
        var info = await Ffmpeg.ProbeAsync(source, ct) ?? throw new ExportException("Couldn't read the recording.");
        var pieces = project.Pieces();
        if (pieces.Count == 0) throw new ExportException("Nothing is left after the cuts.");
        double total = project.OutputDuration;
        double fps = Math.Min(60, Math.Max(10, Math.Round(info.Fps, 3)));
        int w = info.Width, h = info.Height;
        var dir = NewWorkDir();
        var audioPath = Path.Combine(dir, "audio.f32");
        var outFile = Path.Combine(dir, Path.GetFileNameWithoutExtension(source) + ".mp4");
        var ff = Ffmpeg.Require();
        var encoder = await Encoders.PickH264Async();

        long samples = await AudioProcessor.RenderAsync(source, info, project, audioPath, p => progress?.Invoke(p * 0.1), ct);
        var audioInput = samples > 0 ? new[] { "-f", "f32le", "-ar", "48000", "-ac", "2", "-i", audioPath } : Array.Empty<string>();
        void AddOutputs(List<string> a, int audioInputIndex)
        {
            a.AddRange(Encoders.VideoArgs(encoder, bitrateMbps, fps));
            if (samples > 0) a.AddRange(new[] { "-map", $"{audioInputIndex}:a", "-c:a", "aac", "-b:a", "160k" });
            a.AddRange(new[] { "-movflags", "+faststart", outFile });
        }

        bool perFrame = project.Objects.Count > 0;
        if (!perFrame)
        {
            // cuts and speed only: one ffmpeg does it all
            var args = new List<string> { "-y", "-v", "error", "-nostdin", "-i", source };
            args.AddRange(audioInput);
            args.AddRange(new[] { "-filter_complex", VideoGraph(pieces, fps, false), "-map", "[v]" });
            AddOutputs(args, 1);
            var r = await Proc.RunAsync(ff, args, ct);
            TryDelete(audioPath);
            if (!r.Ok || !File.Exists(outFile)) throw new ExportException("Couldn't render the video. " + Tail(r.StdErr));
            progress?.Invoke(1);
            return outFile;
        }

        // annotations: decode → draw every frame ourselves → encode
        var decoder = ToolProcess.Start(ff, new[]
        {
            "-v", "error", "-nostdin", "-i", source, "-filter_complex", VideoGraph(pieces, fps, true),
            "-map", "[v]", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1",
        });
        var encArgs = new List<string> { "-y", "-v", "error", "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{w}x{h}", "-framerate", Ffmpeg.N(fps), "-i", "pipe:0" };
        encArgs.AddRange(audioInput);
        encArgs.AddRange(new[] { "-map", "0:v" });
        AddOutputs(encArgs, 1);
        var encoderProc = ToolProcess.Start(ff, encArgs, stdin: true);
        var clipStreams = new Dictionary<Guid, VideoClipStream>();
        try
        {
            var renderer = new AnnotationRenderer(new SizeD(w, h));
            var map = new TimeMap(pieces);
            int frameBytes = w * h * 4;
            var buf = new byte[frameBytes];
            long maxFrames = (long)Math.Round(total * fps);
            var imageInfo = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            foreach (var o in project.Objects.Where(o => o.Kind == AnnotationKind.Media && o.Media is { Kind: MediaKind.Video, Path: not null }))
                clipStreams[o.Id] = new VideoClipStream(o.Media!, fps);

            for (long i = 0; i < maxFrames; i++)
            {
                ct.ThrowIfCancellationRequested();
                int got = await decoder.ReadFullAsync(buf, frameBytes, ct);
                if (got < frameBytes) break;
                double src = map.SourceTime(i / fps);
                var active = project.Objects.Where(o => o.IsVisible(src) && o.Kind != AnnotationKind.Zoom).ToList();
                var zoom = project.ZoomRect(src, new SizeD(w, h));
                if (active.Count > 0 || zoom != null)
                {
                    var anns = active.Select(o =>
                    {
                        var a = o.AnnotationAt(src);
                        if (clipStreams.TryGetValue(o.Id, out var cs)) a = a with { Image = cs.FrameAt(src - o.Start + o.MediaOffset) };
                        return a;
                    }).ToList();
                    DrawFrame(buf, imageInfo, renderer, anns, zoom, w, h);
                }
                await encoderProc.Input.WriteAsync(buf.AsMemory(0, frameBytes), ct);
                if (i % 5 == 0) progress?.Invoke(0.1 + 0.88 * (i / (double)Math.Max(1, maxFrames)));
            }
            encoderProc.Input.Close();
            await encoderProc.WaitAsync(ct);
            if (encoderProc.P.ExitCode != 0 || !File.Exists(outFile)) throw new ExportException("Couldn't render the video. " + Tail(encoderProc.ErrorText));
        }
        finally
        {
            foreach (var cs in clipStreams.Values) cs.Dispose();
            decoder.Dispose();
            encoderProc.Dispose();
            TryDelete(audioPath);
        }
        progress?.Invoke(1);
        return outFile;
    }

    /// <summary>Draws the annotations onto one raw BGRA frame in place, then applies the camera (zoom) if there is one.</summary>
    static void DrawFrame(byte[] buf, SKImageInfo info, AnnotationRenderer renderer, List<Annotation> annotations, RectD? zoom, int w, int h)
    {
        var handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try
        {
            using var bmp = new SKBitmap();
            bmp.InstallPixels(info, handle.AddrOfPinnedObject(), w * 4);
            bool needsBase = annotations.Any(a => a.Kind == AnnotationKind.Blur);
            using var clean = needsBase ? SKImage.FromPixelCopy(info, buf) : null;      // blur samples the untouched frame
            renderer.Base = clean;
            using (var canvas = new SKCanvas(bmp)) renderer.DrawOverFrame(canvas, annotations);
            renderer.Base = null;

            if (zoom is { } z)
            {
                var r = z.Intersect(new RectD(0, 0, w, h));
                if (r.Width > 8 && r.Height > 8)
                {
                    using var snapshot = SKImage.FromPixelCopy(info, buf);
                    using var paint = new SKPaint { FilterQuality = SKFilterQuality.High };
                    using var canvas = new SKCanvas(bmp);
                    canvas.Clear(SKColors.Black);
                    canvas.DrawImage(snapshot, r.ToSk(), new SKRect(0, 0, w, h), paint);   // crop to the camera region, scale up to the frame
                }
            }
        }
        finally { handle.Free(); }
    }

    static string Tail(string s) => s.Length > 600 ? s[^600..] : s;
    static void TryDelete(string path) { try { File.Delete(path); } catch { } }
}
