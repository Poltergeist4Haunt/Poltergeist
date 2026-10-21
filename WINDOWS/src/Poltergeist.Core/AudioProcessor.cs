using System.Text;

namespace Poltergeist.Core;

/// <summary>
/// Renders the final audio of an export: every recorded track (cut, retimed and concatenated like the video), the audio of
/// imported video layers, each with its own volume curve (mutes, fades), summed into one 48 kHz stereo float stream.
/// </summary>
public static class AudioProcessor
{
    public const int Rate = 48000;
    const int ChunkSamples = 48000;

    /// <summary>atempo only accepts 0.5–2 per stage, so chain stages for other rates.</summary>
    public static string TempoChain(double rate)
    {
        var sb = new StringBuilder();
        while (rate > 2.0 + 1e-9) { sb.Append(",atempo=2.0"); rate /= 2; }
        while (rate < 0.5 - 1e-9) { sb.Append(",atempo=0.5"); rate /= 0.5; }
        if (Math.Abs(rate - 1) > 0.001) sb.Append(",atempo=").Append(Ffmpeg.N(rate));
        return sb.ToString();
    }

    sealed record Source(List<string> Args, VolumeEnvelope Envelope);

    /// <summary>Writes raw little-endian float32 stereo to <paramref name="outPath"/>. Returns the number of sample frames.</summary>
    public static async Task<long> RenderAsync(string src, Ffmpeg.MediaInfo info, VideoProject project, string outPath,
                                               Action<double>? progress, CancellationToken ct)
    {
        var ff = Ffmpeg.Require();
        var pieces = project.Pieces();
        double total = project.OutputDuration;
        long totalSamples = (long)Math.Round(total * Rate);
        var sources = new List<Source>();

        // recorded tracks
        for (int i = 0; i < info.AudioTracks.Count; i++)
        {
            var role = AudioRoleExtensions.FromTag(info.AudioTracks[i].Title);
            var env = VolumeEnvelope.Build(0, total, project.AudioFadeIn, project.AudioFadeOut, project.MutedRanges(role, true));
            sources.Add(new Source(TrackArgs(src, i, pieces), env));
        }
        // imported videos with sound
        foreach (var o in project.Objects.Where(o => o.Kind == AnnotationKind.Media && !o.AudioMuted && o.Volume > 0.001 && o.Media is { Kind: MediaKind.Video, HasAudio: true }))
        {
            var args = OverlayArgs(o, pieces, out double playsUntil);
            if (args == null) continue;
            var env = VolumeEnvelope.Build(0, total, project.AudioFadeIn, project.AudioFadeOut, Array.Empty<(double, double)>(), o.Volume,
                                           new VolumeEnvelope.ClipFade(project.OutputTime(o.Start), project.OutputTime(playsUntil), o.FadeIn, o.FadeOut));
            sources.Add(new Source(args, env));
        }
        if (sources.Count == 0 || totalSamples <= 0) return 0;

        var procs = sources.Select(s => ToolProcess.Start(ff, s.Args)).ToList();
        try
        {
            await using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
            var read = new byte[ChunkSamples * 8];
            var outBytes = new byte[ChunkSamples * 8];
            var mix = new float[ChunkSamples * 2];
            for (long pos = 0; pos < totalSamples; pos += ChunkSamples)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(ChunkSamples, totalSamples - pos);
                Array.Clear(mix, 0, n * 2);
                for (int si = 0; si < procs.Count; si++)
                {
                    int got = await procs[si].ReadFullAsync(read, n * 8, ct);       // a short stream is padded with silence
                    int samples = got / 8;
                    var env = sources[si].Envelope;
                    for (int j = 0; j < samples; j++)
                    {
                        float g = (float)env.GainAt((pos + j) / (double)Rate);
                        mix[j * 2] += BitConverter.ToSingle(read, j * 8) * g;
                        mix[j * 2 + 1] += BitConverter.ToSingle(read, j * 8 + 4) * g;
                    }
                }
                for (int j = 0; j < n * 2; j++)
                {
                    float v = Math.Max(-1f, Math.Min(1f, mix[j]));
                    BitConverter.TryWriteBytes(outBytes.AsSpan(j * 4, 4), v);
                }
                await fs.WriteAsync(outBytes.AsMemory(0, n * 8), ct);
                progress?.Invoke(Math.Min(1.0, (pos + n) / (double)totalSamples));
            }
        }
        finally { foreach (var p in procs) p.Dispose(); }
        return totalSamples;
    }

    static List<string> TrackArgs(string src, int audioIndex, IReadOnlyList<Piece> pieces)
    {
        var g = new StringBuilder();
        for (int k = 0; k < pieces.Count; k++)
            g.Append($"[0:a:{audioIndex}]atrim=start={Ffmpeg.N(pieces[k].SrcStart)}:end={Ffmpeg.N(pieces[k].SrcEnd)},asetpts=PTS-STARTPTS{TempoChain(pieces[k].Rate)}," +
                     $"aformat=sample_rates={Rate}:channel_layouts=stereo[a{k}];");
        if (pieces.Count == 1) g.Append("[a0]anull[a]");
        else
        {
            for (int k = 0; k < pieces.Count; k++) g.Append($"[a{k}]");
            g.Append($"concat=n={pieces.Count}:v=0:a=1[a]");
        }
        return new List<string> { "-v", "error", "-nostdin", "-i", src, "-filter_complex", g.ToString(), "-map", "[a]", "-f", "f32le", "-ar", Rate.ToString(), "-ac", "2", "pipe:1" };
    }

    /// <summary>An imported video's audio placed on the export timeline: it starts when its layer appears, follows cuts and speed changes.</summary>
    static List<string>? OverlayArgs(VideoObject o, IReadOnlyList<Piece> pieces, out double playsUntil)
    {
        var clip = o.Media!;
        playsUntil = Math.Min(o.End, o.Start + Math.Max(0, clip.Duration - o.MediaOffset));
        var slices = new List<(double ClipStart, double Len, double CompAt, double Rate)>();
        foreach (var piece in pieces)
        {
            double a = Math.Max(piece.SrcStart, o.Start), b = Math.Min(piece.SrcEnd, playsUntil);
            if (b <= a) continue;
            slices.Add((a - o.Start + o.MediaOffset, b - a, piece.CompStart + (a - piece.SrcStart) / piece.Rate, piece.Rate));
        }
        if (slices.Count == 0 || clip.Path == null) return null;
        var args = new List<string> { "-v", "error", "-nostdin" };
        var g = new StringBuilder();
        for (int k = 0; k < slices.Count; k++)
        {
            args.AddRange(new[] { "-i", clip.Path });
            var s = slices[k];
            int delayMs = (int)Math.Round(s.CompAt * 1000);
            g.Append($"[{k}:a]atrim=start={Ffmpeg.N(s.ClipStart)}:end={Ffmpeg.N(s.ClipStart + s.Len)},asetpts=PTS-STARTPTS{TempoChain(s.Rate)}," +
                     $"aformat=sample_rates={Rate}:channel_layouts=stereo,adelay={delayMs}|{delayMs}[s{k}];");
        }
        if (slices.Count == 1) g.Append("[s0]anull[a]");
        else
        {
            for (int k = 0; k < slices.Count; k++) g.Append($"[s{k}]");
            g.Append($"amix=inputs={slices.Count}:normalize=0:duration=longest[a]");
        }
        args.AddRange(new[] { "-filter_complex", g.ToString(), "-map", "[a]", "-f", "f32le", "-ar", Rate.ToString(), "-ac", "2", "pipe:1" });
        return args;
    }
}
