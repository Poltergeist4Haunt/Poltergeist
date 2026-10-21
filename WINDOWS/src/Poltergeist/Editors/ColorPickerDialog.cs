using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Poltergeist.Core;

namespace Poltergeist;

/// <summary>Themed colour picker: saturation/brightness square, hue strip, hex field and a before/after swatch.</summary>
sealed class ColorPickerDialog : ChromeWindow
{
    const double Side = 240, HueW = 22;
    public ColorRgba Picked { get; private set; }

    double _h, _s, _v;
    readonly Border _square = new() { Width = Side, Height = Side, CornerRadius = new CornerRadius(10), ClipToBounds = true, Cursor = Cursors.Cross };
    readonly Border _squareHue = new();
    readonly Canvas _squareLayer = new() { Width = Side, Height = Side };
    readonly Border _knob = new() { Width = 16, Height = 16, CornerRadius = new CornerRadius(8), BorderBrush = Brushes.White, BorderThickness = new Thickness(2), IsHitTestVisible = false, Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 4, ShadowDepth = 0, Opacity = 0.7 } };
    readonly Border _hueBar = new() { Width = HueW, Height = Side, CornerRadius = new CornerRadius(11), Cursor = Cursors.Hand };
    readonly Border _hueKnob = new() { Width = HueW + 6, Height = 8, CornerRadius = new CornerRadius(4), BorderBrush = Brushes.White, BorderThickness = new Thickness(2), IsHitTestVisible = false };
    readonly Canvas _hueLayer = new() { Width = HueW, Height = Side };
    readonly Border _after = new() { Height = 36, CornerRadius = new CornerRadius(0, 10, 10, 0) };
    readonly TextBox _hex = new() { Width = 110 };
    bool _updating;

    public ColorPickerDialog(ColorRgba start)
    {
        Section = "Choose a colour";
        ResizeMode = ResizeMode.NoResize;
        ShowMaximize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ToRgbHsv(start, out _h, out _s, out _v);
        Picked = start;

        // square: hue → white (left to right) under a transparent → black (top to bottom) layer
        _squareHue.Width = Side; _squareHue.Height = Side;
        var white = new Border { Width = Side, Height = Side, Background = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), 0) };
        var black = new Border { Width = Side, Height = Side, Background = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, 90) };
        var layers = new Grid { Width = Side, Height = Side };
        layers.Children.Add(_squareHue); layers.Children.Add(white); layers.Children.Add(black); layers.Children.Add(_squareLayer);
        _square.Child = layers;
        _squareLayer.Children.Add(_knob);
        _square.MouseLeftButtonDown += (_, e) => { _square.CaptureMouse(); PickSquare(e.GetPosition(_square)); };
        _square.MouseMove += (_, e) => { if (_square.IsMouseCaptured) PickSquare(e.GetPosition(_square)); };
        _square.MouseLeftButtonUp += (_, _) => _square.ReleaseMouseCapture();

        var stops = new GradientStopCollection();
        for (int i = 0; i <= 6; i++) stops.Add(new GradientStop(FromHsv(i * 60, 1, 1), i / 6.0));
        _hueBar.Background = new LinearGradientBrush(stops, 90);
        _hueLayer.Children.Add(_hueKnob);
        _hueBar.Child = _hueLayer;
        _hueBar.MouseLeftButtonDown += (_, e) => { _hueBar.CaptureMouse(); PickHue(e.GetPosition(_hueBar)); };
        _hueBar.MouseMove += (_, e) => { if (_hueBar.IsMouseCaptured) PickHue(e.GetPosition(_hueBar)); };
        _hueBar.MouseLeftButtonUp += (_, _) => _hueBar.ReleaseMouseCapture();

        var before = new Border { Height = 36, Width = 70, CornerRadius = new CornerRadius(10, 0, 0, 10), Background = new SolidColorBrush(Color.FromRgb(start.R, start.G, start.B)) };
        _after.Width = 70;
        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        swatches.Children.Add(before); swatches.Children.Add(_after);
        var hexRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 14, 0, 0) };
        hexRow.Children.Add(Theme.Label("Hex", 12, Theme.Muted));
        ((TextBlock)hexRow.Children[0]).VerticalAlignment = VerticalAlignment.Center;
        _hex.Margin = new Thickness(8, 0, 0, 0);
        _hex.TextChanged += (_, _) =>
        {
            if (_updating) return;
            if (ColorRgba.TryParse(_hex.Text, out var c)) { ToRgbHsv(c, out _h, out _s, out _v); Refresh(false); }
        };
        hexRow.Children.Add(_hex);
        var row2 = new StackPanel { Orientation = Orientation.Horizontal };
        row2.Children.Add(swatches); row2.Children.Add(hexRow);

        var ok = Theme.Pill("Use colour", () => { DialogResult = true; });
        var cancel = Theme.Pill("Cancel", () => { DialogResult = false; }, prominent: false);
        cancel.Margin = new Thickness(0, 0, 8, 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(cancel); buttons.Children.Add(ok);

        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(_square);
        _hueBar.Margin = new Thickness(16, 0, 0, 0);
        top.Children.Add(_hueBar);
        var all = new StackPanel { Margin = new Thickness(22, 20, 22, 20) };
        all.Children.Add(top); all.Children.Add(row2); all.Children.Add(buttons);
        Body = all;
        Loaded += (_, _) => Refresh(true);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) DialogResult = false; else if (e.Key == Key.Enter && !_hex.IsFocused) DialogResult = true; };
    }

    void PickSquare(System.Windows.Point p)
    {
        _s = Math.Min(1, Math.Max(0, p.X / Side));
        _v = 1 - Math.Min(1, Math.Max(0, p.Y / Side));
        Refresh(true);
    }

    void PickHue(System.Windows.Point p)
    {
        _h = Math.Min(359.99, Math.Max(0, p.Y / Side * 360));
        Refresh(true);
    }

    void Refresh(bool updateHex)
    {
        var c = FromHsv(_h, _s, _v);
        Picked = new ColorRgba(c.R, c.G, c.B);
        _squareHue.Background = new SolidColorBrush(FromHsv(_h, 1, 1));
        _after.Background = new SolidColorBrush(c);
        Canvas.SetLeft(_knob, _s * Side - 8);
        Canvas.SetTop(_knob, (1 - _v) * Side - 8);
        Canvas.SetLeft(_hueKnob, -3);
        Canvas.SetTop(_hueKnob, _h / 360 * Side - 4);
        if (updateHex) { _updating = true; _hex.Text = Picked.ToHex(); _updating = false; }
    }

    static Color FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = (int)(h / 60) switch
        {
            0 => (c, x, 0.0), 1 => (x, c, 0.0), 2 => (0.0, c, x), 3 => (0.0, x, c), 4 => (x, 0.0, c), _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    static void ToRgbHsv(ColorRgba c, out double h, out double s, out double v)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        v = max; s = max <= 0 ? 0 : d / max;
        h = d <= 0 ? 0 : max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
    }
}
