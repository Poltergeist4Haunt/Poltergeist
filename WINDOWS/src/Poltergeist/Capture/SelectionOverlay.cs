using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Poltergeist;

/// <summary>
/// One full-monitor overlay for picking an area or a window. It shows the frozen, slightly darkened screen (or just a clear
/// pane when freezing is off) and reports the chosen rectangle in screen pixels.
/// </summary>
sealed class SelectionOverlay : Window
{
    public enum Kind { Area, Window }

    readonly Rectangle _bounds;                       // this monitor, in physical pixels
    readonly Kind _kind;
    readonly List<Native.WindowInfo> _windows;
    readonly Canvas _canvas = new();
    readonly System.Windows.Shapes.Path _dim = new() { Fill = new SolidColorBrush(Color.FromArgb(0x8c, 0, 0, 0)), IsHitTestVisible = false };
    readonly System.Windows.Shapes.Rectangle _frame = new() { Stroke = Brushes.White, StrokeThickness = 1.5, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    readonly Border _label = new()
    {
        CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 4, 9, 4), IsHitTestVisible = false, Visibility = Visibility.Collapsed,
        Background = new SolidColorBrush(Color.FromArgb(0xe6, 0x0f, 0x0f, 0x0f)),
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x2a, 255, 255, 255)), BorderThickness = new Thickness(1),
    };
    readonly TextBlock _labelText = new() { FontSize = 12, Foreground = Brushes.White, FontFamily = Theme.UiFont };
    readonly Border _hint = new();
    bool _dragging, _settingBounds;
    System.Windows.Point _start;
    int _layoutRounds;

    public event Action<Rectangle?>? Done;

    public SelectionOverlay(Rectangle bounds, BitmapSource? frozen, bool dim, Kind kind, List<Native.WindowInfo> windows, string hint, string? glyph = null)
    {
        _bounds = bounds;
        _kind = kind;
        _windows = windows;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        ShowInTaskbar = false;
        Topmost = true;
        Background = frozen != null ? Brushes.Black : new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));      // a nearly clear pane still catches the mouse
        Cursor = Cursors.Cross;
        Left = bounds.X; Top = bounds.Y; Width = Math.Max(1, bounds.Width); Height = Math.Max(1, bounds.Height);
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;

        var root = new Grid();
        if (frozen != null) root.Children.Add(new System.Windows.Controls.Image { Source = frozen, Stretch = Stretch.Fill, IsHitTestVisible = false });
        _dim.Visibility = dim ? Visibility.Visible : Visibility.Collapsed;
        _canvas.Children.Add(_dim);
        _canvas.Children.Add(_frame);
        _labelText.FontFamily = Theme.UiFont;
        _label.Child = _labelText;
        _canvas.Children.Add(_label);
        root.Children.Add(_canvas);

        _hint.CornerRadius = new CornerRadius(14);
        _hint.Padding = new Thickness(14, 7, 14, 7);
        _hint.Background = new SolidColorBrush(Color.FromArgb(0xe6, 0x0f, 0x0f, 0x0f));
        _hint.BorderBrush = new SolidColorBrush(Color.FromArgb(0x2a, 255, 255, 255));
        _hint.BorderThickness = new Thickness(1);
        _hint.HorizontalAlignment = HorizontalAlignment.Center;
        _hint.VerticalAlignment = VerticalAlignment.Top;
        _hint.Margin = new Thickness(0, 28, 0, 0);
        _hint.IsHitTestVisible = false;
        var hintRow = new StackPanel { Orientation = Orientation.Horizontal };
        if (glyph != null)
            hintRow.Children.Add(new TextBlock { Text = glyph, FontFamily = Theme.IconFont, FontSize = 14, Foreground = Theme.Accent, Margin = new Thickness(0, 1, 9, 0), VerticalAlignment = VerticalAlignment.Center });
        hintRow.Children.Add(new TextBlock { Text = hint, FontSize = 13, Foreground = Brushes.White, FontFamily = Theme.UiFont, VerticalAlignment = VerticalAlignment.Center });
        _hint.Child = hintRow;
        root.Children.Add(_hint);
        Content = root;
        UpdateDim(null);

        SourceInitialized += (_, _) => Place();
        Loaded += (_, _) => { Place(); Activate(); Focus(); };
        ContentRendered += (_, _) => Place();
        DpiChanged += (_, _) => Place();
        MouseEnter += (_, _) => { if (!IsActive) { Activate(); Focus(); } };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Done?.Invoke(null); } };
        MouseDown += OnDown;
        MouseMove += OnMove;
        MouseUp += OnUp;
    }

    /// <summary>Puts the window exactly over its monitor in physical pixels, whatever DPI WPF thinks it has.</summary>
    void Place()
    {
        if (_settingBounds || _layoutRounds > 6) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        _settingBounds = true;
        _layoutRounds++;
        Native.SetWindowPos(hwnd, new IntPtr(-1), _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height, Native.SWP_SHOWWINDOW);
        _settingBounds = false;
    }

    double ScaleX => VisualTreeHelper.GetDpi(this).DpiScaleX;
    double ScaleY => VisualTreeHelper.GetDpi(this).DpiScaleY;

    Point ToScreenPx(System.Windows.Point p) => new Point(_bounds.X + p.X * ScaleX, _bounds.Y + p.Y * ScaleY);
    System.Windows.Point ToLocal(double sx, double sy) => new((sx - _bounds.X) / ScaleX, (sy - _bounds.Y) / ScaleY);

    void OnDown(object? s, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Right) { Done?.Invoke(null); return; }
        if (e.ChangedButton != MouseButton.Left) return;
        if (_kind == Kind.Window)
        {
            if (WindowAt(e.GetPosition(this)) is { } w) Done?.Invoke(w.Bounds);
            return;
        }
        _dragging = true;
        _start = e.GetPosition(this);
        _hint.Visibility = Visibility.Collapsed;
        CaptureMouse();
        UpdateSelection(_start, _start);
    }

    void OnMove(object? s, MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_kind == Kind.Window)
        {
            var w = WindowAt(p);
            if (w == null) { ShowSelection(null); return; }
            var r = Rectangle.Intersect(w.Bounds, _bounds);
            var tl = ToLocal(r.Left, r.Top);
            var br = ToLocal(r.Right, r.Bottom);
            ShowSelection(new Rect(tl, br), $"{w.Title}  ·  {w.Bounds.Width} × {w.Bounds.Height}");
            return;
        }
        if (_dragging) UpdateSelection(_start, p);
    }

    void OnUp(object? s, MouseButtonEventArgs e)
    {
        if (!_dragging || e.ChangedButton != MouseButton.Left) return;
        _dragging = false;
        ReleaseMouseCapture();
        var a = ToScreenPx(_start);
        var b = ToScreenPx(e.GetPosition(this));
        var r = Rectangle.FromLTRB((int)Math.Round(Math.Min(a.X, b.X)), (int)Math.Round(Math.Min(a.Y, b.Y)),
                                   (int)Math.Round(Math.Max(a.X, b.X)), (int)Math.Round(Math.Max(a.Y, b.Y)));
        if (r.Width < 3 || r.Height < 3) { ShowSelection(null); _hint.Visibility = Visibility.Visible; return; }
        Done?.Invoke(r);
    }

    Native.WindowInfo? WindowAt(System.Windows.Point local)
    {
        var sp = ToScreenPx(local);
        foreach (var w in _windows) if (w.Bounds.Contains((int)sp.X, (int)sp.Y)) return w;
        return null;
    }

    void UpdateSelection(System.Windows.Point a, System.Windows.Point b)
    {
        var r = new Rect(a, b);
        var pa = ToScreenPx(a);
        var pb = ToScreenPx(b);
        ShowSelection(r, $"{(int)Math.Abs(Math.Round(pb.X - pa.X))} × {(int)Math.Abs(Math.Round(pb.Y - pa.Y))}");
    }

    void ShowSelection(Rect? r, string? text = null)
    {
        UpdateDim(r);
        if (r == null) { _frame.Visibility = Visibility.Collapsed; _label.Visibility = Visibility.Collapsed; return; }
        var rect = r.Value;
        Canvas.SetLeft(_frame, rect.X); Canvas.SetTop(_frame, rect.Y);
        _frame.Width = Math.Max(0, rect.Width); _frame.Height = Math.Max(0, rect.Height);
        _frame.Visibility = Visibility.Visible;
        _labelText.Text = text ?? "";
        _label.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        _label.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        double ly = rect.Bottom + 8;
        if (ly + _label.DesiredSize.Height > ActualHeight) ly = Math.Max(4, rect.Top - _label.DesiredSize.Height - 8);
        Canvas.SetLeft(_label, Math.Min(Math.Max(4, rect.X), Math.Max(4, ActualWidth - _label.DesiredSize.Width - 4)));
        Canvas.SetTop(_label, ly);
    }

    void UpdateDim(Rect? hole)
    {
        var full = new RectangleGeometry(new Rect(0, 0, Math.Max(1, ActualWidth > 0 ? ActualWidth : Width), Math.Max(1, ActualHeight > 0 ? ActualHeight : Height)));
        _dim.Data = hole is { } h
            ? new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(h))
            : full;
    }
}
