using System.Globalization;
using System.Text.Json;

namespace Poltergeist.Core;

/// <summary>Finds ffmpeg / ffprobe and reads facts about a media file.</summary>
public static class Ffmpeg
{
    /// <summary>Set by the app (e.g. the downloaded copy); otherwise ffmpeg is looked for next to the app and on PATH.</summary>
    public static string? FfmpegPath { get; set; }
    public static string? FfprobePath { get; set; }

    static string? Find(string name, string? configured)
    {
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured)) return configured;
        var exeName = OperatingSystem.IsWindows() ? name + ".exe" : name;
        var beside = Path.Combine(AppContext.BaseDirectory, exeName);
        if (File.Exists(beside)) return beside;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { var c = Path.Combine(dir.Trim('"'), exeName); if (File.Exists(c)) return c; } catch { }
        }
        foreach (var extra in new[] { "/opt/homebrew/bin", "/usr/local/bin" })      // macOS, for running the tests here
        {
            var c = Path.Combine(extra, exeName);
            if (File.Exists(c)) return c;
        }
        return null;
    }

    public static string? LocateFfmpeg() => Find("ffmpeg", FfmpegPath);
    public static string? LocateFfprobe() => Find("ffprobe", FfprobePath);
    public static bool Available => LocateFfmpeg() != null && LocateFfprobe() != null;

    public static string Require() => LocateFfmpeg() ?? throw new FileNotFoundException("ffmpeg was not found.");
    public static string RequireProbe() => LocateFfprobe() ?? throw new FileNotFoundException("ffprobe was not found.");

    public sealed record AudioTrackInfo(int Index, string? Tag, string? Title);

    public sealed record MediaInfo(double Duration, int Width, int Height, double Fps, double VideoDuration,
                                   IReadOnlyList<AudioTrackInfo> AudioTracks)
    {
        public bool HasAudio => AudioTracks.Count > 0;
    }

    /// <summary>Duration, size, frame rate and audio tracks of a media file.</summary>
    public static async Task<MediaInfo?> ProbeAsync(string file, CancellationToken ct = default)
    {
        var probe = LocateFfprobe();
        if (probe == null) return null;
        var r = await Proc.RunAsync(probe, new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", file }, ct);
        if (!r.Ok) return null;
        try
        {
            using var doc = JsonDocument.Parse(r.StdOut);
            var root = doc.RootElement;
            double duration = 0;
            if (root.TryGetProperty("format", out var fmt) && fmt.TryGetProperty("duration", out var d))
                double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
            int w = 0, h = 0;
            double fps = 30, vdur = 0;
            var audio = new List<AudioTrackInfo>();
            int audioIndex = 0;
            if (root.TryGetProperty("streams", out var streams))
            {
                foreach (var s in streams.EnumerateArray())
                {
                    var type = s.GetProperty("codec_type").GetString();
                    if (type == "video" && w == 0)
                    {
                        if (s.TryGetProperty("disposition", out var disp) && disp.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1) continue;
                        w = s.GetProperty("width").GetInt32();
                        h = s.GetProperty("height").GetInt32();
                        if (s.TryGetProperty("avg_frame_rate", out var afr)) fps = ParseRate(afr.GetString()) is > 0 and var f ? f : fps;
                        if (s.TryGetProperty("duration", out var vd)) double.TryParse(vd.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out vdur);
                        // rotation (phone videos)
                        if (s.TryGetProperty("side_data_list", out var sd))
                            foreach (var item in sd.EnumerateArray())
                                if (item.TryGetProperty("rotation", out var rot) && Math.Abs(rot.GetInt32()) % 180 == 90) (w, h) = (h, w);
                    }
                    else if (type == "audio")
                    {
                        string? tag = null, title = null;
                        if (s.TryGetProperty("tags", out var tags))
                        {
                            if (tags.TryGetProperty("language", out var lang)) tag = lang.GetString();
                            // MP4 keeps a track's label in the handler name; other containers use "title"
                            if (tags.TryGetProperty("title", out var t)) title = t.GetString();
                            else if (tags.TryGetProperty("handler_name", out var hn)) title = hn.GetString();
                        }
                        audio.Add(new AudioTrackInfo(audioIndex++, tag, title));
                    }
                }
            }
            if (vdur <= 0) vdur = duration;
            return new MediaInfo(duration, w, h, fps, vdur, audio);
        }
        catch { return null; }
    }

    static double ParseRate(string? s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        var parts = s.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d != 0) return n / d;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    /// <summary>Invariant-culture number for building ffmpeg arguments.</summary>
    public static string N(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
}
