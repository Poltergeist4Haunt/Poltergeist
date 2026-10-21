using SkiaSharp;

namespace Poltergeist.Core;

/// <summary>Sequentially decodes an imported video for an export, handing out the frame for a given time.</summary>
public sealed class VideoClipStream : IDisposable
{
    readonly string _path;
    readonly double _fps;
    readonly int _w, _h;
    readonly byte[] _buf;
    ToolProcess? _proc;
    double _t0;              // time of the first frame the current process produces
    long _index = -1;        // index of the frame held in _last
    SKImage? _last;
    bool _eof;

    public VideoClipStream(MediaClip clip, double fps)
    {
        _path = clip.Path ?? throw new ArgumentException("clip has no file");
        _fps = Math.Max(1, fps);
        double k = Math.Min(1.0, 960.0 / Math.Max(clip.PixelWidth, clip.PixelHeight));
        _w = Math.Max(2, (int)(clip.PixelWidth * k)); _h = Math.Max(2, (int)(clip.PixelHeight * k));
        _buf = new byte[_w * _h * 4];
    }

    void Restart(double t)
    {
        _proc?.Dispose();
        _proc = ToolProcess.Start(Ffmpeg.Require(), new[]
        {
            "-v", "error", "-nostdin", "-ss", Ffmpeg.N(Math.Max(0, t)), "-i", _path,
            "-vf", $"fps={Ffmpeg.N(_fps)},scale={_w}:{_h},format=bgra", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1",
        });
        _t0 = Math.Max(0, t);
        _index = -1;
        _eof = false;
    }

    /// <summary>The frame showing at <paramref name="t"/> seconds into the clip (the last frame once it has ended).</summary>
    public SKImage? FrameAt(double t)
    {
        long want = (long)Math.Floor(Math.Max(0, t - _t0) * _fps + 1e-6);
        if (_proc == null || want < _index || want > _index + _fps * 2)           // first use, going backwards, or a big jump
        {
            Restart(t);
            want = 0;
        }
        while (_index < want && !_eof)
        {
            int got = _proc!.ReadFullAsync(_buf, _buf.Length).GetAwaiter().GetResult();
            if (got < _buf.Length) { _eof = true; break; }
            _index++;
            _last?.Dispose();
            _last = SKImage.FromPixelCopy(new SKImageInfo(_w, _h, SKColorType.Bgra8888, SKAlphaType.Premul), _buf);
        }
        return _last;
    }

    public void Dispose() { _proc?.Dispose(); _last?.Dispose(); }
}
