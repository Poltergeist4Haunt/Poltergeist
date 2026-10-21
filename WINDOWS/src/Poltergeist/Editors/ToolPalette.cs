using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Poltergeist.Core;

namespace Poltergeist;

public enum EditTool { Select, Arrow, Curve, Pen, Marker, Box, Circle, Text, Blur, Zoom }

static class EditToolInfo
{
    public static string Title(this EditTool t) => t switch
    {
        EditTool.Select => "Select", EditTool.Arrow => "Arrow", EditTool.Curve => "Curve", EditTool.Pen => "Pen", EditTool.Marker => "Marker",
        EditTool.Box => "Box", EditTool.Circle => "Circle", EditTool.Text => "Text", EditTool.Blur => "Blur", EditTool.Zoom => "Zoom", _ => t.ToString(),
    };

    public static string Help(this EditTool t) => t switch
    {
        EditTool.Select => "Click things to move or resize them",
        EditTool.Curve => "Draw a curved arrow by hand: drag the path, the arrowhead goes on the end",
        EditTool.Pen => "Draw freehand",
        EditTool.Marker => "Highlight with a wide, see-through marker",
        EditTool.Zoom => "Drag over the part of the video to zoom into",
        _ => t.Title(),
    };

    public static AnnotationKind? Kind(this EditTool t) => t switch
    {
        EditTool.Arrow => AnnotationKind.Arrow, EditTool.Curve => AnnotationKind.Curve, EditTool.Pen => AnnotationKind.Pen, EditTool.Marker => AnnotationKind.Marker,
        EditTool.Box => AnnotationKind.Box, EditTool.Circle => AnnotationKind.Circle, EditTool.Text => AnnotationKind.Text, EditTool.Blur => AnnotationKind.Blur,
        EditTool.Zoom => AnnotationKind.Zoom, _ => null,
    };

    public static bool IsFreehand(this EditTool t) => t is EditTool.Pen or EditTool.Marker or EditTool.Curve;
}

/// <summary>Tool chips, colours, sizes and the soft-blur switch: the strip across the top of both editors.</summary>
sealed class ToolPalette : Border
{
    public EditTool Tool { get; private set; } = EditTool.Arrow;
    public ColorRgba Color { get; private set; } = Palette.Accent;
    public int Level { get; private set; } = 2;
    public bool Soft { get; private set; }

    /// <summary>Raised after the user changes something here; <c>what</c> is "tool", "color", "level" or "soft".</summary>
    public event Action<string>? Changed;
    bool _quiet;
    void Raise(string what) { if (!_quiet) Changed?.Invoke(what); }

    readonly WrapPanel _tools = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly Dictionary<EditTool, RadioButton> _toolButtons = new();
    readonly List<(Border Ring, ColorRgba Color)> _swatches = new();
    readonly Border _custom = new();
    readonly Ellipse _customDot = new() { Width = 18, Height = 18 };
    readonly List<RadioButton> _levels = new();
    readonly CheckBox _soft;
    ColorRgba _customColor = ColorRgba.FromHex(0x8a5cff);
    bool _customChosen;

    public ToolPalette(bool allowZoom)
    {
        Background = Theme.Card;
        BorderBrush = Theme.Border;
        BorderThickness = new Thickness(0, 0, 0, 1);
        Padding = new Thickness(14, 8, 14, 8);

        foreach (var t in Enum.GetValues<EditTool>())
        {
            if (t == EditTool.Zoom && !allowZoom) continue;
            var rb = new RadioButton { Style = Theme.Style("Tab"), Content = t.Title(), GroupName = "tool" + GetHashCode(), ToolTip = t.Help(), IsChecked = t == Tool, Margin = new Thickness(0, 0, 4, 0) };
            rb.Padding = new Thickness(0);
            var tool = t;
            rb.Checked += (_, _) => { Tool = tool; Raise("tool"); };
            _toolButtons[t] = rb;
            _tools.Children.Add(rb);
        }

        var colors = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        foreach (var c in Palette.Presets) colors.Children.Add(MakeSwatch(c));
        // custom colour
        _customDot.Fill = ToBrush(_customColor);
        _custom.Width = 26; _custom.Height = 26; _custom.CornerRadius = new CornerRadius(13); _custom.Margin = new Thickness(3, 0, 3, 0);
        _custom.BorderThickness = new Thickness(2); _custom.BorderBrush = Brushes.Transparent; _custom.Cursor = Cursors.Hand;
        _custom.Background = Brushes.Transparent;
        _custom.Child = new Grid
        {
            Children =
            {
                new Ellipse
                {
                    Width = 18, Height = 18,
                    Fill = new LinearGradientBrush(new GradientStopCollection
                    {
                        new GradientStop(Colors.Red, 0), new GradientStop(Colors.Yellow, 0.25), new GradientStop(Colors.Lime, 0.5),
                        new GradientStop(Colors.Cyan, 0.7), new GradientStop(Colors.Magenta, 1),
                    }, 45),
                },
                new Ellipse { Width = 8, Height = 8, Fill = _customDot.Fill },
            },
        };
        _custom.ToolTip = "Choose any colour";
        _custom.MouseLeftButtonUp += (_, _) => PickCustom();
        colors.Children.Add(_custom);

        var sizes = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        foreach (var (label, level) in new[] { ("S", 1), ("M", 2), ("L", 3) })
        {
            var rb = new RadioButton { Style = Theme.Style("Tab"), Content = label, GroupName = "level" + GetHashCode(), IsChecked = level == Level, Margin = new Thickness(0, 0, 2, 0), ToolTip = "Size" };
            var l = level;
            rb.Checked += (_, _) => { Level = l; Raise("level"); };
            _levels.Add(rb);
            sizes.Children.Add(rb);
        }

        _soft = new CheckBox { Style = Theme.Style("Switch"), Content = Theme.Label("Soft blur", 12, Theme.Muted), Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        _soft.Checked += (_, _) => { Soft = true; Raise("soft"); };
        _soft.Unchecked += (_, _) => { Soft = false; Raise("soft"); };
        _soft.ToolTip = "A smooth blur instead of pixels. Works better on big images";

        var wrap = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        wrap.Children.Add(_tools);
        wrap.Children.Add(colors);
        wrap.Children.Add(sizes);
        wrap.Children.Add(_soft);
        Child = wrap;
        UpdateSwatches();
    }

    static Brush ToBrush(ColorRgba c) => new SolidColorBrush(System.Windows.Media.Color.FromRgb(c.R, c.G, c.B));

    UIElement MakeSwatch(ColorRgba c)
    {
        var ring = new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Margin = new Thickness(3, 0, 3, 0), BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent,
            Cursor = Cursors.Hand, Background = Brushes.Transparent,
            Child = new Ellipse { Width = 18, Height = 18, Fill = ToBrush(c), Stroke = new SolidColorBrush(System.Windows.Media.Color.FromArgb(60, 255, 255, 255)), StrokeThickness = 1 },
        };
        ring.MouseLeftButtonUp += (_, _) => { Color = c; _customChosen = false; UpdateSwatches(); Raise("color"); };
        _swatches.Add((ring, c));
        return ring;
    }

    void PickCustom()
    {
        var dlg = new ColorPickerDialog(_customColor) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        _customColor = dlg.Picked;
        _customDot.Fill = ToBrush(_customColor);
        ((Ellipse)((Grid)_custom.Child).Children[1]).Fill = _customDot.Fill;
        Color = _customColor;
        _customChosen = true;
        UpdateSwatches();
        Raise("color");
    }

    void UpdateSwatches()
    {
        foreach (var (ring, c) in _swatches) ring.BorderBrush = !_customChosen && c == Color ? Theme.Text : Brushes.Transparent;
        _custom.BorderBrush = _customChosen ? Theme.Text : Brushes.Transparent;
    }

    // ---- driven by the editor (selection changes show up here) -------------------------------------------------------------

    public void SetTool(EditTool t) { if (_toolButtons.TryGetValue(t, out var rb)) rb.IsChecked = true; }

    public void ShowStyleOf(ColorRgba color, int level, bool soft)
    {
        _quiet = true;
        Color = color;
        _customChosen = !Palette.Presets.Contains(color);
        if (_customChosen) { _customColor = color; ((Ellipse)((Grid)_custom.Child).Children[1]).Fill = ToBrush(color); }
        UpdateSwatches();
        Level = level;
        if (level is >= 1 and <= 3) _levels[level - 1].IsChecked = true;
        Soft = soft;
        _soft.IsChecked = soft;
        _quiet = false;
    }

    public void SetSoftVisible(bool visible) => _soft.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
}
