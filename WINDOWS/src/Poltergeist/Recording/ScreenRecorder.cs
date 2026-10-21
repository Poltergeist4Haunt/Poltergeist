using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Poltergeist.Core;

namespace Poltergeist;

/// <summary>
/// One screen recording. Video comes from ffmpeg's screen grabber, desktop audio and the microphone from WASAPI. Pausing ends the
/// current video piece and starts another when resumed; on stop the pieces are joined and the audio is muxed in as separate,
/// labelled tracks (<c>polt-system</c>, <c>polt-mic</c>) so the editor can cut them independently.
/// </summary>
public sealed class ScreenRecorder
{
    readonly Rectangle _region;
    readonly AppSettings _s;
    readonly string _dir = Path.Combine(Paths.Temp, "rec-" + Guid.NewGuid().ToString("N")[..8]);
    readonly List<string> _segments = new();
    readonly Stopwatch _active = new();
    readonly StringBuilder _ffLog = new();

    Process? _ff;
    TaskCompletionSource<bool>? _firstFrame;
    RecordingControls? _controls;
    RegionOutline? _outline;
    DispatcherTimer? _tick;

    WasapiLoopbackCapture? _sysCap;
    WaveFileWriter? _sysWriter;
    WasapiCapture? _micCap;
    WaveFileWriter? _micWriter;
    WasapiOut? _silence;
    long _sysWritten, _micWritten;
    volatile bool _gate;
    volatile bool _micMuted;
    string? _sysFile, _micFile;

    bool _paused, _stopping, _started;

    public event Action<string?, string?>? Finished;     // (file, error); both null = discarded

    public bool IsPaused => _paused;
    public bool MicEnabled => _s.RecordMic;
    public bool MicMuted => _micMuted;
    public TimeSpan Elapsed => _active.Elapsed;

    public ScreenRecorder(Rectangle region, AppSettings settings)
    {
        // H.264 wants even dimensions.
        _region = new Rectangle(region.X, region.Y, Math.Max(2, region.Width & ~1), Math.Max(2, region.Height & ~1));
        _s = settings;
    }

    // ---- lifecycle -------------------------------------------------------------------------------------------------------

    public async Task StartAsync()
    {
        Directory.CreateDirectory(_dir);
        try
        {
            await StartSegmentAsync();
            StartAudio();
            _gate = true;
            _started = true;
        }
        catch
        {
            Cleanup();
            throw;
        }

        var screenBounds = System.Windows.Forms.Screen.FromRectangle(_region).Bounds;
        bool wholeScreen = _region.Width >= screenBounds.Width - 2 && _region.Height >= screenBounds.Height - 2;
        if (!wholeScreen) { _outline = new RegionOutline(_region); _outline.Show(); }
        _controls = new RecordingControls(this, _region);
        _controls.Show();
        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _tick.Tick += (_, _) => { _controls?.Update(); KeepAudioInStep(); };
        _tick.Start();
    }

    public void TogglePause()
    {
        if (_stopping || !_started) return;
        if (!_paused) Pause(); else Resume();
    }

    async void Pause()
    {
        _paused = true;
        _gate = false;
        _active.Stop();
        _segLens.Add(_active.Elapsed - _segStart);
        _controls?.Update();
        await StopSegmentAsync();
    }

    async void Resume()
    {
        try
        {
            await StartSegmentAsync();
            _paused = false;
            _gate = true;
            _controls?.Update();
        }
        catch (Exception e) { Fail(e.Message); }
    }

    public void SetMicMuted(bool muted) { _micMuted = muted; _controls?.Update(); }

    /// <summary>Stop, keep the recording and hand it over.</summary>
    public bool EditRequested { get; private set; }

    public void RequestStop(bool edit = false) { EditRequested = edit; _ = FinishAsync(discard: false); }

    /// <summary>Throw the recording away.</summary>
    public void Abort() => _ = FinishAsync(discard: true);

    async Task FinishAsync(bool discard)
    {
        if (_stopping) return;
        _stopping = true;
        _gate = false;
        _active.Stop();
        _stopAt = _active.Elapsed;                  // the moment of the click: whatever ffmpeg captures while closing is cut off
        if (!_paused) _segLens.Add(_active.Elapsed - _segStart);
        _tick?.Stop();
        _controls?.Close(); _controls = null;
        _outline?.Close(); _outline = null;
        if (!discard) PreviewToast.ShowMessage("Recording", "Finishing up…");

        string? result = null, error = null;
        try
        {
            await StopSegmentAsync();
            StopAudio();
            if (!discard) result = await Task.Run(Finalize);
        }
        catch (Exception e) { error = e.Message; }
        finally { Cleanup(keepFile: result); }

        Application.Current.Dispatcher.Invoke(() => Finished?.Invoke(discard ? null : result, error));
    }

    void Fail(string message)
    {
        if (_stopping) return;
        _stopping = true;
        _gate = false;
        _tick?.Stop();
        _controls?.Close(); _controls = null;
        _outline?.Close(); _outline = null;
        try { _ff?.Kill(true); } catch { }
        StopAudio();
        Cleanup();
        Finished?.Invoke(null, message);
    }

    void Cleanup(string? keepFile = null)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_dir))
                if (!string.Equals(f, keepFile, StringComparison.OrdinalIgnoreCase)) File.Delete(f);
            if (keepFile == null || !string.Equals(Path.GetDirectoryName(keepFile), _dir, StringComparison.OrdinalIgnoreCase)) Directory.Delete(_dir, true);
        }
        catch { }
    }

    // ---- video -----------------------------------------------------------------------------------------------------------

    async Task StartSegmentAsync()
    {
        var seg = Path.Combine(_dir, $"part{_segments.Count}.mp4");
        _segments.Add(seg);
        var encoder = await Encoders.PickH264Async();
        int fps = Math.Max(5, _s.Fps);
        var args = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "error", "-nostats", "-progress", "pipe:1", "-stats_period", "0.04", "-fflags", "nobuffer", "-probesize", "32",
            "-f", "gdigrab", "-framerate", fps.ToString(), "-offset_x", _region.X.ToString(), "-offset_y", _region.Y.ToString(),
            "-video_size", $"{_region.Width}x{_region.Height}", "-draw_mouse", "1", "-i", "desktop",
        };
        args.AddRange(Encoders.VideoArgs(encoder, _s.Bitrate, fps, realtime: true));
        args.AddRange(new[] { "-an", seg });

        var psi = Proc.Start(Ffmpeg.Require(), args, redirectIn: true, redirectOut: true);
        psi.Environment["__COMPAT_LAYER"] = "HIGHDPIAWARE";        // gdigrab reads physical pixels only if its process is DPI aware
        var p = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start ffmpeg.");
        _ff = p;
        var first = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _firstFrame = first;
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (_ffLog) _ffLog.AppendLine(e.Data); };
        p.BeginErrorReadLine();
        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await p.StandardOutput.ReadLineAsync()) != null)
                    if (line.StartsWith("frame=") && int.TryParse(line.AsSpan(6), out var n) && n > 0) first.TrySetResult(true);
            }
            catch { }
            first.TrySetResult(false);
        });

        var done = await Task.WhenAny(first.Task, Task.Delay(10000));
        if (done != first.Task || !first.Task.Result)
        {
            string log; lock (_ffLog) log = _ffLog.ToString().Trim();
            try { p.Kill(true); } catch { }
            throw new InvalidOperationException("The screen grabber didn't start. " + (log.Length > 0 ? log : "ffmpeg may be too old (needs the gdigrab input)."));
        }
        _segStart = _active.Elapsed;                 // the recording clock and the picture start together, so audio can line up with it
        _active.Start();
        // If ffmpeg dies mid-recording, say so.
        _ = p.WaitForExitAsync().ContinueWith(_ =>
        {
            if (!_stopping && !_paused && ReferenceEquals(_ff, p))
                Application.Current.Dispatcher.BeginInvoke(() => Fail("Recording stopped unexpectedly. " + LastLog()));
        });
    }

    string LastLog()
    {
        lock (_ffLog) { var s = _ffLog.ToString().Trim(); return s.Length > 400 ? s[^400..] : s; }
    }

    async Task StopSegmentAsync()
    {
        var p = _ff;
        _ff = null;
        if (p == null) return;
        try
        {
            if (!p.HasExited)
            {
                await p.StandardInput.WriteAsync("q");             // ffmpeg finishes the file properly on "q"
                await p.StandardInput.FlushAsync();
                p.StandardInput.Close();
                if (!p.WaitForExit(12000)) p.Kill(true);
            }
        }
        catch { try { p.Kill(true); } catch { } }
        finally { p.Dispose(); }
    }

    // ---- audio -----------------------------------------------------------------------------------------------------------

    void StartAudio()
    {
        if (_s.RecordSystemAudio)
        {
            try
            {
                // Loopback only delivers data while something is playing; keep a silent stream running so the track never goes short.
                _silence = new WasapiOut();
                _silence.Init(new SilenceProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)));
                _silence.Play();
            }
            catch { _silence = null; }
            _sysFile = Path.Combine(_dir, "system.wav");
            AttachSystem();
        }
        if (_s.RecordMic)
        {
            try
            {
                MMDevice? device = null;
                using var en = new MMDeviceEnumerator();
                if (!string.IsNullOrEmpty(_s.MicDeviceId))
                    device = en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).FirstOrDefault(d => d.ID == _s.MicDeviceId);
                device ??= en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                _micFile = Path.Combine(_dir, "mic.wav");
                _micCap = new WasapiCapture(device);
                _micWriter = new WaveFileWriter(_micFile, _micCap.WaveFormat);
                _micCap.DataAvailable += (_, e) =>
                {
                    if (_gate) WriteAudio(_micWriter, _micCap.WaveFormat, ref _micWritten, e.Buffer, e.BytesRecorded, _micMuted);
                };
                _micCap.StartRecording();
            }
            catch
            {
                _micFile = null;
                _micCap?.Dispose(); _micCap = null;
                _micWriter?.Dispose(); _micWriter = null;
            }
        }
    }

    /// <summary>
    /// Writes audio, first filling any gap with silence so the track always matches the time the recording has run. (Desktop
    /// loopback sends nothing while nothing plays, which would otherwise make the audio drift ahead of the video and end early.)
    /// </summary>
    void WriteAudio(WaveFileWriter? w, WaveFormat f, ref long written, byte[] buf, int count, bool silent)
    {
        if (w == null) return;
        lock (w)
        {
            PadLocked(w, f, ref written);
            if (silent) w.Write(new byte[count], 0, count); else w.Write(buf, 0, count);
            written += count;
        }
    }

    void PadLocked(WaveFileWriter w, WaveFormat f, ref long written, double tolerance = 0.12)
    {
        long expected = (long)(_active.Elapsed.TotalSeconds * f.AverageBytesPerSecond);
        expected -= expected % f.BlockAlign;
        long gap = expected - written;
        if (gap < f.AverageBytesPerSecond * tolerance) return;
        var zeros = new byte[Math.Min(gap, 1 << 16)];
        long left = gap - gap % f.BlockAlign;
        while (left > 0)
        {
            int n = (int)Math.Min(left, zeros.Length);
            w.Write(zeros, 0, n);
            left -= n;
            written += n;
        }
    }

    /// <summary>Called from the timer so a long silence is filled even when no audio arrives at all.</summary>
    void KeepAudioInStep()
    {
        if (!_gate) return;
        if (_sysWriter != null && _sysCap != null) lock (_sysWriter) PadLocked(_sysWriter, _sysCap.WaveFormat, ref _sysWritten);
        if (_micWriter != null && _micCap != null) lock (_micWriter) PadLocked(_micWriter, _micCap.WaveFormat, ref _micWritten);
    }

    int _restarts;
    TimeSpan _stopAt, _segStart;
    readonly List<TimeSpan> _segLens = new();

    void AttachSystem()
    {
        var cap = new WasapiLoopbackCapture();
        if (_sysWriter == null) _sysWriter = new WaveFileWriter(_sysFile!, cap.WaveFormat);
        else if (!_sysWriter.WaveFormat.Equals(cap.WaveFormat)) { cap.Dispose(); Paths.Log("desktop audio device changed format; can't continue that track"); return; }
        _sysCap = cap;
        cap.DataAvailable += (_, e) => { if (_gate) WriteAudio(_sysWriter, cap.WaveFormat, ref _sysWritten, e.Buffer, e.BytesRecorded, false); };
        cap.RecordingStopped += (_, e) =>
        {
            if (_stopping) return;
            Paths.Log("desktop audio capture stopped while recording: " + (e.Exception?.Message ?? "no error"));
            if (_restarts++ >= 30) return;
            // Windows ends loopback capture when the output device changes or restarts: start it again, the gap is filled with silence.
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                if (_stopping) return;
                try { cap.Dispose(); } catch { }
                try { AttachSystem(); } catch (Exception x) { Paths.Log("couldn't restart desktop audio: " + x.Message); }
            });
        };
        cap.StartRecording();
    }

    void StopAudio()
    {
        try
        {
            if (_sysWriter != null && _sysCap != null) lock (_sysWriter) PadLocked(_sysWriter, _sysCap.WaveFormat, ref _sysWritten, 0.01);
            if (_micWriter != null && _micCap != null) lock (_micWriter) PadLocked(_micWriter, _micCap.WaveFormat, ref _micWritten, 0.01);
        }
        catch { }
        try { _sysCap?.StopRecording(); } catch { }
        try { _micCap?.StopRecording(); } catch { }
        try { _silence?.Stop(); _silence?.Dispose(); } catch { }
        _silence = null;
        if (_sysWriter != null) lock (_sysWriter) { _sysWriter.Dispose(); }
        if (_micWriter != null) lock (_micWriter) { _micWriter.Dispose(); }
        _sysWriter = null; _micWriter = null;
        _sysCap?.Dispose(); _micCap?.Dispose();
        _sysCap = null; _micCap = null;
    }

    // ---- put it together -------------------------------------------------------------------------------------------------

    string Finalize()
    {
        var ffmpeg = Ffmpeg.Require();
        var parts = _segments.Select((f, i) => (File: f, Len: i < _segLens.Count ? _segLens[i] : (TimeSpan?)null))
                             .Where(p => File.Exists(p.File) && new FileInfo(p.File).Length > 0).ToList();
        if (parts.Count == 0) throw new InvalidOperationException("Nothing was recorded.");

        string video;
        if (parts.Count == 1) video = parts[0].File;
        else
        {
            var list = Path.Combine(_dir, "parts.txt");
            var lines = new List<string>();
            foreach (var p in parts)
            {
                lines.Add("file '" + p.File.Replace("\\", "/").Replace("'", "'\\''") + "'");
                if (p.Len is { } len) lines.Add("outpoint " + Ffmpeg.N(len.TotalSeconds));        // each piece ends exactly where Pause / Stop was clicked
            }
            File.WriteAllLines(list, lines);
            video = Path.Combine(_dir, "joined.mp4");
            var r = Proc.RunAsync(ffmpeg, new[] { "-y", "-v", "error", "-f", "concat", "-safe", "0", "-i", list, "-c", "copy", video }).GetAwaiter().GetResult();
            if (!r.Ok || !File.Exists(video)) throw new InvalidOperationException("Couldn't join the recording pieces. " + Tail(r.StdErr));
        }

        var probes = new[] { video, _sysFile, _micFile }.Select(x => Task.Run(() => Dur(x))).ToArray();     // in parallel
        Task.WaitAll(probes);
        Paths.Log($"recording: video {probes[0].Result}s, desktop audio {(_s.RecordSystemAudio ? probes[1].Result : "off")}s, mic {(_s.RecordMic ? probes[2].Result : "off")}s, clock {_active.Elapsed.TotalSeconds:0.0}s, restarts {_restarts}");
        var tracks = new List<(string File, AudioRole Role)>();
        if (_sysFile != null && File.Exists(_sysFile) && new FileInfo(_sysFile).Length > 100) tracks.Add((_sysFile, AudioRole.System));
        if (_micFile != null && File.Exists(_micFile) && new FileInfo(_micFile).Length > 100) tracks.Add((_micFile, AudioRole.Mic));

        var final = Path.Combine(_dir, $"Recording {DateTime.Now:yyyy-MM-dd HH.mm.ss}.mp4");
        string end = Ffmpeg.N(Math.Max(0.2, _stopAt.TotalSeconds));
        if (tracks.Count == 0)
        {
            var cut = Proc.RunAsync(ffmpeg, new[] { "-y", "-v", "error", "-i", video, "-t", end, "-c", "copy", "-movflags", "+faststart", final }).GetAwaiter().GetResult();
            if (!cut.Ok || !File.Exists(final)) File.Move(video, final, true);
            return final;
        }

        var args = new List<string> { "-y", "-v", "error", "-i", video };
        foreach (var t in tracks) { args.Add("-i"); args.Add(t.File); }
        var graph = new StringBuilder();
        for (int i = 0; i < tracks.Count; i++) graph.Append($"[{i + 1}:a]apad[a{i}];");
        args.AddRange(new[] { "-filter_complex", graph.ToString().TrimEnd(';'), "-map", "0:v" });
        for (int i = 0; i < tracks.Count; i++) { args.Add("-map"); args.Add($"[a{i}]"); }
        args.AddRange(new[] { "-c:v", "copy", "-c:a", "aac", "-b:a", "160k", "-shortest" });
        for (int i = 0; i < tracks.Count; i++) { args.Add($"-metadata:s:a:{i}"); args.Add("handler_name=" + tracks[i].Role.Tag()); }
        args.AddRange(new[] { "-t", end, "-movflags", "+faststart", final });
        var mux = Proc.RunAsync(ffmpeg, args).GetAwaiter().GetResult();
        if (!mux.Ok || !File.Exists(final)) throw new InvalidOperationException("Couldn't add the audio. " + Tail(mux.StdErr));
        return final;
    }

    static string Dur(string? file)
    {
        try
        {
            if (file == null || !File.Exists(file)) return "-";
            var info = Ffmpeg.ProbeAsync(file).GetAwaiter().GetResult();
            return info == null ? "?" : info.Duration.ToString("0.0");
        }
        catch { return "?"; }
    }

    static string Tail(string s) => s.Length > 300 ? s[^300..] : s;
}
