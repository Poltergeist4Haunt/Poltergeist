using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Poltergeist.Core;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace Poltergeist;

/// <summary>Quick screenshot editor: arrows, boxes, circles, text, blur, freehand, pasted pictures; then upload, copy or save.</summary>
sealed class ImageEditorWindow : ChromeWindow
{
    readonly string _file;
    readonly bool _deleteWhenDone;
    readonly SKImage _image;
    readonly AnnotationRenderer _renderer;
    readonly SKElement _canvas = new() { IgnorePixelScaling = true, Focusable = true, ClipToBounds = true };
    readonly Canvas _overlay = new() { IsHitTestVisible = true, Background = null };
    readonly ToolPalette _palette = new(allowZoom: false);
    readonly TextBlock _hint = Theme.Label("", 12, Theme.Muted);

    List<Annotation> _anns = new();
    readonly Stack<List<Annotation>> _undo = new();
    readonly Stack<List<Annotation>> _redo = new();
    Guid? _sel;
    Annotation? _draft;
    List<PointD>? _stroke;
    Annotation? _dragStart;
    HandleTarget _dragTarget;
    PointD _dragOrigin;
    bool _dragMoved;
    bool _panning;
    System.Windows.Point _panLast;
    double _zoom = 1, _offX, _offY;
    bool _fitted;
    TextBox? _editor;
    Guid? _editingId;
    bool _done;

    EditTool Tool => _palette.Tool;

    public ImageEditorWindow(string file, bool deleteWhenDone)
    {
        _file = file;
        _deleteWhenDone = deleteWhenDone;
        using (var codec = SKCodec.Create(file) ?? throw new InvalidDataException("Couldn't read the screenshot."))
        using (var bmp = SKBitmap.Decode(codec))
            _image = SKImage.FromBitmap(bmp);
        _renderer = new AnnotationRenderer(_image);

        Section = "Edit screenshot";
        var work = SystemParameters.WorkArea;
        Width = Math.Min(Math.Max(_image.Width * 0.75 + 80, 760), work.Width * 0.92);
        Height = Math.Min(Math.Max(_image.Height * 0.75 + 190, 520), work.Height * 0.92);
        MinWidth = 640; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // canvas + inline text editor overlay
        var stage = new Grid { Background = new SolidColorBrush(Color.FromRgb(0x0b, 0x0b, 0x0b)), ClipToBounds = true };
        stage.Children.Add(_canvas);
        stage.Children.Add(_overlay);
        _canvas.PaintSurface += OnPaint;
        stage.MouseDown += OnMouseDown;
        stage.MouseMove += OnMouseMove;
        stage.MouseUp += OnMouseUp;
        stage.MouseWheel += OnWheel;
        stage.SizeChanged += (_, _) => { if (!_fitted || _zoomIsFit) Fit(); _canvas.InvalidateVisual(); };

        // bottom bar
        var cancel = Theme.Pill("Cancel", Cancel, prominent: false);
        var upload = new SplitButton("Upload", Upload,
            ("Copy to clipboard", CopyToClipboard), ("Save as…", SaveAs));
        var undo = Theme.Icon("", "Undo (Ctrl+Z)", Undo);
        var del = Theme.Icon("", "Delete selected (Del)", DeleteSelected);
        var paste = Theme.Pill("Paste image", PasteImage, prominent: false);
        paste.Padding = new Thickness(12, 5, 12, 5);
        var fit = Theme.Pill("Fit", () => { Fit(); _canvas.InvalidateVisual(); }, prominent: false);
        fit.Padding = new Thickness(12, 5, 12, 5);

        var left = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        for (int i = 0; i < 5; i++) left.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 4 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        paste.Margin = new Thickness(8, 0, 0, 0); fit.Margin = new Thickness(6, 0, 0, 0);
        _hint.Text = "";
        _hint.Margin = new Thickness(14, 0, 14, 0); _hint.VerticalAlignment = VerticalAlignment.Center; _hint.TextTrimming = TextTrimming.CharacterEllipsis;
        UIElement[] items = { undo, del, paste, fit, _hint };
        for (int i = 0; i < items.Length; i++) { Grid.SetColumn(items[i], i); left.Children.Add(items[i]); }
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        cancel.Margin = new Thickness(0, 0, 8, 0);
        right.Children.Add(cancel); right.Children.Add(upload);
        var bar = new Grid { Height = 56, Background = Theme.Card };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.Children.Add(left);
        Grid.SetColumn(right, 1);
        bar.Children.Add(right);
        var barBorder = new Border { BorderBrush = Theme.Border, BorderThickness = new Thickness(0, 1, 0, 0), Child = bar };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(_palette);
        Grid.SetRow(stage, 1); root.Children.Add(stage);
        Grid.SetRow(barBorder, 2); root.Children.Add(barBorder);
        Body = root;

        _palette.Changed += OnPaletteChanged;
        _palette.SetSoftVisible(false);
        UpdateHint();

        AllowDrop = true;
        Drop += async (_, e) => await DropFiles(e);
        PreviewKeyDown += OnKey;
        Closed += (_, _) =>
        {
            if (!_done) CleanupTemp();
            _image.Dispose();
        };
        Loaded += (_, _) => { _canvas.Focus(); Fit(); };
    }

    // ---- view ------------------------------------------------------------------------------------------------------------

    bool _zoomIsFit = true;
    double Dpi => VisualTreeHelper.GetDpi(this).DpiScaleX;

    void Fit()
    {
        double w = _canvas.ActualWidth * Dpi, h = _canvas.ActualHeight * Dpi;
        if (w < 10 || h < 10) return;
        _zoom = Math.Min(Math.Min((w - 40) / _image.Width, (h - 40) / _image.Height), 2);
        if (_zoom <= 0) _zoom = 1;
        _offX = (w - _image.Width * _zoom) / 2;
        _offY = (h - _image.Height * _zoom) / 2;
        _fitted = true;
        _zoomIsFit = true;
    }

    PointD ToImage(System.Windows.Point dip) => new((dip.X * Dpi - _offX) / _zoom, (dip.Y * Dpi - _offY) / _zoom);
    System.Windows.Point ToView(PointD p) => new((p.X * _zoom + _offX) / Dpi, (p.Y * _zoom + _offY) / Dpi);

    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (_editor != null) return;
        var pos = e.GetPosition(_canvas);
        double px = pos.X * Dpi, py = pos.Y * Dpi;
        double k = e.Delta > 0 ? 1.12 : 1 / 1.12;
        double nz = Math.Min(Math.Max(_zoom * k, 0.05), 16);
        k = nz / _zoom;
        _offX = px - (px - _offX) * k;
        _offY = py - (py - _offY) * k;
        _zoom = nz;
        _zoomIsFit = false;
        _canvas.InvalidateVisual();
        e.Handled = true;
    }

    // ---- painting --------------------------------------------------------------------------------------------------------

    void OnPaint(object? sender, SKPaintSurfaceEventArgs e)
    {
        var c = e.Surface.Canvas;
        c.Clear(new SKColor(0x0b, 0x0b, 0x0b));
        if (!_fitted) Fit();
        c.Save();
        c.Translate((float)_offX, (float)_offY);
        c.Scale((float)_zoom);

        // soft shadow card behind the picture
        using (var sh = new SKPaint { Color = new SKColor(0, 0, 0, 120), ImageFilter = SKImageFilter.CreateBlur(14, 14), IsAntialias = true })
            c.DrawRect(new SKRect(0, 6 / (float)_zoom, _image.Width, _image.Height + 6 / (float)_zoom), sh);

        var shown = _editingId is { } eid ? _anns.Where(a => a.Id != eid) : _anns;
        _renderer.DrawAll(c, shown, _draft);
        if (_sel is { } id && _anns.FirstOrDefault(a => a.Id == id) is { } selected && _editingId != id) DrawSelection(c, selected);
        c.Restore();
    }

    void DrawSelection(SKCanvas c, Annotation a)
    {
        float z = (float)_zoom;
        using var line = new SKPaint { Color = new SKColor(0xf2, 0x7d, 0xb3), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f / z, IsAntialias = true, PathEffect = SKPathEffect.CreateDash(new[] { 5f / z, 4f / z }, 0) };
        using var fill = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var ring = new SKPaint { Color = new SKColor(0xf2, 0x7d, 0xb3), Style = SKPaintStyle.Stroke, StrokeWidth = 2f / z, IsAntialias = true };
        var b = _renderer.BoundsOf(a);
        if (a.Kind != AnnotationKind.Arrow) c.DrawRect(b.Inflate(2 / _zoom).ToSk(), line);
        float r = 5f / z;
        foreach (var p in HandlePoints(a))
        {
            c.DrawCircle(p.ToSk(), r, fill);
            c.DrawCircle(p.ToSk(), r, ring);
        }
    }

    IEnumerable<PointD> HandlePoints(Annotation a)
    {
        if (a.Kind == AnnotationKind.Text) { var b = _renderer.BoundsOf(a); yield return new PointD(b.MaxX, b.MaxY); yield break; }
        yield return a.P1;
        yield return a.P2;
    }

    // ---- palette -----------------------------------------------------------------------------------------------------------

    void OnPaletteChanged(string what)
    {
        _palette.SetSoftVisible(Tool == EditTool.Blur || (SelectedAnn?.Kind == AnnotationKind.Blur));
        if (what == "tool") { _canvas.Cursor = Tool == EditTool.Select ? Cursors.Arrow : Cursors.Cross; UpdateHint(); return; }
        if (SelectedAnn is { } a)
        {
            var edited = what switch
            {
                "color" when a.Kind != AnnotationKind.Blur && a.Kind != AnnotationKind.Media => a with { Color = _palette.Color },
                "level" => a with { Level = _palette.Level },
                "soft" when a.Kind == AnnotationKind.Blur => a with { Soft = _palette.Soft },
                _ => a,
            };
            if (!ReferenceEquals(edited, a)) { Snapshot(); Replace(edited); _canvas.InvalidateVisual(); }
        }
    }

    Annotation? SelectedAnn => _sel is { } id ? _anns.FirstOrDefault(a => a.Id == id) : null;

    void UpdateHint()
    {
        _hint.Text = Tool switch
        {
            EditTool.Select => "Click an item to move or resize it",
            EditTool.Text => "Click where the text goes",
            _ => "Drag on the picture to draw",
        } + "  ·  Ctrl+V pastes a picture  ·  wheel zooms  ·  middle-drag pans";
    }

    // ---- undo ------------------------------------------------------------------------------------------------------------

    void Snapshot()
    {
        _undo.Push(new List<Annotation>(_anns));
        if (_undo.Count > 80) { var keep = _undo.Reverse().Skip(1).Reverse().ToList(); _undo.Clear(); foreach (var k in keep) _undo.Push(k); }
        _redo.Clear();
    }

    void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(new List<Annotation>(_anns));
        _anns = _undo.Pop();
        if (_sel is { } id && _anns.All(a => a.Id != id)) _sel = null;
        _canvas.InvalidateVisual();
    }

    void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(new List<Annotation>(_anns));
        _anns = _redo.Pop();
        _canvas.InvalidateVisual();
    }

    void Replace(Annotation a)
    {
        int i = _anns.FindIndex(x => x.Id == a.Id);
        if (i >= 0) _anns[i] = a;
    }

    void DeleteSelected()
    {
        if (_sel is not { } id) return;
        Snapshot();
        _anns.RemoveAll(a => a.Id == id);
        _sel = null;
        _canvas.InvalidateVisual();
    }

    // ---- mouse ---------------------------------------------------------------------------------------------------------------

    void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_editor != null) { CommitText(); }
        _canvas.Focus();
        var pos = e.GetPosition(_canvas);

        if (e.ChangedButton == MouseButton.Middle || (e.ChangedButton == MouseButton.Left && Keyboard.IsKeyDown(Key.Space)))
        {
            _panning = true; _panLast = pos; ((UIElement)sender).CaptureMouse(); _canvas.Cursor = Cursors.SizeAll; return;
        }
        if (e.ChangedButton != MouseButton.Left) return;

        var p = ToImage(pos);
        double tol = 9 * Dpi / _zoom;

        if (e.ClickCount == 2 && HitTest(p, tol) is { Kind: AnnotationKind.Text } txt)
        {
            _sel = txt.Id; BeginTextEdit(txt); return;
        }

        // handles of the current selection come first, whatever tool is active
        if (SelectedAnn is { } cur && HandleAt(cur, p, tol) is { } ht)
        {
            StartDrag(cur, ht, p);
            ((UIElement)sender).CaptureMouse();
            return;
        }

        if (SelectedAnn is { } picked && _renderer.Hits(picked, p, tol))
        {
            StartDrag(picked, HandleTarget.Body, p);
            ((UIElement)sender).CaptureMouse();
            return;
        }

        if (Tool == EditTool.Select)
        {
            var hit = HitTest(p, tol);
            _sel = hit?.Id;
            if (hit != null)
            {
                SyncPalette(hit);
                StartDrag(hit, HandleTarget.Body, p);
                ((UIElement)sender).CaptureMouse();
            }
            _canvas.InvalidateVisual();
            return;
        }

        // drawing tools
        _sel = null;
        if (Tool == EditTool.Text)
        {
            var t = new Annotation { Kind = AnnotationKind.Text, P1 = p, P2 = p, Color = _palette.Color, Level = _palette.Level };
            BeginTextEdit(t, isNew: true);
            return;
        }
        if (Tool.IsFreehand())
        {
            _stroke = new List<PointD> { p };
            _draft = null;
        }
        else
        {
            _draft = new Annotation { Kind = Tool.Kind()!.Value, P1 = p, P2 = p, Color = _palette.Color, Level = _palette.Level, Soft = _palette.Soft };
        }
        ((UIElement)sender).CaptureMouse();
        _canvas.InvalidateVisual();
    }

    void OnMouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(_canvas);
        if (_panning)
        {
            _offX += (pos.X - _panLast.X) * Dpi; _offY += (pos.Y - _panLast.Y) * Dpi; _panLast = pos; _zoomIsFit = false;
            _canvas.InvalidateVisual(); return;
        }
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var p = ToImage(pos);

        if (_dragStart is { } start)
        {
            double dx = p.X - _dragOrigin.X, dy = p.Y - _dragOrigin.Y;
            if (!_dragMoved && Math.Abs(dx) + Math.Abs(dy) < 2 / _zoom) return;
            if (!_dragMoved) { Snapshot(); _dragMoved = true; }
            Replace(start.Moved(_dragTarget, dx, dy));
            _canvas.InvalidateVisual();
            return;
        }
        if (_stroke != null)
        {
            if (_stroke[^1].DistanceTo(p) > 1.5 / _zoom) _stroke.Add(p);
            _draft = Annotation.Freehand(Tool.Kind()!.Value, _stroke, _palette.Color, _palette.Level);
            _canvas.InvalidateVisual();
            return;
        }
        if (_draft != null)
        {
            _draft = _draft with { P2 = p };
            _canvas.InvalidateVisual();
        }
    }

    void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        ((UIElement)sender).ReleaseMouseCapture();
        if (_panning) { _panning = false; _canvas.Cursor = Tool == EditTool.Select ? Cursors.Arrow : Cursors.Cross; return; }
        if (_dragStart != null) { _dragStart = null; _canvas.InvalidateVisual(); return; }

        Annotation? made = null;
        if (_stroke != null) { made = _draft; _stroke = null; }
        else if (_draft != null)
        {
            var d = _draft;
            bool big = d.Kind == AnnotationKind.Arrow ? d.P1.DistanceTo(d.P2) > 6 / _zoom : Math.Abs(d.P2.X - d.P1.X) > 4 / _zoom && Math.Abs(d.P2.Y - d.P1.Y) > 4 / _zoom;
            if (big) made = d;
        }
        _draft = null;
        if (made != null)
        {
            Snapshot();
            _anns.Add(made);
            _sel = made.Id;
        }
        _canvas.InvalidateVisual();
    }

    Annotation? HitTest(PointD p, double tol)
    {
        for (int i = _anns.Count - 1; i >= 0; i--) if (_renderer.Hits(_anns[i], p, tol)) return _anns[i];
        return null;
    }

    HandleTarget? HandleAt(Annotation a, PointD p, double tol)
    {
        if (a.Kind == AnnotationKind.Text)
        {
            var b = _renderer.BoundsOf(a);
            return new PointD(b.MaxX, b.MaxY).DistanceTo(p) <= tol * 0.8 ? HandleTarget.P2 : null;
        }
        if (a.P1.DistanceTo(p) <= tol) return HandleTarget.P1;
        if (a.P2.DistanceTo(p) <= tol) return HandleTarget.P2;
        return null;
    }

    void StartDrag(Annotation a, HandleTarget target, PointD origin)
    {
        _dragStart = a; _dragTarget = target; _dragOrigin = origin; _dragMoved = false;
        // Text has no second corner: its handle changes the size level instead of moving an end.
        if (a.Kind == AnnotationKind.Text && target == HandleTarget.P2) _dragTarget = HandleTarget.Body;
    }

    void SyncPalette(Annotation a)
    {
        _palette.ShowStyleOf(a.Kind is AnnotationKind.Media or AnnotationKind.Blur ? _palette.Color : a.Color, a.Level, a.Soft);
        _palette.SetSoftVisible(a.Kind == AnnotationKind.Blur);
    }

    // ---- text ------------------------------------------------------------------------------------------------------------

    void BeginTextEdit(Annotation a, bool isNew = false)
    {
        CommitText();
        _editingId = isNew ? null : a.Id;
        _pendingNew = isNew ? a : null;
        double sizePx = _renderer.FontSize(a.Level) * _zoom;
        var tl = ToView(a.P1);
        _editor = new TextBox
        {
            Text = a.Text, FontSize = Math.Max(10, sizePx / Dpi), FontWeight = FontWeights.Bold, FontFamily = new FontFamily("Segoe UI"),
            Foreground = new SolidColorBrush(Color.FromRgb(a.Color.R, a.Color.G, a.Color.B)), CaretBrush = Theme.Accent,
            Background = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), BorderBrush = Theme.AccentLine, BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 0, 4, 0), MinWidth = 60, AcceptsReturn = false,
            Template = (ControlTemplate)XamlTextBoxPlain(),
        };
        Canvas.SetLeft(_editor, tl.X - 4);
        Canvas.SetTop(_editor, tl.Y - 2);
        _editor.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitText(); e.Handled = true; }
            else if (e.Key == Key.Escape) { CancelText(); e.Handled = true; }
        };
        _editor.LostKeyboardFocus += (_, _) => CommitText();
        _overlay.Children.Add(_editor);
        _editor.Focus();
        _editor.SelectAll();
        _canvas.InvalidateVisual();
    }

    Annotation? _pendingNew;

    static object XamlTextBoxPlain()
    {
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
        return tpl;
    }

    void CommitText()
    {
        if (_editor == null) return;
        var ed = _editor;
        _editor = null;
        _overlay.Children.Remove(ed);
        var text = ed.Text.Trim();
        var id = _editingId;
        var fresh = _pendingNew;
        _editingId = null; _pendingNew = null;
        if (fresh != null)
        {
            if (text.Length > 0)
            {
                Snapshot();
                var made = fresh with { Text = text };
                _anns.Add(made);
                _sel = made.Id;
            }
        }
        else if (id is { } eid && _anns.FirstOrDefault(a => a.Id == eid) is { } existing)
        {
            if (text.Length == 0) { Snapshot(); _anns.Remove(existing); _sel = null; }
            else if (text != existing.Text) { Snapshot(); Replace(existing with { Text = text }); }
        }
        _canvas.InvalidateVisual();
        _canvas.Focus();
    }

    void CancelText()
    {
        if (_editor == null) return;
        var ed = _editor;
        _editor = null;
        _overlay.Children.Remove(ed);
        _editingId = null; _pendingNew = null;
        _canvas.InvalidateVisual();
        _canvas.Focus();
    }

    // ---- pictures ----------------------------------------------------------------------------------------------------------

    void PasteImage()
    {
        try
        {
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } src)
            {
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(src));
                using var ms = new MemoryStream();
                enc.Save(ms);
                var img = SKImage.FromEncodedData(ms.ToArray());
                if (img != null) AddPicture(img, null);
                return;
            }
            if (Clipboard.ContainsFileDropList())
                foreach (string f in Clipboard.GetFileDropList()) _ = AddFile(f, null);
        }
        catch { }
    }

    async Task DropFiles(DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        var p = ToImage(e.GetPosition(_canvas));
        foreach (var f in files) await AddFile(f, p);
    }

    async Task AddFile(string file, PointD? at)
    {
        using var clip = await MediaClip.LoadAsync(file, allowVideo: false);
        if (clip?.FrameAt(0) is { } frame)
        {
            using var copy = SKBitmap.FromImage(frame);
            AddPicture(SKImage.FromBitmap(copy), at);
        }
    }

    void AddPicture(SKImage img, PointD? at)
    {
        double k = Math.Min(1, Math.Min(_image.Width * 0.4 / img.Width, _image.Height * 0.4 / img.Height));
        double w = img.Width * k, h = img.Height * k;
        var c = at ?? new PointD(_image.Width / 2.0, _image.Height / 2.0);
        double x = Math.Min(Math.Max(c.X - w / 2, 0), Math.Max(0, _image.Width - w));
        double y = Math.Min(Math.Max(c.Y - h / 2, 0), Math.Max(0, _image.Height - h));
        var a = new Annotation { Kind = AnnotationKind.Media, P1 = new PointD(x, y), P2 = new PointD(x + w, y + h), Image = img, Color = Palette.White };
        Snapshot();
        _anns.Add(a);
        _sel = a.Id;
        _palette.SetTool(EditTool.Select);
        _canvas.InvalidateVisual();
    }

    // ---- keys --------------------------------------------------------------------------------------------------------------

    void OnKey(object sender, KeyEventArgs e)
    {
        if (_editor != null) return;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl && e.Key == Key.Z) { if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) Redo(); else Undo(); e.Handled = true; }
        else if (ctrl && e.Key == Key.Y) { Redo(); e.Handled = true; }
        else if (ctrl && e.Key == Key.V) { PasteImage(); e.Handled = true; }
        else if (ctrl && e.Key == Key.D0) { Fit(); _canvas.InvalidateVisual(); e.Handled = true; }
        else if (ctrl && e.Key == Key.Enter) { Upload(); e.Handled = true; }
        else if (ctrl && e.Key == Key.C && SelectedAnn == null) { CopyToClipboard(); e.Handled = true; }
        else if (e.Key is Key.Delete or Key.Back && SelectedAnn != null) { DeleteSelected(); e.Handled = true; }
        else if (e.Key == Key.Escape && _sel != null) { _sel = null; _canvas.InvalidateVisual(); e.Handled = true; }
        else if (!ctrl && e.Key == Key.Enter && SelectedAnn is { Kind: AnnotationKind.Text } t) { BeginTextEdit(t); e.Handled = true; }
    }

    // ---- finishing -----------------------------------------------------------------------------------------------------------

    string Render()
    {
        using var surface = SKSurface.Create(new SKImageInfo(_image.Width, _image.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        _renderer.DrawAll(surface.Canvas, _anns);
        using var snap = surface.Snapshot();
        using var data = snap.Encode(SKEncodedImageFormat.Png, 100);
        var name = Path.GetFileNameWithoutExtension(_file);
        var outFile = Path.Combine(Paths.Temp, $"{name} edited {Guid.NewGuid().ToString("N")[..4]}.png");
        File.WriteAllBytes(outFile, data.ToArray());
        return outFile;
    }

    void Upload()
    {
        CommitText();
        var edited = _anns.Count == 0 ? _file : Render();
        _done = true;
        if (!ReferenceEquals(edited, _file) && _deleteWhenDone) TryDelete(_file);
        App.Current.UploadFile(edited, deleteAfter: _deleteWhenDone || edited != _file);
        Close();
    }

    void CopyToClipboard()
    {
        CommitText();
        var f = _anns.Count == 0 ? _file : Render();
        App.Current.CopyImageFile(f);
        if (f != _file) TryDelete(f);
        _done = true;
        if (_deleteWhenDone) TryDelete(_file);
        Notifier.Info("Copied to clipboard", "The screenshot is on your clipboard.");
        Close();
    }

    void SaveAs()
    {
        CommitText();
        var dlg = new SaveFileDialog { Title = "Save screenshot", Filter = "PNG image|*.png", FileName = Path.GetFileName(_file), InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) };
        if (dlg.ShowDialog() != true) return;
        var f = _anns.Count == 0 ? _file : Render();
        File.Copy(f, dlg.FileName, true);
        if (f != _file) TryDelete(f);
        Notifier.Info("Saved", dlg.FileName);
    }

    void Cancel() => Close();

    void CleanupTemp()
    {
        if (_deleteWhenDone) TryDelete(_file);
    }

    static void TryDelete(string f) { try { File.Delete(f); } catch { } }
}
