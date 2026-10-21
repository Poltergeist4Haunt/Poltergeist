namespace Poltergeist.Core;

/// <summary>Picks a video encoder this ffmpeg build actually has.</summary>
public static class Encoders
{
    static string? _cached;

    public static async Task<string> PickH264Async()
    {
        if (_cached != null) return _cached;
        var ff = Ffmpeg.Require();
        var r = await Proc.RunAsync(ff, new[] { "-hide_banner", "-encoders" });
        string[] preferred = { "libx264", "h264_nvenc", "h264_qsv", "h264_amf", "h264_videotoolbox", "h264_mf" };
        foreach (var name in preferred)
            if (r.StdOut.Contains(" " + name + " ")) return _cached = name;
        throw new InvalidOperationException("This ffmpeg has no H.264 encoder.");
    }

    /// <summary>Arguments for the chosen encoder at the given bitrate (Mbps) and frame rate.</summary>
    public static IEnumerable<string> VideoArgs(string encoder, double mbps, double fps, bool realtime = false)
    {
        var args = new List<string> { "-c:v", encoder, "-b:v", Ffmpeg.N(mbps) + "M", "-pix_fmt", "yuv420p", "-g", Math.Max(1, (int)Math.Round(fps)).ToString() };
        if (encoder == "libx264") args.AddRange(new[] { "-preset", "veryfast", "-maxrate", Ffmpeg.N(mbps * 1.5) + "M", "-bufsize", Ffmpeg.N(mbps * 2) + "M" });
        if (realtime && encoder == "libx264") args.AddRange(new[] { "-tune", "zerolatency" });     // starts writing frames right away
        return args;
    }
}
