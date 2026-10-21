namespace Poltergeist.Core;

/// <summary>A position of an object at one moment (video pixels, origin top-left).</summary>
public readonly record struct Keyframe(double Time, PointD P1, PointD P2);

/// <summary>
/// A shape, text, blur, picture or camera move placed on the video. It is visible from <see cref="Start"/> to
/// <see cref="End"/> (source time) and glides between its keyframes.
/// </summary>
public sealed class VideoObject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public AnnotationKind Kind { get; set; }
    public ColorRgba Color { get; set; } = Palette.Accent;
    public int Level { get; set; } = 2;
    public string Text { get; set; } = "";
    public double Start { get; set; }
    public double End { get; set; }
    /// <summary>Sorted by time, never empty.</summary>
    public List<Keyframe> Keyframes { get; set; } = new();
    public MediaClip? Media { get; set; }
    /// <summary>0…1, the clip's own audio.</summary>
    public double Volume { get; set; } = 1;
    public bool AudioMuted { get; set; }
    /// <summary>Fade in / out seconds. For a zoom they are the ease into and out of the zoom instead.</summary>
    public double FadeIn { get; set; }
    public double FadeOut { get; set; }
    /// <summary>Seconds into the clip where this layer starts playing (non-zero for the second half of a split).</summary>
    public double MediaOffset { get; set; }
    public bool SoftBlur { get; set; }
    public List<PointD> Path { get; set; } = new();

    /// <summary>Half-open, so two halves of a split layer never both show on the frame where they meet.</summary>
    public bool IsVisible(double t) => t >= Start && t < End;

    public double OpacityAt(double t)
    {
        double o = 1;
        if (FadeIn > 0) o = Math.Min(o, (t - Start) / FadeIn);
        if (FadeOut > 0) o = Math.Min(o, (End - t) / FadeOut);
        return Math.Min(1, Math.Max(0, o));
    }

    /// <summary>Linear interpolation between the surrounding keyframes; holds the first / last position outside them.</summary>
    public (PointD P1, PointD P2) GeometryAt(double t)
    {
        if (Keyframes.Count == 0) return (default, default);
        var first = Keyframes[0];
        var last = Keyframes[^1];
        if (t <= first.Time) return (first.P1, first.P2);
        if (t >= last.Time) return (last.P1, last.P2);
        for (int i = 1; i < Keyframes.Count; i++)
        {
            var a = Keyframes[i - 1];
            var b = Keyframes[i];
            if (t <= b.Time)
            {
                double span = b.Time - a.Time;
                double f = span > 0 ? (t - a.Time) / span : 1;
                return (PointD.Lerp(a.P1, b.P1, f), PointD.Lerp(a.P2, b.P2, f));
            }
        }
        return (last.P1, last.P2);
    }

    /// <summary>The drawable form at time <paramref name="t"/>.</summary>
    public Annotation AnnotationAt(double t)
    {
        var g = GeometryAt(t);
        return new Annotation
        {
            Id = Id,
            Kind = Kind,
            P1 = g.P1,
            P2 = g.P2,
            Text = Text,
            Color = Color,
            Level = Level,
            Opacity = Kind == AnnotationKind.Zoom ? 1 : OpacityAt(t),
            Soft = SoftBlur,
            Path = Path,
            Image = Kind == AnnotationKind.Media ? Media?.FrameAt(t - Start + MediaOffset) : null,
        };
    }

    /// <summary>Sets the position at time <paramref name="t"/>, reusing a keyframe that's already (almost) there.</summary>
    public void SetKeyframe(double t, PointD p1, PointD p2)
    {
        int i = Keyframes.FindIndex(k => Math.Abs(k.Time - t) < 0.05);
        if (i >= 0) Keyframes[i] = Keyframes[i] with { P1 = p1, P2 = p2 };
        else { Keyframes.Add(new Keyframe(t, p1, p2)); Keyframes.Sort((a, b) => a.Time.CompareTo(b.Time)); }
    }

    /// <summary>
    /// Dragging at the playhead. With <paramref name="animate"/> a keyframe is recorded at <paramref name="t"/> (so the object
    /// moves over time); without it the whole path shifts and no keyframe is added.
    /// </summary>
    public VideoObject Moved(HandleTarget target, double dx, double dy, double t, bool animate, double videoAspect = 16.0 / 9.0)
    {
        bool movesP1 = target != HandleTarget.P2, movesP2 = target != HandleTarget.P1;
        PointD Shift(PointD p, bool moves) => moves ? new PointD(p.X + dx, p.Y + dy) : p;
        (PointD, PointD) Locked(PointD a, PointD b)
        {
            if (target == HandleTarget.Body || (Kind != AnnotationKind.Media && Kind != AnnotationKind.Zoom)) return (a, b);
            Annotation.LockAspect(ref a, ref b, target, Kind == AnnotationKind.Zoom ? videoAspect : (Media?.Aspect ?? 1));
            return (a, b);
        }
        var copy = Clone();
        if (animate)
        {
            var g = GeometryAt(t);
            var n = Locked(Shift(g.P1, movesP1), Shift(g.P2, movesP2));
            copy.SetKeyframe(t, n.Item1, n.Item2);
        }
        else
        {
            for (int i = 0; i < copy.Keyframes.Count; i++)
            {
                var k = copy.Keyframes[i];
                var n = Locked(Shift(k.P1, movesP1), Shift(k.P2, movesP2));
                copy.Keyframes[i] = k with { P1 = n.Item1, P2 = n.Item2 };
            }
        }
        return copy;
    }

    public VideoObject Clone()
    {
        var c = (VideoObject)MemberwiseClone();
        c.Keyframes = new List<Keyframe>(Keyframes);
        c.Path = new List<PointD>(Path);
        return c;
    }
}

/// <summary>Which recorded source an audio track holds. Recordings label their tracks so the editor can tell them apart.</summary>
public enum AudioRole
{
    System, Mic, Unknown,
}

public static class AudioRoleExtensions
{
    public static string Title(this AudioRole r) => r switch
    {
        AudioRole.System => "Desktop audio",
        AudioRole.Mic => "Microphone",
        _ => "Audio",
    };

    /// <summary>The label stored on a recorded track (its handler name in MP4); read back with ffprobe.</summary>
    public static string? Tag(this AudioRole r) => r switch
    {
        AudioRole.System => "polt-system",
        AudioRole.Mic => "polt-mic",
        _ => null,
    };

    public static AudioRole FromTag(string? title) => title switch
    {
        "polt-system" => AudioRole.System,
        "polt-mic" => AudioRole.Mic,
        _ => AudioRole.Unknown,
    };
}

/// <summary>A stretch where one audio track is silenced (source time). The other tracks keep playing.</summary>
public sealed class AudioMute
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public AudioRole Role { get; set; }
    public double Start { get; set; }
    public double End { get; set; }
}

/// <summary>A place where an audio track has been split in the editor, so its parts can be selected and muted one by one. Has no effect on the export itself.</summary>
public sealed class AudioSplit
{
    public AudioRole Role { get; set; }
    public double Time { get; set; }
}

/// <summary>A stretch of the recording played faster or slower (source time).</summary>
public sealed class SpeedRegion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public double Start { get; set; }
    public double End { get; set; }
    /// <summary>0.25 … 4.</summary>
    public double Rate { get; set; } = 2;
}

/// <summary>
/// One clip of the recording after splitting. Clips always cover the whole timeline end to end; a removed clip is skipped in
/// the preview and left out of the export.
/// </summary>
public sealed class Segment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public double Start { get; set; }
    public double End { get; set; }
    public bool Removed { get; set; }
}

public readonly record struct Cut(Guid Id, double Start, double End);

/// <summary>A stretch of the output: part of the source played at one rate, starting at CompStart on the exported timeline.</summary>
public readonly record struct Piece(double SrcStart, double SrcEnd, double Rate, double CompStart)
{
    public double CompLength => (SrcEnd - SrcStart) / Rate;
    public double CompEnd => CompStart + CompLength;
}

/// <summary>Everything the user did to a recording. Times are in the original (source) video.</summary>
public sealed class VideoProject
{
    public double Duration { get; }
    public List<VideoObject> Objects { get; set; } = new();
    public List<Segment> Segments { get; set; } = new();
    public List<AudioMute> Mutes { get; set; } = new();
    public List<AudioSplit> AudioSplits { get; set; } = new();
    public List<SpeedRegion> Speeds { get; set; } = new();
    /// <summary>The audio tracks in the file, in order.</summary>
    public List<AudioRole> AudioRoles { get; set; } = new();
    /// <summary>Fades of the whole mix at the start and end of the exported video.</summary>
    public double AudioFadeIn { get; set; }
    public double AudioFadeOut { get; set; }
    public double TrimStart { get; set; }
    public double TrimEnd { get; set; }

    public VideoProject(double duration)
    {
        Duration = duration;
        TrimEnd = duration;
        Segments = new List<Segment> { new() { Start = 0, End = duration } };
    }

    public bool IsTrimmed => TrimStart > 0.05 || TrimEnd < Duration - 0.05;

    /// <summary>The removed clips as time ranges. Assigning replaces the clips with a split at each cut's edges.</summary>
    public IReadOnlyList<Cut> Cuts
    {
        get => Segments.Where(s => s.Removed).Select(s => new Cut(s.Id, s.Start, s.End)).ToList();
        set
        {
            var outList = new List<Segment>();
            double cursor = 0;
            foreach (var c in value.OrderBy(c => c.Start))
            {
                double s = Math.Max(c.Start, cursor), e = Math.Min(c.End, Duration);
                if (e <= s) continue;
                if (s > cursor) outList.Add(new Segment { Start = cursor, End = s });
                outList.Add(new Segment { Id = c.Id, Start = s, End = e, Removed = true });
                cursor = e;
            }
            if (cursor < Duration) outList.Add(new Segment { Start = cursor, End = Duration });
            Segments = outList.Count == 0 ? new List<Segment> { new() { Start = 0, End = Duration } } : outList;
        }
    }

    /// <summary>Annotations, cut-outs and speed changes need the video re-encoded; plain trimming doesn't.</summary>
    public bool NeedsRender => Objects.Count > 0 || Cuts.Count > 0 || Speeds.Count > 0;

    /// <summary>Several audio tracks must be mixed into one for upload (players only use the first), and mutes are baked in.</summary>
    public bool NeedsAudioMix => AudioRoles.Count > 1 || Mutes.Count > 0 || AudioFadeIn > 0 || AudioFadeOut > 0;

    /// <summary>The parts of the source that survive the trim and cuts, in order.</summary>
    public List<(double Start, double End)> KeptSegments()
    {
        var outList = new List<(double, double)>();
        double cursor = TrimStart;
        foreach (var c in Cuts.OrderBy(c => c.Start))
        {
            double cs = Math.Max(c.Start, TrimStart), ce = Math.Min(c.End, TrimEnd);
            if (ce <= cs) continue;
            if (cs > cursor) outList.Add((cursor, cs));
            cursor = Math.Max(cursor, ce);
        }
        if (cursor < TrimEnd) outList.Add((cursor, TrimEnd));
        return outList.Where(s => s.Item2 - s.Item1 > 0.04).ToList();
    }

    /// <summary>Speed regions clipped to the trim, in order, with overlaps resolved (an earlier region wins).</summary>
    List<SpeedRegion> NormalizedSpeeds()
    {
        var outList = new List<SpeedRegion>();
        double cursor = TrimStart;
        foreach (var r in Speeds.OrderBy(r => r.Start))
        {
            double s = Math.Max(Math.Max(r.Start, cursor), TrimStart), e = Math.Min(r.End, TrimEnd);
            if (e - s <= 0.05) continue;
            outList.Add(new SpeedRegion { Id = r.Id, Start = s, End = e, Rate = Math.Min(4, Math.Max(0.25, r.Rate)) });
            cursor = e;
        }
        return outList;
    }

    /// <summary>What the export plays, in order: the kept parts of the source split wherever the speed changes.</summary>
    public List<Piece> Pieces()
    {
        var regions = NormalizedSpeeds();
        var outList = new List<Piece>();
        double comp = 0;
        void Add(double a, double b, double rate)
        {
            if (b - a <= 0.001) return;
            var piece = new Piece(a, b, rate, comp);
            outList.Add(piece);
            comp = piece.CompEnd;
        }
        foreach (var seg in KeptSegments())
        {
            double cursor = seg.Start;
            foreach (var r in regions)
            {
                if (r.End <= seg.Start || r.Start >= seg.End) continue;
                double s = Math.Max(r.Start, seg.Start), e = Math.Min(r.End, seg.End);
                Add(cursor, s, 1);
                Add(s, e, r.Rate);
                cursor = e;
            }
            Add(cursor, seg.End, 1);
        }
        return outList;
    }

    public double OutputDuration => Pieces() is { Count: > 0 } p ? p[^1].CompEnd : 0;

    /// <summary>Position on the exported timeline for a source time (a moment inside a cut maps to where the cut closes).</summary>
    public double OutputTime(double t)
    {
        double last = 0;
        foreach (var p in Pieces())
        {
            if (t <= p.SrcStart) return p.CompStart;
            if (t < p.SrcEnd) return p.CompStart + (t - p.SrcStart) / p.Rate;
            last = p.CompEnd;
        }
        return last;
    }

    /// <summary>Speed at a source time (1 outside every region), used by the preview.</summary>
    public double RateAt(double t) => NormalizedSpeeds().FirstOrDefault(r => t >= r.Start && t < r.End)?.Rate ?? 1;

    public Cut? CutAt(double t) => Cuts.Where(c => t >= c.Start && t < c.End).Select(c => (Cut?)c).FirstOrDefault();

    /// <summary>
    /// Where the camera is at time <paramref name="t"/> (source time): null means the whole frame. A zoom object eases from the
    /// full frame to its region over FadeIn seconds and back over FadeOut.
    /// </summary>
    public RectD? ZoomRect(double t, SizeD videoSize)
    {
        var z = Objects.LastOrDefault(o => o.Kind == AnnotationKind.Zoom && o.IsVisible(t));
        if (z == null) return null;
        var g = z.GeometryAt(t);
        var target = RectD.FromCorners(g.P1, g.P2);
        double k = 1;
        if (z.FadeIn > 0) k = Math.Min(k, (t - z.Start) / z.FadeIn);
        if (z.FadeOut > 0) k = Math.Min(k, (z.End - t) / z.FadeOut);
        k = Math.Min(1, Math.Max(0, k));
        k = k * k * (3 - 2 * k);                                    // smoothstep: eases in and out
        double L(double a, double b) => a + (b - a) * k;
        return new RectD(L(0, target.X), L(0, target.Y), L(videoSize.Width, target.Width), L(videoSize.Height, target.Height));
    }

    /// <summary>Muted time ranges for a track, merged and sorted. <paramref name="inOutput"/> maps them onto the exported timeline.</summary>
    public List<(double Start, double End)> MutedRanges(AudioRole role, bool inOutput)
    {
        var raw = Mutes.Where(m => m.Role == role && m.End > m.Start).OrderBy(m => m.Start).ToList();
        var merged = new List<(double Start, double End)>();
        foreach (var m in raw)
        {
            if (merged.Count > 0 && m.Start <= merged[^1].End) merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, m.End));
            else merged.Add((m.Start, m.End));
        }
        if (!inOutput) return merged;
        var outList = new List<(double, double)>();
        foreach (var piece in Pieces())
            foreach (var m in merged)
            {
                double a = Math.Max(m.Start, piece.SrcStart), b = Math.Min(m.End, piece.SrcEnd);
                if (b > a) outList.Add((piece.CompStart + (a - piece.SrcStart) / piece.Rate, piece.CompStart + (b - piece.SrcStart) / piece.Rate));
            }
        return outList;
    }

    public VideoProject Clone()
    {
        var c = new VideoProject(Duration)
        {
            Objects = Objects.Select(o => o.Clone()).ToList(),
            Segments = Segments.Select(s => new Segment { Id = s.Id, Start = s.Start, End = s.End, Removed = s.Removed }).ToList(),
            Mutes = Mutes.Select(m => new AudioMute { Id = m.Id, Role = m.Role, Start = m.Start, End = m.End }).ToList(),
            AudioSplits = AudioSplits.Select(a => new AudioSplit { Role = a.Role, Time = a.Time }).ToList(),
            Speeds = Speeds.Select(r => new SpeedRegion { Id = r.Id, Start = r.Start, End = r.End, Rate = r.Rate }).ToList(),
            AudioRoles = new List<AudioRole>(AudioRoles),
            AudioFadeIn = AudioFadeIn,
            AudioFadeOut = AudioFadeOut,
            TrimStart = TrimStart,
            TrimEnd = TrimEnd,
        };
        return c;
    }
}

/// <summary>Maps a time in the exported video back to the source, so annotations (defined on source time) line up after cuts and speed changes.</summary>
public sealed class TimeMap
{
    readonly IReadOnlyList<Piece> _pieces;
    public TimeMap(IReadOnlyList<Piece> pieces) => _pieces = pieces;

    public double SourceTime(double comp)
    {
        for (int i = _pieces.Count - 1; i >= 0; i--)
        {
            var p = _pieces[i];
            if (comp >= p.CompStart - 0.0005) return p.SrcStart + Math.Min(comp - p.CompStart, p.CompLength) * p.Rate;
        }
        return _pieces.Count > 0 ? _pieces[0].SrcStart : comp;
    }
}
