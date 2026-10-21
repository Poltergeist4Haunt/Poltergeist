using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Poltergeist.Core;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace Poltergeist;

/// <summary>
/// The video editor: trim, split and cut clips, change speed, mute the desktop / microphone tracks, fade the sound, and lay arrows,
/// boxes, text, blur, pictures, GIFs, videos and zoom moves over the picture, with keyframes to move them over time.
/// </summary>
sealed class VideoEditorWindow : ChromeWindow
{
    readonly string _file;
    readonly bool _deleteWhenDone;
    readonly Ffmpeg.MediaInfo _info;
    VideoProject _project;
    readonly AnnotationRenderer _renderer;
    readonly double _vw, _vh;

    readonly MediaElement _media = new()
    {
        LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, ScrubbingEnabled = true, Stretch = Stretch.Uniform,
    };
    readonly SKElement _overlay = new() { IgnorePixelScaling = true, Focusable = true };
    readonly Grid _zoomHost = new() { RenderTransformOrigin = new System.Windows.Point(0, 0) };
    readonly Canvas _editorLayer = new();
    readonly ToolPalette _palette = new(allowZoom: true);
    readonly TimelineView _timeline = new();
    readonly ScrollViewer _tlScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    readonly WrapPanel _inspector = new() { Margin = new Thickness(14, 6, 14, 6) };
    readonly TextBlock _timeText = new() { FontFamily = Theme.Mono, FontSize = 12.5, Foreground = Theme.Text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
    readonly Button _playBtn;
    readonly Button _muteSystem, _muteMic;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(15) };

    readonly Stack<VideoProject> _undo = new();
    readonly Stack<VideoProject> _redo = new();
    readonly Dictionary<Guid, (double Time, SKImage? Image)> _videoStills = new();
    readonly HashSet<Guid> _stillPending = new();

    double _t;
    bool _playing, _opened, _exported;
    Annotation? _draft;
    List<PointD>? _stroke;
    VideoObject? _dragObj;
    HandleTarget _dragTarget;
    PointD _dragOrigin;
    bool _dragSnap;
    TextBox? _editor;
    Guid? _editingId;
    VideoObject? _pendingText;

    EditTool Tool => _palette.Tool;
    double Dpi => VisualTreeHelper.GetDpi(this).DpiScaleX;
    VideoObject? SelectedObject => _timeline.Sel == SelKind.Object ? _project.Objects.FirstOrDefault(o => o.Id == _timeline.SelId) : null;

    public VideoEditorWindow(string file, bool deleteWhenDone)
    {
        _file = file;
        _deleteWhenDone = deleteWhenDone;
        var made = Task.Run(() => ProjectFactory.ForFileAsync(file)).GetAwaiter().GetResult()
                   ?? throw new InvalidOperationException("Couldn't read that video.");
        _project = made.Project;
        _info = made.Info;
        _vw = Math.Max(2, _info.Width);
        _vh = Math.Max(2, _info.Height);
        _renderer = new AnnotationRenderer(new SizeD(_vw, _vh));

        Section = "Edit video";
        var work = SystemParameters.WorkArea;
        Width = work.Width * 0.9; Height = work.Height * 0.92;
        MinWidth = 900; MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // ---- stage: video + overlay
        _zoomHost.Children.Add(_media);
        _zoomHost.Children.Add(_overlay);
        _zoomHost.Children.Add(_editorLayer);
        var stage = new Grid { Background = new SolidColorBrush(Color.FromRgb(0x05, 0x05, 0x05)), ClipToBounds = true };
        stage.Children.Add(_zoomHost);
        _overlay.PaintSurface += OnPaintOverlay;
        _overlay.MouseDown += OnOverlayDown;
        _overlay.MouseMove += OnOverlayMove;
        _overlay.MouseUp += OnOverlayUp;
        _overlay.SizeChanged += (_, _) => { UpdateZoom(); _overlay.InvalidateVisual(); };
        _media.MediaOpened += (_, _) =>
        {
            _opened = true;
            _media.Play(); _media.Pause();                    // shows the first frame
            Seek(0);
        };
        _media.MediaEnded += (_, _) => { Pause(); };
        _media.MediaFailed += (_, e) => Notifier.Error("Can't preview this video", e.ErrorException?.Message ?? "Windows couldn't decode it. You can still trim and upload.");
        _media.Source = new Uri(file);
        // With manual control Windows only starts opening the file once told to play or pause.
        _media.Loaded += (_, _) => { _media.Play(); _media.Pause(); };

        // ---- transport
        _playBtn = Theme.Icon("", "Play / pause (Space)", TogglePlay);
        _playBtn.Width = 40; _playBtn.Height = 36; _playBtn.FontSize = 16; _playBtn.Foreground = Theme.Accent;
        var back = Theme.Icon("", "Previous frame (←)", () => StepFrames(-1));
        var fwd = Theme.Icon("", "Next frame (→)", () => StepFrames(1));
        var split = Theme.Pill("Split", Split, false);
        split.ToolTip = "Split the clip (or the selected item) at the playhead (S)";
        var del = Theme.Pill("Delete", DeleteSelected, false);
        del.ToolTip = "Cut out the selected clip, or delete the selected item (Del)";
        var speed = Theme.Pill("+ Speed", AddSpeed, false);
        speed.ToolTip = "Make the part from the playhead faster or slower";
        _muteSystem = Theme.Pill("Mute desktop", () => AddMute(AudioRole.System), false);
        _muteMic = Theme.Pill("Mute mic", () => AddMute(AudioRole.Mic), false);
        _muteSystem.Visibility = _project.AudioRoles.Contains(AudioRole.System) ? Visibility.Visible : Visibility.Collapsed;
        _muteMic.Visibility = _project.AudioRoles.Contains(AudioRole.Mic) ? Visibility.Visible : Visibility.Collapsed;
        var addMedia = Theme.Pill("+ Image / video", ChooseMedia, false);
        addMedia.ToolTip = "Put a picture, GIF or video on top (you can also paste or drop one)";
        var key = Theme.Pill("◆ Keyframe", AddKeyframe, false);
        key.ToolTip = "Record the selected object's position at the playhead (K)";
        foreach (var b in new[] { split, del, speed, _muteSystem, _muteMic, addMedia, key }) { b.Padding = new Thickness(12, 5, 12, 5); b.Margin = new Thickness(0, 0, 6, 0); b.FontSize = 12; }
        var zoomOut = Theme.Icon("", "Zoom timeline out", () => ZoomTimeline(1 / 1.3));
        var zoomIn = Theme.Icon("", "Zoom timeline in", () => ZoomTimeline(1.3));

        var transport = new WrapPanel { Margin = new Thickness(10, 6, 10, 6) };
        void Dock(UIElement e) { if (e is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center; transport.Children.Add(e); }
        Dock(back); Dock(_playBtn); Dock(fwd); Dock(_timeText);
        foreach (var b in new UIElement[] { split, del, speed, _muteSystem, _muteMic, addMedia, key }) Dock(b);
        Dock(zoomOut); Dock(zoomIn);
        var transportWrap = new Border { Background = Theme.Card, BorderBrush = Theme.Border, BorderThickness = new Thickness(0, 1, 0, 0), Child = transport };

        // ---- timeline
        _timeline.Project = _project;
        _tlScroll.Content = _timeline;
        _tlScroll.ScrollChanged += (_, e) => _timeline.ScrollOffset = _tlScroll.HorizontalOffset;
        _timeline.Scrub += t => Seek(t);
        _timeline.SelectionChanged += () => { RefreshInspector(); _overlay.InvalidateVisual(); };
        _timeline.BeforeEdit += Snapshot;
        _timeline.Edited += () => { UpdateLabels(); RefreshInspectorValues(); _overlay.InvalidateVisual(); };
        _timeline.ContextRequested += ShowContextMenu;
        _timeline.Zoomed += (t, vx) =>
        {
            _tlScroll.UpdateLayout();
            _tlScroll.ScrollToHorizontalOffset(Math.Max(0, 12 + t * _timeline.PxPerSec - vx));
        };
        var tlBorder = new Border { Background = Theme.Bg, BorderBrush = Theme.Border, BorderThickness = new Thickness(0, 1, 0, 0), Child = _tlScroll };

        var inspectorWrap = new Border { Background = Theme.Card, BorderBrush = Theme.Border, BorderThickness = new Thickness(0, 1, 0, 0), Child = _inspector, MinHeight = 44 };

        // ---- bottom bar
        var cancel = Theme.Pill("Cancel", Close, prominent: false);
        var upload = new SplitButton("Upload", () => Finish(false), ("Copy to clipboard", () => Finish(true)));
        var hint = Theme.Label("Space plays  ·  S splits  ·  ← → step a frame  ·  I / O set the trim  ·  Ctrl+scroll zooms the timeline", 12, Theme.Muted);
        hint.VerticalAlignment = VerticalAlignment.Center; hint.Margin = new Thickness(14, 0, 14, 0); hint.TextTrimming = TextTrimming.CharacterEllipsis;
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        cancel.Margin = new Thickness(0, 0, 8, 0);
        right.Children.Add(cancel); right.Children.Add(upload);
        var bottom = new Grid { Height = 56, Background = Theme.Card };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottom.Children.Add(hint); Grid.SetColumn(right, 1); bottom.Children.Add(right);
        var bottomWrap = new Border { BorderBrush = Theme.Border, BorderThickness = new Thickness(0, 1, 0, 0), Child = bottom };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // palette
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 200 });   // stage
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // transport
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // inspector
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(210) });                   // timeline
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // buttons
        root.Children.Add(_palette);
        void Put(UIElement e, int row) { Grid.SetRow(e, row); root.Children.Add(e); }
        Put(stage, 1); Put(transportWrap, 2); Put(inspectorWrap, 3); Put(tlBorder, 4); Put(bottomWrap, 5);
        Body = root;

        _palette.Changed += OnPaletteChanged;
        _palette.SetSoftVisible(false);

        _timer.Tick += (_, _) => Tick();
        AllowDrop = true;
        Drop += async (_, e) => await DropFiles(e);
        PreviewKeyDown += OnKey;
        Closed += OnClosed;
        Loaded += (_, _) =>
        {
            _timeline.PxPerSec = Math.Max(20, Math.Min(240, (ActualWidth - 60) / Math.Max(1, _project.Duration)));
            RefreshInspector();
            UpdateLabels();
            _overlay.Focus();
            LoadWaveforms();
        };
    }

    async void LoadWaveforms()
    {
        for (int i = 0; i < _project.AudioRoles.Count; i++)
        {
            var role = _project.AudioRoles[i];
            if (_timeline.Waves.ContainsKey(role)) continue;
            var peaks = await Waveform.LoadAsync(_file, i);
            if (peaks != null) { _timeline.Waves[role] = peaks; _timeline.InvalidateVisual(); }
        }
    }

    // ---- playback --------------------------------------------------------------------------------------------------------

    void TogglePlay() { if (_playing) Pause(); else Play(); }

    void Play()
    {
        if (!_opened) return;
        if (_t >= _project.TrimEnd - 0.05) Seek(_project.TrimStart);
        _playing = true;
        _media.Play();
        _timer.Start();
        _playBtn.Content = "";
    }

    void Pause()
    {
        if (!_playing && _playBtn.Content as string == "") return;
        _playing = false;
        _media.Pause();
        _timer.Stop();
        _playBtn.Content = "";
        RefreshVideoStills();
    }

    void Tick()
    {
        if (!_playing) return;
        double t = _media.Position.TotalSeconds;
        // skip clips that were cut out
        if (_project.CutAt(t) is { } cut)
        {
            if (cut.End >= _project.TrimEnd - 0.02) { Seek(_project.TrimEnd); Pause(); return; }
            _media.Position = TimeSpan.FromSeconds(cut.End);
            t = cut.End;
        }
        if (t >= _project.TrimEnd - 0.01) { Seek(_project.TrimEnd); Pause(); return; }
        if (t < _project.TrimStart - 0.05) { _media.Position = TimeSpan.FromSeconds(_project.TrimStart); t = _project.TrimStart; }
        var rate = _project.RateAt(t);
        if (Math.Abs(_media.SpeedRatio - rate) > 0.001) _media.SpeedRatio = rate;
        ApplyPreviewVolume(t);
        _t = t;
        _timeline.Playhead = t;
        EnsurePlayheadVisible();
        UpdateZoom();
        UpdateLabels();
        _overlay.InvalidateVisual();
    }

    void ApplyPreviewVolume(double t)
    {
        // Windows' player only plays the first audio track, so the preview honours mutes on that one.
        var first = _project.AudioRoles.Count > 0 ? _project.AudioRoles[0] : AudioRole.Unknown;
        bool muted = _project.MutedRanges(first, false).Any(r => t >= r.Start && t < r.End);
        double fade = 1;
        if (_project.AudioFadeIn > 0) fade = Math.Min(fade, (t - _project.TrimStart) / _project.AudioFadeIn);
        if (_project.AudioFadeOut > 0) fade = Math.Min(fade, (_project.TrimEnd - t) / _project.AudioFadeOut);
        _media.Volume = muted ? 0 : Math.Min(1, Math.Max(0, fade));
    }

    void Seek(double t)
    {
        t = Math.Min(Math.Max(t, 0), _project.Duration);
        _t = t;
        if (_opened) _media.Position = TimeSpan.FromSeconds(t);
        _timeline.Playhead = t;
        EnsurePlayheadVisible();
        UpdateZoom();
        UpdateLabels();
        RefreshVideoStills();
        _overlay.InvalidateVisual();
    }

    void StepFrames(int n)
    {
        if (_playing) Pause();
        double step = 1.0 / Math.Max(1, _info.Fps);
        Seek(_t + n * step);
    }

    void EnsurePlayheadVisible()
    {
        double x = 12 + _t * _timeline.PxPerSec;
        double left = _tlScroll.HorizontalOffset, w = _tlScroll.ViewportWidth;
        if (w <= 0) return;
        if (x < left + 20) _tlScroll.ScrollToHorizontalOffset(Math.Max(0, x - 40));
        else if (x > left + w - 20) _tlScroll.ScrollToHorizontalOffset(x - w + 80);
    }

    void ZoomTimeline(double k)
    {
        var t = _t;
        double vx = 12 + t * _timeline.PxPerSec - _tlScroll.HorizontalOffset;
        _timeline.PxPerSec *= k;
        _tlScroll.UpdateLayout();
        _tlScroll.ScrollToHorizontalOffset(Math.Max(0, 12 + t * _timeline.PxPerSec - vx));
    }

    void UpdateLabels()
    {
        var o = _project.OutputDuration;
        _timeText.Text = $"{TimelineView.Format(_t, true)} / {TimelineView.Format(_project.Duration, true)}   →  {TimelineView.Format(o, true)}";
    }

    /// <summary>The camera: a zoom object makes the preview fill the frame with its region.</summary>
    void UpdateZoom()
    {
        var host = _zoomHost;
        var r = _project.ZoomRect(_t, new SizeD(_vw, _vh));
        double w = _overlay.ActualWidth, h = _overlay.ActualHeight;
        if (r == null || w < 2 || h < 2 || r.Value.Width < 1) { host.RenderTransform = Transform.Identity; return; }
        double s = Math.Min(w / _vw, h / _vh);
        double fx = (w - _vw * s) / 2, fy = (h - _vh * s) / 2;
        double k = _vw / r.Value.Width;
        double tx = fx * (1 - k) - r.Value.X * k * s;
        double ty = fy * (1 - k) - r.Value.Y * k * s;
        host.RenderTransform = new MatrixTransform(k, 0, 0, k, tx, ty);
    }

    // ---- undo ------------------------------------------------------------------------------------------------------------

    void Snapshot()
    {
        _undo.Push(_project.Clone());
        _redo.Clear();
    }

    void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(_project.Clone());
        SwapProject(_undo.Pop());
    }

    void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(_project.Clone());
        SwapProject(_redo.Pop());
    }

    void SwapProject(VideoProject p)
    {
        _project = p;
        _timeline.Project = p;
        if (_timeline.Sel != SelKind.None && !Exists(_timeline.Sel, _timeline.SelId)) _timeline.ClearSelection();
        RefreshInspector(); UpdateLabels(); UpdateZoom(); _overlay.InvalidateVisual();
    }

    bool Exists(SelKind k, Guid id) => k switch
    {
        SelKind.Segment => _project.Segments.Any(s => s.Id == id),
        SelKind.Speed => _project.Speeds.Any(s => s.Id == id),
        SelKind.Mute => _project.Mutes.Any(m => m.Id == id),
        SelKind.Object => _project.Objects.Any(o => o.Id == id),
        SelKind.AudioClip => true,
        _ => false,
    };

    // ---- editing actions ---------------------------------------------------------------------------------------------------

    void Edited() { _timeline.InvalidateMeasure(); _timeline.InvalidateVisual(); UpdateLabels(); RefreshInspector(); _overlay.InvalidateVisual(); }

    void Split()
    {
        double t = _t;
        const double edge = 0.05;
        Snapshot();
        bool did = false;
        if (_timeline.Sel == SelKind.AudioClip && t > _timeline.SelClipStart + edge && t < _timeline.SelClipEnd - edge)
        {
            _project.AudioSplits.Add(new AudioSplit { Role = _timeline.SelRole, Time = t });
            var (ca, cb) = _timeline.AudioClipAt(_timeline.SelRole, t);
            _timeline.SelectAudio(_timeline.SelRole, ca, cb);
            did = true;
        }
        else if (SelectedObject is { } o) did = SplitObject(o, t);
        else if (_timeline.Sel == SelKind.Speed && _project.Speeds.FirstOrDefault(s => s.Id == _timeline.SelId) is { } sp && t > sp.Start + edge && t < sp.End - edge)
        {
            var right = new SpeedRegion { Start = t, End = sp.End, Rate = sp.Rate };
            sp.End = t;
            _project.Speeds.Insert(_project.Speeds.IndexOf(sp) + 1, right);
            did = true;
        }
        else if (_timeline.Sel == SelKind.Mute && _project.Mutes.FirstOrDefault(m => m.Id == _timeline.SelId) is { } mu && t > mu.Start + edge && t < mu.End - edge)
        {
            var right = new AudioMute { Role = mu.Role, Start = t, End = mu.End };
            mu.End = t;
            _project.Mutes.Insert(_project.Mutes.IndexOf(mu) + 1, right);
            did = true;
        }
        else
        {
            var seg = _project.Segments.FirstOrDefault(s => t > s.Start + edge && t < s.End - edge);
            if (seg != null)
            {
                var right = new Segment { Start = t, End = seg.End, Removed = seg.Removed };
                seg.End = t;
                _project.Segments.Insert(_project.Segments.IndexOf(seg) + 1, right);
                _timeline.Select(SelKind.Segment, right.Id);
                did = true;
            }
        }
        if (!did) { _undo.Pop(); return; }
        Edited();
    }

    bool SplitObject(VideoObject o, double t)
    {
        if (t <= o.Start + 0.05 || t >= o.End - 0.05) return false;
        var g = o.GeometryAt(t);
        var right = o.Clone();
        right.Id = Guid.NewGuid();
        right.Start = t;
        right.MediaOffset = o.MediaOffset + (t - o.Start);
        right.FadeIn = 0;
        right.Keyframes = new List<Keyframe> { new(t, g.P1, g.P2) };
        right.Keyframes.AddRange(o.Keyframes.Where(k => k.Time > t));
        var leftKeys = o.Keyframes.Where(k => k.Time < t).ToList();
        leftKeys.Add(new Keyframe(t, g.P1, g.P2));
        o.Keyframes = leftKeys;
        o.End = t;
        o.FadeOut = 0;
        _project.Objects.Insert(_project.Objects.IndexOf(o) + 1, right);
        return true;
    }

    void DeleteSelected()
    {
        switch (_timeline.Sel)
        {
            case SelKind.Segment:
                var seg = _project.Segments.FirstOrDefault(s => s.Id == _timeline.SelId);
                if (seg == null) return;
                if (!seg.Removed && _project.Segments.Count(s => !s.Removed) <= 1) { Notifier.Info("Can't cut out everything", "Keep at least one clip."); return; }
                Snapshot();
                seg.Removed = !seg.Removed;
                break;
            case SelKind.AudioClip:
                Snapshot();
                var mute = new AudioMute { Role = _timeline.SelRole, Start = _timeline.SelClipStart, End = _timeline.SelClipEnd };
                _project.Mutes.Add(mute);
                _timeline.Select(SelKind.Mute, mute.Id);
                break;
            case SelKind.Speed:
                Snapshot(); _project.Speeds.RemoveAll(s => s.Id == _timeline.SelId); _timeline.ClearSelection(); break;
            case SelKind.Mute:
                Snapshot(); _project.Mutes.RemoveAll(m => m.Id == _timeline.SelId); _timeline.ClearSelection(); break;
            case SelKind.Object:
                var o = SelectedObject;
                if (o == null) return;
                Snapshot();
                if (_timeline.SelKey >= 0 && o.Keyframes.Count > 1 && _timeline.SelKey < o.Keyframes.Count) { o.Keyframes.RemoveAt(_timeline.SelKey); _timeline.Select(SelKind.Object, o.Id); }
                else { _project.Objects.Remove(o); _timeline.ClearSelection(); }
                break;
            default: return;
        }
        Edited();
    }

    void AddSpeed()
    {
        Snapshot();
        double s = _t, e = Math.Min(_project.Duration, s + 3);
        if (e - s < 0.5) { e = _project.Duration; s = Math.Max(0, e - 3); }
        var r = new SpeedRegion { Start = s, End = e, Rate = 2 };
        _project.Speeds.Add(r);
        _timeline.Select(SelKind.Speed, r.Id);
        Edited();
    }

    void AddMute(AudioRole role)
    {
        Snapshot();
        double s = _t, e = Math.Min(_project.Duration, s + 3);
        if (e - s < 0.5) { e = _project.Duration; s = Math.Max(0, e - 3); }
        var m = new AudioMute { Role = role, Start = s, End = e };
        _project.Mutes.Add(m);
        _timeline.Select(SelKind.Mute, m.Id);
        Edited();
    }

    void AddKeyframe()
    {
        if (SelectedObject is not { } o || _t < o.Start || _t > o.End) return;
        Snapshot();
        var g = o.GeometryAt(_t);
        o.SetKeyframe(_t, g.P1, g.P2);
        Edited();
    }

    VideoObject NewObject(AnnotationKind kind, PointD p1, PointD p2)
    {
        double len = kind == AnnotationKind.Zoom ? 4 : 3;
        double end = Math.Min(_project.Duration, _t + len);
        double start = _t;
        if (end - start < 0.4) { end = _project.Duration; start = Math.Max(0, end - len); }
        return new VideoObject
        {
            Kind = kind, Color = _palette.Color, Level = _palette.Level, SoftBlur = _palette.Soft, Start = start, End = end,
            Keyframes = new List<Keyframe> { new(start, p1, p2) },
            FadeIn = kind == AnnotationKind.Zoom ? 0.6 : 0, FadeOut = kind == AnnotationKind.Zoom ? 0.6 : 0,
        };
    }

    void AddObject(VideoObject o)
    {
        Snapshot();
        _project.Objects.Add(o);
        _timeline.Select(SelKind.Object, o.Id);
        Edited();
    }

    // ---- media ---------------------------------------------------------------------------------------------------------------

    void ChooseMedia()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Add a picture, GIF or video", Multiselect = true,
            Filter = "Pictures, GIFs and videos|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif;*.mp4;*.mov;*.m4v;*.webm;*.mkv;*.avi|All files|*.*",
        };
        if (dlg.ShowDialog() == true) foreach (var f in dlg.FileNames) _ = AddMediaFile(f, null);
    }

    async Task DropFiles(DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        var p = ToVideo(e.GetPosition(_overlay));
        foreach (var f in files) await AddMediaFile(f, p);
    }

    async Task AddMediaFile(string path, PointD? at)
    {
        var clip = await MediaClip.LoadAsync(path);
        if (clip == null) { Notifier.Info("Can't use that file", "Poltergeist couldn't read " + Path.GetFileName(path)); return; }
        double k = Math.Min(1, Math.Min(_vw * 0.4 / clip.PixelWidth, _vh * 0.4 / clip.PixelHeight));
        double w = clip.PixelWidth * k, h = clip.PixelHeight * k;
        var c = at ?? new PointD(_vw / 2, _vh / 2);
        double x = Math.Min(Math.Max(c.X - w / 2, 0), Math.Max(0, _vw - w)), y = Math.Min(Math.Max(c.Y - h / 2, 0), Math.Max(0, _vh - h));
        var o = NewObject(AnnotationKind.Media, new PointD(x, y), new PointD(x + w, y + h));
        o.Media = clip;
        if (clip.Duration > 0) o.End = Math.Min(_project.Duration, o.Start + clip.Duration);
        AddObject(o);
        _palette.SetTool(EditTool.Select);
    }

    void PasteMedia()
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                foreach (string f in Clipboard.GetFileDropList()) _ = AddMediaFile(f, null);
                return;
            }
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } src)
            {
                var file = Path.Combine(Paths.Temp, $"Pasted {DateTime.Now:HHmmss}.png");
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(src));
                using (var fs = File.Create(file)) enc.Save(fs);
                _ = AddMediaFile(file, null);
            }
        }
        catch { }
    }

    // ---- video stills for picture-in-picture layers ----------------------------------------------------------------------

    void RefreshVideoStills()
    {
        foreach (var o in _project.Objects.Where(o => o.Kind == AnnotationKind.Media && o.Media is { Kind: MediaKind.Video } && o.IsVisible(_t)))
        {
            var clip = o.Media!;
            double ct = _t - o.Start + o.MediaOffset;
            if (_videoStills.TryGetValue(o.Id, out var have) && Math.Abs(have.Time - ct) < 1.0 / 30) continue;
            if (!_stillPending.Add(o.Id)) continue;
            var id = o.Id;
            Task.Run(() =>
            {
                var img = clip.FrameAt(ct);
                Dispatcher.BeginInvoke(() => { _videoStills[id] = (ct, img); _stillPending.Remove(id); _overlay.InvalidateVisual(); });
            });
        }
    }

    Annotation AnnotationFor(VideoObject o)
    {
        if (o.Kind == AnnotationKind.Media && o.Media is { Kind: MediaKind.Video })
        {
            var bare = o.Clone();
            bare.Media = null;
            return bare.AnnotationAt(_t) with { Image = _videoStills.TryGetValue(o.Id, out var s) ? s.Image : null };
        }
        return o.AnnotationAt(_t);
    }

    // ---- overlay painting ----------------------------------------------------------------------------------------------------

    (double Scale, double Ox, double Oy) Fit(double pxW, double pxH)
    {
        double s = Math.Min(pxW / _vw, pxH / _vh);
        return (s, (pxW - _vw * s) / 2, (pxH - _vh * s) / 2);
    }

    PointD ToVideo(System.Windows.Point dip)
    {
        var (s, ox, oy) = Fit(_overlay.ActualWidth * Dpi, _overlay.ActualHeight * Dpi);
        return new PointD((dip.X * Dpi - ox) / s, (dip.Y * Dpi - oy) / s);
    }

    System.Windows.Point ToView(PointD p)
    {
        var (s, ox, oy) = Fit(_overlay.ActualWidth * Dpi, _overlay.ActualHeight * Dpi);
        return new System.Windows.Point((p.X * s + ox) / Dpi, (p.Y * s + oy) / Dpi);
    }

    void OnPaintOverlay(object? sender, SKPaintSurfaceEventArgs e)
    {
        var c = e.Surface.Canvas;
        c.Clear(SKColors.Transparent);
        var (s, ox, oy) = Fit(e.Info.Width, e.Info.Height);
        c.Save();
        c.Translate((float)ox, (float)oy);
        c.Scale((float)s);

        foreach (var o in _project.Objects)
        {
            if (!o.IsVisible(_t) || o.Kind == AnnotationKind.Zoom || o.Id == _editingId) continue;
            var a = AnnotationFor(o);
            if (a.Kind == AnnotationKind.Blur) DrawBlurPlaceholder(c, a, s);
            else _renderer.Draw(c, a);
        }
        if (_draft != null)
        {
            if (_draft.Kind == AnnotationKind.Blur) DrawBlurPlaceholder(c, _draft, s);
            else if (_draft.Kind == AnnotationKind.Zoom) DrawZoomFrame(c, _draft.Rect, s, true);
            else _renderer.Draw(c, _draft);
        }
        foreach (var z in _project.Objects.Where(o => o.Kind == AnnotationKind.Zoom && o.IsVisible(_t)))
            if (Tool == EditTool.Zoom || z.Id == _timeline.SelId)
            {
                var g = z.GeometryAt(_t);
                DrawZoomFrame(c, RectD.FromCorners(g.P1, g.P2), s, z.Id == _timeline.SelId);
            }
        if (SelectedObject is { } sel && sel.IsVisible(_t) && sel.Id != _editingId) DrawSelection(c, sel, s);
        c.Restore();
    }

    static void DrawBlurPlaceholder(SKCanvas c, Annotation a, double s)
    {
        using var fill = new SKPaint { Color = new SKColor(150, 150, 160, 90) };
        using var edge = new SKPaint { Color = new SKColor(255, 255, 255, 140), Style = SKPaintStyle.Stroke, StrokeWidth = (float)(1.5 / s), IsAntialias = true, PathEffect = SKPathEffect.CreateDash(new[] { (float)(5 / s), (float)(4 / s) }, 0) };
        c.DrawRect(a.Rect.ToSk(), fill);
        c.DrawRect(a.Rect.ToSk(), edge);
        using var font = new SKFont(SKTypeface.Default, (float)(12 / s));
        using var txt = new SKPaint { Color = new SKColor(255, 255, 255, 200), IsAntialias = true };
        c.DrawText("Blur (applied when exported)", (float)(a.Rect.X + 6 / s), (float)(a.Rect.Y + 16 / s), font, txt);
    }

    static void DrawZoomFrame(SKCanvas c, RectD r, double s, bool strong)
    {
        using var p = new SKPaint { Color = new SKColor(0x5a, 0xc8, 0xfa, (byte)(strong ? 255 : 150)), Style = SKPaintStyle.Stroke, StrokeWidth = (float)(2 / s), IsAntialias = true, PathEffect = SKPathEffect.CreateDash(new[] { (float)(7 / s), (float)(5 / s) }, 0) };
        c.DrawRect(r.ToSk(), p);
        using var font = new SKFont(SKTypeface.Default, (float)(12 / s));
        using var t = new SKPaint { Color = new SKColor(0x5a, 0xc8, 0xfa), IsAntialias = true };
        c.DrawText("Zoom", (float)(r.X + 6 / s), (float)(r.Y + 16 / s), font, t);
    }

    void DrawSelection(SKCanvas c, VideoObject o, double s)
    {
        var a = o.AnnotationAt(_t);
        float z = (float)s;
        using var line = new SKPaint { Color = new SKColor(0xf2, 0x7d, 0xb3), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f / z, IsAntialias = true, PathEffect = SKPathEffect.CreateDash(new[] { 5f / z, 4f / z }, 0) };
        using var fill = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var ring = new SKPaint { Color = new SKColor(0xf2, 0x7d, 0xb3), Style = SKPaintStyle.Stroke, StrokeWidth = 2f / z, IsAntialias = true };
        var b = _renderer.BoundsOf(a);
        if (a.Kind != AnnotationKind.Arrow && a.Kind != AnnotationKind.Zoom) c.DrawRect(b.Inflate(2 / s).ToSk(), line);
        float r = 5f / z;
        IEnumerable<PointD> pts = a.Kind == AnnotationKind.Text ? new[] { new PointD(b.MaxX, b.MaxY) } : new[] { a.P1, a.P2 };
        foreach (var p in pts) { c.DrawCircle(p.ToSk(), r, fill); c.DrawCircle(p.ToSk(), r, ring); }
    }

    // ---- overlay mouse ------------------------------------------------------------------------------------------------------

    void OnOverlayDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (_editor != null) CommitText();
        _overlay.Focus();
        var p = ToVideo(e.GetPosition(_overlay));
        var (sc, _, _) = Fit(_overlay.ActualWidth * Dpi, _overlay.ActualHeight * Dpi);
        double tol = 9 * Dpi / sc;

        if (e.ClickCount == 2 && HitObject(p, tol) is { Kind: AnnotationKind.Text } txt) { _timeline.Select(SelKind.Object, txt.Id); BeginTextEdit(txt); return; }

        if (SelectedObject is { } cur && cur.IsVisible(_t) && HandleAt(cur, p, tol) is { } ht)
        {
            StartDrag(cur, ht, p);
            _overlay.CaptureMouse();
            return;
        }

        if (SelectedObject is { } picked && picked.IsVisible(_t) && picked.Kind != AnnotationKind.Zoom && _renderer.Hits(picked.AnnotationAt(_t), p, tol))
        {
            StartDrag(picked, HandleTarget.Body, p);
            _overlay.CaptureMouse();
            return;
        }

        if (Tool == EditTool.Select)
        {
            var hit = HitObject(p, tol);
            if (hit != null)
            {
                _timeline.Select(SelKind.Object, hit.Id);
                SyncPalette(hit);
                StartDrag(hit, HandleTarget.Body, p);
                _overlay.CaptureMouse();
            }
            else _timeline.ClearSelection();
            _overlay.InvalidateVisual();
            return;
        }

        _timeline.ClearSelection();
        if (Tool == EditTool.Text)
        {
            var t = NewObject(AnnotationKind.Text, p, p);
            BeginTextEdit(t, isNew: true);
            return;
        }
        if (Tool.IsFreehand()) { _stroke = new List<PointD> { p }; _draft = null; }
        else _draft = new Annotation { Kind = Tool.Kind()!.Value, P1 = p, P2 = p, Color = _palette.Color, Level = _palette.Level, Soft = _palette.Soft };
        _overlay.CaptureMouse();
        _overlay.InvalidateVisual();
    }

    void OnOverlayMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var p = ToVideo(e.GetPosition(_overlay));
        var (sc, _, _) = Fit(_overlay.ActualWidth * Dpi, _overlay.ActualHeight * Dpi);

        if (_dragObj is { } start)
        {
            double dx = p.X - _dragOrigin.X, dy = p.Y - _dragOrigin.Y;
            if (!_dragSnap && Math.Abs(dx) + Math.Abs(dy) < 2 / sc) return;
            if (!_dragSnap) { Snapshot(); _dragSnap = true; }
            var moved = start.Moved(_dragTarget, dx, dy, _t, !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), _vw / _vh);
            int i = _project.Objects.FindIndex(x => x.Id == start.Id);
            if (i >= 0) _project.Objects[i] = moved;
            _timeline.InvalidateVisual();
            _overlay.InvalidateVisual();
            return;
        }
        if (_stroke != null)
        {
            if (_stroke[^1].DistanceTo(p) > 1.5 / sc) _stroke.Add(p);
            _draft = Annotation.Freehand(Tool.Kind()!.Value, _stroke, _palette.Color, _palette.Level);
            _overlay.InvalidateVisual();
            return;
        }
        if (_draft != null)
        {
            var p1 = _draft.P1;
            var p2 = p;
            if (_draft.Kind == AnnotationKind.Zoom) Annotation.LockAspect(ref p1, ref p2, HandleTarget.P2, _vw / _vh);
            _draft = _draft with { P2 = p2 };
            _overlay.InvalidateVisual();
        }
    }

    void OnOverlayUp(object sender, MouseButtonEventArgs e)
    {
        _overlay.ReleaseMouseCapture();
        if (_dragObj != null) { _dragObj = null; Edited(); return; }

        var d = _draft;
        _draft = null;
        var stroke = _stroke;
        _stroke = null;
        if (d == null) { _overlay.InvalidateVisual(); return; }
        var (sc, _, _) = Fit(_overlay.ActualWidth * Dpi, _overlay.ActualHeight * Dpi);
        bool ok = d.IsFreehand || (d.Kind == AnnotationKind.Arrow ? d.P1.DistanceTo(d.P2) > 6 / sc
                                  : Math.Abs(d.P2.X - d.P1.X) > 4 / sc && Math.Abs(d.P2.Y - d.P1.Y) > 4 / sc);
        if (ok)
        {
            var o = NewObject(d.Kind, d.P1, d.P2);
            if (d.IsFreehand) o.Path = d.Path.ToList();
            AddObject(o);
        }
        _overlay.InvalidateVisual();
    }

    VideoObject? HitObject(PointD p, double tol)
    {
        for (int i = _project.Objects.Count - 1; i >= 0; i--)
        {
            var o = _project.Objects[i];
            if (!o.IsVisible(_t)) continue;
            var a = o.AnnotationAt(_t);
            if (o.Kind == AnnotationKind.Zoom)
            {
                if (o.Id == _timeline.SelId && a.Rect.Inflate(tol).Contains(p)) return o;
                continue;
            }
            if (_renderer.Hits(a, p, tol)) return o;
        }
        return null;
    }

    HandleTarget? HandleAt(VideoObject o, PointD p, double tol)
    {
        var a = o.AnnotationAt(_t);
        if (a.Kind == AnnotationKind.Text)
        {
            var b = _renderer.BoundsOf(a);
            return new PointD(b.MaxX, b.MaxY).DistanceTo(p) <= tol * 0.8 ? HandleTarget.Body : null;
        }
        if (a.P1.DistanceTo(p) <= tol) return HandleTarget.P1;
        if (a.P2.DistanceTo(p) <= tol) return HandleTarget.P2;
        return null;
    }

    void StartDrag(VideoObject o, HandleTarget t, PointD origin)
    {
        _dragObj = o.Clone(); _dragTarget = t; _dragOrigin = origin; _dragSnap = false;
    }

    void SyncPalette(VideoObject o) => _palette.ShowStyleOf(o.Kind is AnnotationKind.Media or AnnotationKind.Blur or AnnotationKind.Zoom ? _palette.Color : o.Color, o.Level, o.SoftBlur);

    void OnPaletteChanged(string what)
    {
        _palette.SetSoftVisible(Tool == EditTool.Blur || SelectedObject?.Kind == AnnotationKind.Blur);
        if (what == "tool") { _overlay.Cursor = Tool == EditTool.Select ? Cursors.Arrow : Cursors.Cross; return; }
        if (SelectedObject is { } o)
        {
            Snapshot();
            switch (what)
            {
                case "color" when o.Kind is not (AnnotationKind.Blur or AnnotationKind.Media or AnnotationKind.Zoom): o.Color = _palette.Color; break;
                case "level": o.Level = _palette.Level; break;
                case "soft" when o.Kind == AnnotationKind.Blur: o.SoftBlur = _palette.Soft; break;
                default: _undo.Pop(); return;
            }
            Edited();
        }
    }

    // ---- text ------------------------------------------------------------------------------------------------------------

    void BeginTextEdit(VideoObject o, bool isNew = false)
    {
        CommitText();
        _editingId = isNew ? null : o.Id;
        _pendingText = isNew ? o : null;
        var a = o.AnnotationAt(_t);
        var (sc, _, _) = Fit(_overlay.ActualWidth * Dpi, _overlay.ActualHeight * Dpi);
        double sizeDip = Math.Max(10, _renderer.FontSize(o.Level) * sc / Dpi);
        var tl = ToView(a.P1);
        _editor = new TextBox
        {
            Text = o.Text, FontSize = sizeDip, FontWeight = FontWeights.Bold, FontFamily = new FontFamily("Segoe UI"),
            Foreground = new SolidColorBrush(Color.FromRgb(o.Color.R, o.Color.G, o.Color.B)), CaretBrush = Theme.Accent,
            Background = new SolidColorBrush(Color.FromArgb(130, 0, 0, 0)), BorderBrush = Theme.AccentLine, BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 0, 4, 0), MinWidth = 60, AcceptsReturn = false,
        };
        var tpl = new ControlTemplate(typeof(TextBox));
        var bd = new FrameworkElementFactory(typeof(Border));
        bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        bd.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        bd.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        var sv = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        sv.SetValue(Control.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        bd.AppendChild(sv);
        tpl.VisualTree = bd;
        _editor.Template = tpl;
        Canvas.SetLeft(_editor, tl.X - 4);
        Canvas.SetTop(_editor, tl.Y - 2);
        _editor.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitText(); e.Handled = true; }
            else if (e.Key == Key.Escape) { CancelText(); e.Handled = true; }
        };
        _editor.LostKeyboardFocus += (_, _) => CommitText();
        _editorLayer.Children.Add(_editor);
        _editor.Focus();
        _editor.SelectAll();
        _overlay.InvalidateVisual();
    }

    void CommitText()
    {
        if (_editor == null) return;
        var ed = _editor;
        _editor = null;
        _editorLayer.Children.Remove(ed);
        var text = ed.Text.Trim();
        var id = _editingId;
        var fresh = _pendingText;
        _editingId = null; _pendingText = null;
        if (fresh != null)
        {
            if (text.Length > 0) { fresh.Text = text; AddObject(fresh); }
        }
        else if (id is { } eid && _project.Objects.FirstOrDefault(o => o.Id == eid) is { } existing)
        {
            if (text.Length == 0) { Snapshot(); _project.Objects.Remove(existing); _timeline.ClearSelection(); Edited(); }
            else if (text != existing.Text) { Snapshot(); existing.Text = text; Edited(); }
        }
        _overlay.InvalidateVisual();
        _overlay.Focus();
    }

    void CancelText()
    {
        if (_editor == null) return;
        _editorLayer.Children.Remove(_editor);
        _editor = null; _editingId = null; _pendingText = null;
        _overlay.InvalidateVisual();
        _overlay.Focus();
    }

    // ---- inspector -------------------------------------------------------------------------------------------------------

    bool _refreshing;

    void RefreshInspector()
    {
        _refreshing = true;
        _inspector.Children.Clear();
        void Add(UIElement e) { if (e is FrameworkElement fe && fe.Margin == default) fe.Margin = new Thickness(0, 0, 14, 0); if (e is FrameworkElement f2) f2.VerticalAlignment = VerticalAlignment.Center; _inspector.Children.Add(e); }
        TextBlock Lab(string s) => Theme.Label(s, 12, Theme.Muted);

        Slider Slide(double min, double max, double value, Action<double> set)
        {
            var sl = new Slider { Minimum = min, Maximum = max, Value = value, Width = 130, SmallChange = 0.1, TickFrequency = 0.1, IsSnapToTickEnabled = false };
            sl.PreviewMouseLeftButtonDown += (_, _) => Snapshot();
            sl.ValueChanged += (_, e) => { if (_refreshing) return; set(e.NewValue); _timeline.InvalidateVisual(); UpdateLabels(); _overlay.InvalidateVisual(); };
            return sl;
        }

        switch (_timeline.Sel)
        {
            case SelKind.Segment when _project.Segments.FirstOrDefault(s => s.Id == _timeline.SelId) is { } seg:
                Add(Theme.Label($"Clip {TimelineView.Format(seg.Start, true)} – {TimelineView.Format(seg.End, true)}" + (seg.Removed ? "  (cut out)" : ""), 12.5, Theme.Text));
                Add(Theme.Pill(seg.Removed ? "Restore clip" : "Cut out this clip", DeleteSelected, false));
                break;
            case SelKind.AudioClip:
                Add(Theme.Label($"{_timeline.SelRole.Title()}  {TimelineView.Format(_timeline.SelClipStart, true)} – {TimelineView.Format(_timeline.SelClipEnd, true)}", 12.5, Theme.Text));
                Add(Theme.Pill("Split here", Split, false));
                Add(Theme.Pill("Mute this part", DeleteSelected, false));
                Add(Lab("Click a sound track to pick it, split it at the playhead, then mute or delete parts."));
                break;
            case SelKind.Speed when _project.Speeds.FirstOrDefault(s => s.Id == _timeline.SelId) is { } sp:
                Add(Lab("Speed"));
                var rates = new[] { 0.25, 0.5, 0.75, 1.5, 2, 3, 4 };
                var combo = Ui.Combo(rates.Select(r => ($"{r:0.##}×", r)), rates.OrderBy(r => Math.Abs(r - sp.Rate)).First(), r => { if (_refreshing) return; Snapshot(); sp.Rate = r; _timeline.InvalidateVisual(); UpdateLabels(); }, 100);
                Add(combo);
                Add(Lab("Slower below 1×, faster above. Audio follows."));
                break;
            case SelKind.Mute when _project.Mutes.FirstOrDefault(m => m.Id == _timeline.SelId) is { } mu:
                Add(Theme.Label($"{mu.Role.Title()} is silent from {TimelineView.Format(mu.Start, true)} to {TimelineView.Format(mu.End, true)}", 12.5, Theme.Text));
                Add(Lab("Drag the ends on the timeline to change it. The other tracks keep playing."));
                break;
            case SelKind.Object when SelectedObject is { } o:
                bool zoom = o.Kind == AnnotationKind.Zoom;
                Add(Theme.Label(zoom ? "Zoom" : o.Kind.ToString(), 12.5, Theme.Text, FontWeights.SemiBold));
                Add(Lab(zoom ? "Ease in" : "Fade in"));
                Add(Slide(0, 3, o.FadeIn, v => o.FadeIn = v));
                Add(Lab(zoom ? "Ease out" : "Fade out"));
                Add(Slide(0, 3, o.FadeOut, v => o.FadeOut = v));
                if (o.Kind == AnnotationKind.Media && o.Media is { HasAudio: true })
                {
                    Add(Lab("Its volume"));
                    Add(Slide(0, 1, o.Volume, v => o.Volume = v));
                    var mute = new CheckBox { Style = Theme.Style("Switch"), Content = Lab("Mute"), IsChecked = o.AudioMuted };
                    mute.Checked += (_, _) => { if (!_refreshing) { Snapshot(); o.AudioMuted = true; } };
                    mute.Unchecked += (_, _) => { if (!_refreshing) { Snapshot(); o.AudioMuted = false; } };
                    Add(mute);
                }
                Add(Lab($"{o.Keyframes.Count} keyframe{(o.Keyframes.Count == 1 ? "" : "s")}  ·  move the playhead and drag it to animate; hold Shift to move the whole path instead"));
                break;
            default:
                Add(Lab("Fade the sound in"));
                Add(Slide(0, 5, _project.AudioFadeIn, v => _project.AudioFadeIn = v));
                Add(Lab("Fade the sound out"));
                Add(Slide(0, 5, _project.AudioFadeOut, v => _project.AudioFadeOut = v));
                Add(Lab("Click a clip, bar or diamond on the timeline to edit it."));
                break;
        }
        _refreshing = false;
    }

    void RefreshInspectorValues() { /* sliders read live values from the project; nothing to sync while dragging on the timeline */ }

    void ShowContextMenu()
    {
        var menu = new ContextMenu();
        void Item(string title, Action a) { var mi = new MenuItem { Header = title }; mi.Click += (_, _) => a(); menu.Items.Add(mi); }
        Item("Split at playhead", Split);
        if (_timeline.Sel == SelKind.AudioClip) Item("Mute this part", DeleteSelected);
        else if (_timeline.Sel == SelKind.Segment)
        {
            var seg = _project.Segments.FirstOrDefault(s => s.Id == _timeline.SelId);
            Item(seg?.Removed == true ? "Restore clip" : "Cut out this clip", DeleteSelected);
        }
        else if (_timeline.SelKey >= 0) { Item("Delete keyframe", DeleteSelected); Item("Add keyframe at playhead", AddKeyframe); }
        else
        {
            Item("Delete", DeleteSelected);
            if (_timeline.Sel == SelKind.Object) Item("Add keyframe at playhead", AddKeyframe);
        }
        menu.IsOpen = true;
    }

    // ---- keys ------------------------------------------------------------------------------------------------------------

    void OnKey(object sender, KeyEventArgs e)
    {
        if (_editor != null || Keyboard.FocusedElement is TextBox) return;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control), shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        bool handled = true;
        switch (e.Key)
        {
            case Key.Space: TogglePlay(); break;
            case Key.Left: if (shift) { if (_playing) Pause(); Seek(_t - 1); } else StepFrames(-1); break;
            case Key.Right: if (shift) { if (_playing) Pause(); Seek(_t + 1); } else StepFrames(1); break;
            case Key.OemComma: StepFrames(-1); break;
            case Key.OemPeriod: StepFrames(1); break;
            case Key.Home: Seek(_project.TrimStart); break;
            case Key.End: Seek(_project.TrimEnd); break;
            case Key.S when !ctrl: Split(); break;
            case Key.K when !ctrl: AddKeyframe(); break;
            case Key.I when !ctrl: Snapshot(); _project.TrimStart = Math.Min(_t, _project.TrimEnd - 0.2); Edited(); break;
            case Key.O when !ctrl: Snapshot(); _project.TrimEnd = Math.Max(_t, _project.TrimStart + 0.2); Edited(); break;
            case Key.Delete: case Key.Back: DeleteSelected(); break;
            case Key.Z when ctrl: if (shift) Redo(); else Undo(); break;
            case Key.Y when ctrl: Redo(); break;
            case Key.V when ctrl: PasteMedia(); break;
            case Key.Escape: _timeline.ClearSelection(); break;
            default: handled = false; break;
        }
        e.Handled = handled;
    }

    // ---- finishing -------------------------------------------------------------------------------------------------------

    void Finish(bool toClipboard)
    {
        CommitText();
        Pause();
        _exported = true;
        App.Current.ExportAndUpload(_file, _project, _deleteWhenDone, toClipboard);
        Close();
    }

    void OnClosed(object? sender, EventArgs e)
    {
        _timer.Stop();
        try { _media.Close(); } catch { }
        if (!_exported)
        {
            foreach (var o in _project.Objects) o.Media?.Dispose();
            if (_deleteWhenDone) { try { File.Delete(_file); } catch { } }
        }
    }
}
