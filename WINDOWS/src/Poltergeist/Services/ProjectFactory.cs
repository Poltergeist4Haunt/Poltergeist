using Poltergeist.Core;

namespace Poltergeist;

static class ProjectFactory
{
    /// <summary>A fresh, unedited project for a video file, with its audio tracks recognised (desktop / microphone).</summary>
    public static async Task<(VideoProject Project, Ffmpeg.MediaInfo Info)?> ForFileAsync(string file)
    {
        var info = await Ffmpeg.ProbeAsync(file);
        if (info == null || info.Duration <= 0) return null;
        var p = new VideoProject(info.Duration);
        foreach (var t in info.AudioTracks) p.AudioRoles.Add(AudioRoleExtensions.FromTag(t.Title));
        return (p, info);
    }
}
