using Poltergeist.Core;

namespace Poltergeist;

/// <summary>Loudness of one audio track over time, for drawing the timeline lane.</summary>
static class Waveform
{
    public const double Bucket = 0.02;                 // seconds per value

    /// <summary>Decodes one audio track with ffmpeg and returns its peak level (0…1) for every <see cref="Bucket"/>.</summary>
    public static async Task<float[]?> LoadAsync(string file, int trackIndex)
    {
        try
        {
            using var tool = ToolProcess.Start(Ffmpeg.Require(), new[]
            {
                "-v", "error", "-nostdin", "-i", file, "-map", $"0:a:{trackIndex}", "-ac", "1", "-ar", "4000", "-f", "s16le", "-",
            });
            const int per = (int)(4000 * Bucket);       // samples per bucket
            var peaks = new List<float>();
            var buf = new byte[per * 2 * 64];
            int carry = 0;
            float peak = 0;
            int inBucket = 0;
            while (true)
            {
                int n = await tool.Output.ReadAsync(buf.AsMemory(carry, buf.Length - carry));
                if (n == 0) break;
                int total = carry + n, usable = total - total % 2;
                for (int i = 0; i < usable; i += 2)
                {
                    float v = Math.Abs((short)(buf[i] | (buf[i + 1] << 8))) / 32768f;
                    if (v > peak) peak = v;
                    if (++inBucket == per) { peaks.Add(peak); peak = 0; inBucket = 0; }
                }
                carry = total - usable;
                if (carry > 0) buf[0] = buf[usable];
            }
            if (inBucket > 0) peaks.Add(peak);
            if (peaks.Count == 0) return null;
            float max = Math.Max(0.02f, peaks.OrderBy(p => p).ElementAt((int)(peaks.Count * 0.98) - (peaks.Count > 1 ? 1 : 0)));
            return peaks.Select(p => (float)Math.Min(1, Math.Sqrt(p / max))).ToArray();
        }
        catch { return null; }
    }
}
