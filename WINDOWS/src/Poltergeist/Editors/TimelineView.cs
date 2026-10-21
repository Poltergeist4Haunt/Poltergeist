using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Poltergeist.Core;

namespace Poltergeist;

enum SelKind { None, Segment, Speed, Mute, Object, AudioClip }

/// <summary>
/// The video editor's timeline: ruler, the video clips, speed changes, one lane per audio track (with mute ranges) and one lane per
/// object (shapes, text, blur, pictures, zoom) with keyframe diamonds. Everything is dragged directly: bars move, edges resize,
/// diamonds retime, the red handles trim.
/// </summary>
sealed class TimelineView : FrameworkElement
{
    const double Pad = 12, RulerH = 30, RowH = 28, RowGap = 5;

    enum Drag { None, Scrub, Trim0, Trim1, Move, ResizeL, ResizeR, Key }
    sealed record Hit(Rect R, SelKind Kind, Guid Id, string Part, int Index);
    sealed record Row(string Label, int Type, AudioRole Role, VideoObject? Obj);      // Type: 0 video, 1 speed, 2 audio, 3 object

    VideoProject _project = new(1);
    double _playhead;
    double _pps = 90;
    double _scrollOffset;
    readonly List<Hit> _hits = new();

    /// <summary>Loudness per audio role, filled in by the editor once ffmpeg has decoded each track.</summary>
    public Dictionary<AudioRole, float[]> Waves { get; } = new();

    public SelKind Sel { get; private set; }
    public Guid SelId { get; private set; }
    public int SelKey { get; private set; } = -1;
    public AudioRole SelRole { get; private set; }
    public double SelClipStart { get; private set; }
    public double SelClipEnd { get; private set; }

    /// <summary>The part of an audio track between its split points that contains <paramref name="t"/>.</summary>
    public (double Start, double End) AudioClipAt(AudioRole role, double t)
    {
        var cuts = _project.AudioSplits.Where(s => s.Role == role).Select(s => s.Time).OrderBy(x => x).ToList();
        double a = 0, b = _project.Duration;
        foreach (var c in cuts) { if (c <= t) a = c; else { b = c; break; } }
        return (a, b);
    }

    public void SelectAudio(AudioRole role, double a, double b)
    {
        Sel = SelKind.AudioClip; SelId = Guid.Empty; SelKey = -1; SelRole = role; SelClipStart = a; SelClipEnd = b;
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    public event Action<double>? Scrub;
    public event Action? SelectionChanged;
    public event Action? BeforeEdit;
    public event Action? Edited;
    public event Action<double, double>? Zoomed;                // (time under cursor, cursor x within the viewport)
    public event Action? ContextRequested;

    public VideoProject Project { get => _project; set { _project = value; InvalidateMeasure(); InvalidateVisual(); } }
    public double Playhead { get => _playhead; set { _playhead = value; InvalidateVisual(); } }
    public double ScrollOffset { get => _scrollOffset; set { _scrollOffset = value; InvalidateVisual(); } }
    public double PxPerSec
    {
        get => _pps;
        set { _pps = Math.Min(Math.Max(value, 6), 800); InvalidateMeasure(); InvalidateVisual(); }
    }

    public TimelineView() { Focusable = false; ClipToBounds = true; }

    public void Select(SelKind kind, Guid id, int key = -1)
    {
        Sel = kind; SelId = id; SelKey = key;
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    public void ClearSelection() { if (Sel != SelKind.None) Select(SelKind.None, Guid.Empty); }

    double X(double t) => Pad + t * _pps;
    double T(double x) => Math.Min(Math.Max((x - Pad) / _pps, 0), _project.Duration);

    List<Row> Rows()
    {
        var rows = new List<Row> { new("Video", 0, AudioRole.Unknown, null), new("Speed", 1, AudioRole.Unknown, null) };
        foreach (var r in _project.AudioRoles.Distinct()) rows.Add(new Row(r.Title(), 2, r, null));
        foreach (var o in _project.Objects) rows.Add(new Row(ObjectLabel(o), 3, AudioRole.Unknown, o));
        return rows;
    }

    static string ObjectLabel(VideoObject o) => o.Kind switch
    {
        AnnotationKind.Text => string.IsNullOrEmpty(o.Text) ? "Text" : "“" + (o.Text.Length > 18 ? o.Text[..17] + "…" : o.Text) + "”",
        AnnotationKind.Media => o.Media?.Name is { Length: > 0 } n ? (n.Length > 20 ? n[..19] + "…" : n) : "Picture",
        AnnotationKind.Curve => "Curve arrow",
        AnnotationKind.Zoom => "Zoom",
        AnnotationKind.Blur => "Blur",
        _ => o.Kind.ToString(),
    };

    protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize)
    {
        int rows = Rows().Count;
        return new System.Windows.Size(Pad * 2 + _project.Duration * _pps + 60, RulerH + rows * (RowH + RowGap) + 14);
    }

    // ---- drawing ---------------------------------------------------------------------------------------------------------

    static readonly Brush LaneBrush = new SolidColorBrush(Color.FromArgb(0x10, 255, 255, 255));
    static readonly Pen Hair = new(new SolidColorBrush(Color.FromArgb(0x1c, 255, 255, 255)), 1);

    FormattedText Txt(string s, double size, Brush b, FontWeight? w = null) => new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
        new Typeface(Theme.UiFont, FontStyles.Normal, w ?? FontWeights.Normal, FontStretches.Normal), size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnRender(DrawingContext dc)
    {
        _hits.Clear();
        double w = Math.Max(ActualWidth, 10), dur = _project.Duration;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

        DrawRuler(dc, dur);

        var rows = Rows();
        double y = RulerH;
        foreach (var row in rows)
        {
            var lane = new Rect(Pad, y, Math.Max(1, dur * _pps), RowH);
            dc.DrawRoundedRectangle(LaneBrush, null, lane, 7, 7);
            switch (row.Type)
            {
                case 0: DrawSegments(dc, y); break;
                case 1: DrawSpeeds(dc, y); break;
                case 2: DrawMutes(dc, y, row.Role); break;
                case 3: DrawObject(dc, y, row.Obj!); break;
            }
            // sticky row label
            var label = Txt(row.Label, 11, new SolidColorBrush(Color.FromArgb(0xd0, 0xf2, 0xf2, 0xf2)), FontWeights.Medium);
            double lx = Math.Max(_scrollOffset, 0) + 6;
            var chip = new Rect(lx, y + 5, label.Width + 12, RowH - 10);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0xcc, 0x0f, 0x0f, 0x0f)), null, chip, 6, 6);
            dc.DrawText(label, new System.Windows.Point(lx + 6, y + (RowH - label.Height) / 2));
            y += RowH + RowGap;
        }

        DrawTrim(dc, y);
        DrawPlayhead(dc, y);
    }

    void DrawRuler(DrawingContext dc, double dur)
    {
        double[] steps = { 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 };
        double step = steps.FirstOrDefault(s => s * _pps >= 64);
        if (step == 0) step = 600;
        var dim = new SolidColorBrush(Color.FromArgb(0x99, 0xf2, 0xf2, 0xf2));
        for (double t = 0; t <= dur + 0.0001; t += step)
        {
            double x = X(t);
            dc.DrawLine(Hair, new System.Windows.Point(x, RulerH - 9), new System.Windows.Point(x, RulerH - 1));
            var f = Txt(Format(t, step < 1), 10.5, dim);
            dc.DrawText(f, new System.Windows.Point(x + 3, 3));
        }
        // minor ticks
        double minor = step / 5;
        if (minor * _pps >= 7)
            for (double t = 0; t <= dur + 0.0001; t += minor)
            {
                double x = X(t);
                dc.DrawLine(Hair, new System.Windows.Point(x, RulerH - 4), new System.Windows.Point(x, RulerH - 1));
            }
    }

    public static string Format(double t, bool fraction)
    {
        if (t < 0) t = 0;
        int m = (int)(t / 60);
        double s = t - m * 60;
        return fraction ? $"{m}:{s:00.0}" : $"{m}:{(int)s:00}";
    }

    void DrawSegments(DrawingContext dc, double y)
    {
        var keep = new SolidColorBrush(Color.FromArgb(0x30, 0xf2, 0x7d, 0xb3));
        var keepPen = new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0xf2, 0x7d, 0xb3)), 1);
        var gone = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255));
        var gonePen = new Pen(new SolidColorBrush(Color.FromArgb(0x50, 255, 255, 255)) { }, 1) { DashStyle = new DashStyle(new[] { 3.0, 3.0 }, 0) };
        foreach (var seg in _project.Segments)
        {
            var r = new Rect(X(seg.Start) + 0.5, y + 2, Math.Max(2, (seg.End - seg.Start) * _pps - 1), RowH - 4);
            bool sel = Sel == SelKind.Segment && SelId == seg.Id;
            dc.DrawRoundedRectangle(seg.Removed ? gone : keep, sel ? new Pen(Theme.Accent, 2) : (seg.Removed ? gonePen : keepPen), r, 6, 6);
            if (seg.Removed && r.Width > 50)
            {
                var f = Txt("Cut out", 10.5, new SolidColorBrush(Color.FromArgb(0x99, 255, 255, 255)));
                dc.DrawText(f, new System.Windows.Point(r.X + (r.Width - f.Width) / 2, y + (RowH - f.Height) / 2));
            }
            _hits.Add(new Hit(r, SelKind.Segment, seg.Id, "B", 0));
        }
    }

    void DrawSpeeds(DrawingContext dc, double y)
    {
        foreach (var s in _project.Speeds)
        {
            var r = new Rect(X(s.Start), y + 2, Math.Max(6, (s.End - s.Start) * _pps), RowH - 4);
            bool sel = Sel == SelKind.Speed && SelId == s.Id;
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x48, 0xf5, 0xa6, 0x23)), new Pen(sel ? Theme.Text : new SolidColorBrush(Color.FromArgb(0xa0, 0xf5, 0xa6, 0x23)), sel ? 2 : 1), r, 6, 6);
            var f = Txt($"{s.Rate:0.##}×", 11, new SolidColorBrush(Color.FromRgb(0xf5, 0xa6, 0x23)), FontWeights.SemiBold);
            if (r.Width > f.Width + 8) dc.DrawText(f, new System.Windows.Point(r.X + (r.Width - f.Width) / 2, y + (RowH - f.Height) / 2));
            AddBarHits(r, SelKind.Speed, s.Id);
        }
    }

    void DrawMutes(DrawingContext dc, double y, AudioRole role)
    {
        if (Waves.TryGetValue(role, out var peaks) && peaks.Length > 0)
        {
            var wave = new Pen(new SolidColorBrush(Color.FromArgb(0xa0, 0x9f, 0xd8, 0xff)), 1);
            double step = 1.5;
            double mid = y + RowH / 2, half = (RowH - 8) / 2;
            double first = Math.Max(_scrollOffset - 20, 0), last = _scrollOffset + 4000;
            for (double x = Math.Max(X(0), first - first % step); x < Math.Min(X(_project.Duration), last); x += step)
            {
                double t0 = (x - Pad) / _pps, t1 = (x + step - Pad) / _pps;
                int i0 = (int)(t0 / Waveform.Bucket), i1 = Math.Max(i0, (int)(t1 / Waveform.Bucket));
                float v = 0;
                for (int i = i0; i <= i1 && i < peaks.Length; i++) if (i >= 0 && peaks[i] > v) v = peaks[i];
                double h = Math.Max(0.5, v * half);
                dc.DrawLine(wave, new System.Windows.Point(x, mid - h), new System.Windows.Point(x, mid + h));
            }
        }
        // the whole lane can be clicked to pick the part of the track under the cursor
        _hits.Add(new Hit(new Rect(X(0), y, Math.Max(1, _project.Duration * _pps), RowH), SelKind.AudioClip, Guid.Empty, "A" + (int)role, 0));
        if (Sel == SelKind.AudioClip && SelRole == role)
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x24, 0xf2, 0x7d, 0xb3)), new Pen(Theme.Accent, 2),
                new Rect(X(SelClipStart), y + 2, Math.Max(2, (SelClipEnd - SelClipStart) * _pps), RowH - 4), 6, 6);
        var splitPen = new Pen(new SolidColorBrush(Color.FromArgb(0xd0, 255, 255, 255)), 1.5);
        foreach (var sp in _project.AudioSplits.Where(sp => sp.Role == role))
            dc.DrawLine(splitPen, new System.Windows.Point(X(sp.Time), y + 1), new System.Windows.Point(X(sp.Time), y + RowH - 1));
        foreach (var m in _project.Mutes.Where(m => m.Role == role))
        {
            var r = new Rect(X(m.Start), y + 2, Math.Max(6, (m.End - m.Start) * _pps), RowH - 4);
            bool sel = Sel == SelKind.Mute && SelId == m.Id;
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x70, 0xff, 0x6b, 0x6b)), new Pen(sel ? Theme.Text : new SolidColorBrush(Color.FromArgb(0xb0, 0xff, 0x6b, 0x6b)), sel ? 2 : 1), r, 6, 6);
            var f = Txt("Muted", 10.5, Brushes.White);
            if (r.Width > f.Width + 8) dc.DrawText(f, new System.Windows.Point(r.X + (r.Width - f.Width) / 2, y + (RowH - f.Height) / 2));
            AddBarHits(r, SelKind.Mute, m.Id);
        }
    }

    void DrawObject(DrawingContext dc, double y, VideoObject o)
    {
        Color baseColor = o.Kind switch
        {
            AnnotationKind.Blur => Color.FromRgb(0xa0, 0xa0, 0xa0),
            AnnotationKind.Zoom => Color.FromRgb(0x5a, 0xc8, 0xfa),
            AnnotationKind.Media => Color.FromRgb(0xc8, 0xb4, 0xff),
            _ => Color.FromRgb(o.Color.R, o.Color.G, o.Color.B),
        };
        var r = new Rect(X(o.Start), y + 2, Math.Max(8, (o.End - o.Start) * _pps), RowH - 4);
        bool sel = Sel == SelKind.Object && SelId == o.Id;
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x4a, baseColor.R, baseColor.G, baseColor.B)),
            new Pen(sel ? Theme.Text : new SolidColorBrush(Color.FromArgb(0xc0, baseColor.R, baseColor.G, baseColor.B)), sel ? 2 : 1), r, 6, 6);

        // fades as little ramps
        var ramp = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0));
        if (o.FadeIn > 0)
        {
            double fw = Math.Min(o.FadeIn * _pps, r.Width);
            dc.DrawGeometry(ramp, null, Geometry.Parse($"M{r.X},{r.Y} L{r.X + fw},{r.Y} L{r.X},{r.Bottom} Z"));
        }
        if (o.FadeOut > 0)
        {
            double fw = Math.Min(o.FadeOut * _pps, r.Width);
            dc.DrawGeometry(ramp, null, Geometry.Parse($"M{r.Right},{r.Y} L{r.Right - fw},{r.Y} L{r.Right},{r.Bottom} Z"));
        }
        AddBarHits(r, SelKind.Object, o.Id);

        // keyframe diamonds
        for (int i = 0; i < o.Keyframes.Count; i++)
        {
            var k = o.Keyframes[i];
            double cx = X(k.Time), cy = y + RowH / 2;
            bool ks = sel && SelKey == i;
            var d = Geometry.Parse($"M{cx},{cy - 6} L{cx + 6},{cy} L{cx},{cy + 6} L{cx - 6},{cy} Z");
            dc.DrawGeometry(ks ? Theme.Accent : Brushes.White, new Pen(new SolidColorBrush(Color.FromArgb(0xc0, 0, 0, 0)), 1), d);
            _hits.Add(new Hit(new Rect(cx - 7, cy - 7, 14, 14), SelKind.Object, o.Id, "K", i));
        }
    }

    void AddBarHits(Rect r, SelKind kind, Guid id)
    {
        _hits.Add(new Hit(r, kind, id, "B", 0));
        if (r.Width > 18)
        {
            _hits.Add(new Hit(new Rect(r.X - 2, r.Y, 8, r.Height), kind, id, "L", 0));
            _hits.Add(new Hit(new Rect(r.Right - 6, r.Y, 8, r.Height), kind, id, "R", 0));
        }
    }

    void DrawTrim(DrawingContext dc, double bottom)
    {
        var dim = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0));
        double a = X(_project.TrimStart), b = X(_project.TrimEnd);
        if (a > Pad) dc.DrawRectangle(dim, null, new Rect(Pad, RulerH, a - Pad, bottom - RulerH));
        if (b < X(_project.Duration)) dc.DrawRectangle(dim, null, new Rect(b, RulerH, X(_project.Duration) - b, bottom - RulerH));
        var red = new SolidColorBrush(Color.FromRgb(0xff, 0x6b, 0x6b));
        foreach (var (x, part) in new[] { (a, "T0"), (b, "T1") })
        {
            bool trimmed = part == "T0" ? _project.TrimStart > 0.01 : _project.TrimEnd < _project.Duration - 0.01;
            var grip = new Rect(x - (part == "T0" ? 0 : 7), 0, 7, bottom);
            dc.DrawRectangle(trimmed ? red : new SolidColorBrush(Color.FromArgb(0x50, 255, 255, 255)), null, new Rect(x - 1.5, RulerH - 4, 3, bottom - RulerH + 4));
            dc.DrawRoundedRectangle(trimmed ? red : new SolidColorBrush(Color.FromArgb(0x66, 255, 255, 255)), null, new Rect(x - (part == "T0" ? 0 : 7), 2, 7, 14), 2, 2);
            _hits.Add(new Hit(new Rect(x - 7, 0, 14, 20), SelKind.None, Guid.Empty, part, 0));
        }
    }

    void DrawPlayhead(DrawingContext dc, double bottom)
    {
        double x = X(_playhead);
        dc.DrawLine(new Pen(Theme.Accent, 1.5), new System.Windows.Point(x, 0), new System.Windows.Point(x, bottom));
        dc.DrawGeometry(Theme.Accent, null, Geometry.Parse($"M{x - 6},0 L{x + 6},0 L{x},9 Z"));
    }

    // ---- mouse -----------------------------------------------------------------------------------------------------------

    Drag _drag = Drag.None;
    double _downX, _origStart, _origEnd, _origKeyTime;
    List<Keyframe>? _origKeys;
    bool _changed;
    int _keyIndex;

    Hit? HitAt(System.Windows.Point p)
    {
        for (int i = _hits.Count - 1; i >= 0; i--) if (_hits[i].R.Contains(p)) return _hits[i];
        return null;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var p = e.GetPosition(this);
        _downX = p.X;
        _changed = false;
        var hit = p.Y < RulerH - 6 && p.Y > 0 && !(HitAt(p) is { Part: "T0" or "T1" }) ? null : HitAt(p);

        if (hit is { Part: "T0" }) _drag = Drag.Trim0;
        else if (hit is { Part: "T1" }) _drag = Drag.Trim1;
        else if (hit == null || p.Y < RulerH)
        {
            _drag = Drag.Scrub;
            if (hit == null && p.Y >= RulerH) ClearSelection();
            Scrub?.Invoke(T(p.X));
        }
        else
        {
            if (hit.Kind == SelKind.AudioClip)
            {
                double ct = T(p.X);
                var role = (AudioRole)int.Parse(hit.Part[1..]);
                var (ca, cb) = AudioClipAt(role, ct);
                SelectAudio(role, ca, cb);
                _drag = Drag.Scrub;
                Scrub?.Invoke(ct);
                CaptureMouse();
                e.Handled = true;
                return;
            }
            if (hit.Part == "K") { Select(SelKind.Object, hit.Id, hit.Index); _keyIndex = hit.Index; }
            else Select(hit.Kind, hit.Id);
            _drag = hit.Part switch { "L" => Drag.ResizeL, "R" => Drag.ResizeR, "K" => Drag.Key, _ => hit.Kind == SelKind.Segment ? Drag.Scrub : Drag.Move };
            if (hit.Kind == SelKind.Segment) Scrub?.Invoke(T(p.X));
            CaptureOrig();
        }
        CaptureMouse();
        e.Handled = true;
    }

    void CaptureOrig()
    {
        switch (Sel)
        {
            case SelKind.Speed when _project.Speeds.FirstOrDefault(s => s.Id == SelId) is { } s: _origStart = s.Start; _origEnd = s.End; break;
            case SelKind.Mute when _project.Mutes.FirstOrDefault(m => m.Id == SelId) is { } m: _origStart = m.Start; _origEnd = m.End; break;
            case SelKind.Object when _project.Objects.FirstOrDefault(o => o.Id == SelId) is { } o:
                _origStart = o.Start; _origEnd = o.End; _origKeys = new List<Keyframe>(o.Keyframes);
                if (_keyIndex >= 0 && _keyIndex < o.Keyframes.Count) _origKeyTime = o.Keyframes[_keyIndex].Time;
                break;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        if (_drag == Drag.None)
        {
            var hit = HitAt(p);
            Cursor = hit?.Part switch { "L" or "R" or "T0" or "T1" => Cursors.SizeWE, "K" => Cursors.Hand, _ => Cursors.Arrow };
            return;
        }
        double dt = (p.X - _downX) / _pps;
        double dur = _project.Duration;
        const double minLen = 0.2;

        switch (_drag)
        {
            case Drag.Scrub: Scrub?.Invoke(T(p.X)); return;
            case Drag.Trim0:
                Edit(() => _project.TrimStart = Math.Min(Math.Max(T(p.X), 0), _project.TrimEnd - minLen)); return;
            case Drag.Trim1:
                Edit(() => _project.TrimEnd = Math.Max(Math.Min(T(p.X), dur), _project.TrimStart + minLen)); return;
        }

        if (Sel == SelKind.Speed && _project.Speeds.FirstOrDefault(s => s.Id == SelId) is { } sp)
            Edit(() => (sp.Start, sp.End) = Shift(dt, dur, minLen));
        else if (Sel == SelKind.Mute && _project.Mutes.FirstOrDefault(m => m.Id == SelId) is { } mu)
            Edit(() => (mu.Start, mu.End) = Shift(dt, dur, minLen));
        else if (Sel == SelKind.Object && _project.Objects.FirstOrDefault(o => o.Id == SelId) is { } ob)
        {
            if (_drag == Drag.Key)
            {
                if (_keyIndex < 0 || _keyIndex >= ob.Keyframes.Count) return;
                Edit(() =>
                {
                    var k = ob.Keyframes[_keyIndex];
                    double nt = Math.Min(Math.Max(_origKeyTime + dt, ob.Start), ob.End);
                    ob.Keyframes[_keyIndex] = k with { Time = nt };
                    var moved = ob.Keyframes[_keyIndex];
                    ob.Keyframes.Sort((a, b) => a.Time.CompareTo(b.Time));
                    _keyIndex = ob.Keyframes.IndexOf(moved);
                    SelKey = _keyIndex;
                });
            }
            else
            {
                var (s, en) = Shift(dt, dur, minLen);
                Edit(() =>
                {
                    double delta = s - _origStart;
                    ob.Start = s; ob.End = en;
                    if (_drag == Drag.Move && _origKeys != null)
                        ob.Keyframes = _origKeys.Select(k => k with { Time = k.Time + delta }).ToList();
                });
            }
        }
    }

    (double Start, double End) Shift(double dt, double dur, double minLen)
    {
        switch (_drag)
        {
            case Drag.ResizeL: return (Math.Min(Math.Max(_origStart + dt, 0), _origEnd - minLen), _origEnd);
            case Drag.ResizeR: return (_origStart, Math.Max(Math.Min(_origEnd + dt, dur), _origStart + minLen));
            default:
                double len = _origEnd - _origStart;
                double s = Math.Min(Math.Max(_origStart + dt, 0), Math.Max(0, dur - len));
                return (s, s + len);
        }
    }

    void Edit(Action change)
    {
        if (!_changed) { BeforeEdit?.Invoke(); _changed = true; }
        change();
        InvalidateVisual();
        Edited?.Invoke();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _drag = Drag.None;
        _origKeys = null;
        ReleaseMouseCapture();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        var p = e.GetPosition(this);
        if (HitAt(p) is { Kind: not SelKind.None } hit)
        {
            if (hit.Kind == SelKind.AudioClip)
            {
                var role = (AudioRole)int.Parse(hit.Part[1..]);
                var (ca, cb) = AudioClipAt(role, T(p.X));
                SelectAudio(role, ca, cb);
            }
            else if (hit.Part == "K") Select(SelKind.Object, hit.Id, hit.Index); else Select(hit.Kind, hit.Id);
            ContextRequested?.Invoke();
            e.Handled = true;
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            var p = e.GetPosition(this);
            double t = T(p.X);
            PxPerSec = _pps * (e.Delta > 0 ? 1.18 : 1 / 1.18);
            Zoomed?.Invoke(t, p.X - _scrollOffset);
            e.Handled = true;
        }
        else base.OnMouseWheel(e);
    }
}
