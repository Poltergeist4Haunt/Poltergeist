namespace Poltergeist.Core;

/// <summary>
/// One track's volume over time: mutes (10 ms slopes), fades at the start and end of the mix, an optional clip fade and a constant
/// gain, multiplied into one piecewise-linear curve (breakpoints with the combined gain at each).
/// </summary>
public sealed class VolumeEnvelope
{
    readonly double[] _times;
    readonly double[] _gains;

    VolumeEnvelope(double[] times, double[] gains) { _times = times; _gains = gains; }

    public static readonly VolumeEnvelope Unity = new(new[] { 0.0 }, new[] { 1.0 });

    public sealed record ClipFade(double Start, double End, double FadeIn, double FadeOut);

    public static VolumeEnvelope Build(double origin, double length, double fadeIn, double fadeOut,
                                       IEnumerable<(double Start, double End)> mutes, double gain = 1, ClipFade? clip = null)
    {
        if (length <= 0.05) return new VolumeEnvelope(new[] { origin }, new[] { gain });
        double end = origin + length;

        var merged = new List<(double Start, double End)>();
        foreach (var m in mutes.OrderBy(m => m.Start).Where(m => m.End - m.Start > 0.02))
        {
            if (merged.Count > 0 && m.Start - merged[^1].End < 0.03) merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, m.End));
            else merged.Add(m);
        }
        static double Slope((double Start, double End) m) => Math.Min(0.01, (m.End - m.Start) / 2);
        static double MuteAmount(double t, (double Start, double End) m)               // 1 inside a mute, easing at its edges
        {
            double rf = Slope(m);
            if (t <= m.Start || t >= m.End) return 0;
            return Math.Min(1, Math.Min((t - m.Start) / rf, (m.End - t) / rf));
        }
        double GainAt(double t)
        {
            double g = gain;
            double u = t - origin;
            if (fadeIn > 0) g *= Math.Min(1, Math.Max(0, u / fadeIn));
            if (fadeOut > 0) g *= Math.Min(1, Math.Max(0, (length - u) / fadeOut));
            foreach (var m in merged) g *= 1 - MuteAmount(t, m);
            if (clip != null)
            {
                double k = 1;
                if (clip.FadeIn > 0) k = Math.Min(k, (t - clip.Start) / clip.FadeIn);
                if (clip.FadeOut > 0) k = Math.Min(k, (clip.End - t) / clip.FadeOut);
                g *= Math.Min(1, Math.Max(0, k));
            }
            return g;
        }

        var points = new List<double> { origin, end };
        if (fadeIn > 0) points.Add(origin + fadeIn);
        if (fadeOut > 0) points.Add(end - fadeOut);
        foreach (var m in merged) points.AddRange(new[] { m.Start, m.Start + Slope(m), m.End - Slope(m), m.End });
        if (clip != null)
        {
            points.Add(clip.Start); points.Add(clip.End);
            if (clip.FadeIn > 0) points.Add(clip.Start + clip.FadeIn);
            if (clip.FadeOut > 0) points.Add(clip.End - clip.FadeOut);
        }
        var unique = new List<double>();
        foreach (var t in points.Where(t => t >= origin - 1e-9 && t <= end + 1e-9).OrderBy(t => t))
            if (unique.Count == 0 || t - unique[^1] > 0.002) unique.Add(t);
        return new VolumeEnvelope(unique.ToArray(), unique.Select(GainAt).ToArray());
    }

    /// <summary>The gain at time <paramref name="t"/> (linear between breakpoints; held outside them).</summary>
    public double GainAt(double t)
    {
        int n = _times.Length;
        if (t <= _times[0]) return _gains[0];
        if (t >= _times[n - 1]) return _gains[n - 1];
        int lo = 0, hi = n - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (_times[mid] <= t) lo = mid; else hi = mid;
        }
        double span = _times[hi] - _times[lo];
        double f = span > 0 ? (t - _times[lo]) / span : 1;
        return _gains[lo] + (_gains[hi] - _gains[lo]) * f;
    }
}
