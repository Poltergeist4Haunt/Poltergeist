using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Poltergeist;

/// <summary>Row builders shared by the settings tabs.</summary>
static class Ui
{
    public static StackPanel Page(params UIElement[] cards)
    {
        var p = new StackPanel();
        foreach (var c in cards)
        {
            if (p.Children.Count > 0 && c is FrameworkElement fe) fe.Margin = new Thickness(0, 16, 0, 0);
            p.Children.Add(c);
        }
        return p;
    }

    public static StackPanel TextBlockPair(string title, string? sub)
    {
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(Theme.Label(title, 14, Theme.Text, FontWeights.Medium, true));
        if (!string.IsNullOrEmpty(sub))
        {
            var s = Theme.Sub(sub);
            s.Margin = new Thickness(0, 2, 0, 0);
            sp.Children.Add(s);
        }
        return sp;
    }

    public static CheckBox Toggle(string title, string? sub, bool value, Action<bool> set)
    {
        var cb = new CheckBox { Style = Theme.Style("Switch"), Content = TextBlockPair(title, sub), IsChecked = value };
        cb.Checked += (_, _) => set(true);
        cb.Unchecked += (_, _) => set(false);
        return cb;
    }

    /// <summary>A label (with optional explanation) on the left and any control on the right.</summary>
    public static Grid Row(string title, string? sub, UIElement control)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(TextBlockPair(title, sub));
        if (control is FrameworkElement fe) { fe.VerticalAlignment = VerticalAlignment.Center; fe.Margin = new Thickness(16, 0, 0, 0); }
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        return g;
    }

    public static ComboBox Combo<T>(IEnumerable<(string Label, T Value)> items, T current, Action<T> set, double width = 200)
    {
        var cb = new ComboBox { Width = width, HorizontalAlignment = HorizontalAlignment.Right };
        int sel = -1, i = 0;
        foreach (var (label, value) in items)
        {
            cb.Items.Add(new ComboBoxItem { Content = label, Tag = value });
            if (EqualityComparer<T>.Default.Equals(value, current)) sel = i;
            i++;
        }
        cb.SelectedIndex = sel;
        cb.SelectionChanged += (_, _) => { if (cb.SelectedItem is ComboBoxItem it && it.Tag is T v) set(v); };
        return cb;
    }

    public static UIElement Field(string label, string value, string placeholder, Action<string> set)
    {
        var sp = new StackPanel();
        var l = Theme.Label(label, 13, Theme.Text, FontWeights.Medium);
        l.Margin = new Thickness(0, 0, 0, 6);
        sp.Children.Add(l);
        sp.Children.Add(Box(value, placeholder, set));
        return sp;
    }

    public static TextBox Box(string value, string placeholder, Action<string> set)
    {
        var tb = new TextBox { Text = value };
        tb.TextChanged += (_, _) => set(tb.Text);
        if (!string.IsNullOrEmpty(placeholder)) tb.ToolTip = placeholder;
        PlaceholderBehavior.Attach(tb, placeholder);
        return tb;
    }

    public static Border Line() => new() { Height = 1, Background = Theme.Border, Margin = new Thickness(0, 4, 0, 4) };

    public static Border Banner(string text)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Text = "", FontFamily = Theme.IconFont, FontSize = 14, Foreground = Theme.Warning, Margin = new Thickness(0, 1, 10, 0), VerticalAlignment = VerticalAlignment.Top });
        var t = Theme.Label(text, 12, Theme.Warning, null, true);
        t.MaxWidth = 560;
        sp.Children.Add(t);
        return new Border
        {
            Padding = new Thickness(12), CornerRadius = new CornerRadius(12), Child = sp,
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0xf5, 0xa6, 0x23)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x59, 0xf5, 0xa6, 0x23)), BorderThickness = new Thickness(1),
        };
    }

    public static Border Chip(string text, bool good)
    {
        var c = good ? Color.FromRgb(0x4a, 0xde, 0x80) : Color.FromRgb(0xf5, 0xa6, 0x23);
        return new Border
        {
            CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 2, 8, 2), Background = new SolidColorBrush(Color.FromArgb(0x1f, c.R, c.G, c.B)),
            Child = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.Medium, Foreground = new SolidColorBrush(c) }, VerticalAlignment = VerticalAlignment.Center,
        };
    }
}

/// <summary>Grey hint text inside an empty TextBox.</summary>
static class PlaceholderBehavior
{
    public static void Attach(TextBox tb, string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        tb.Loaded += (_, _) =>
        {
            var layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(tb);
            if (layer == null) return;
            var ad = new HintAdorner(tb, text);
            layer.Add(ad);
        };
    }

    sealed class HintAdorner : System.Windows.Documents.Adorner
    {
        readonly TextBox _tb;
        readonly string _text;
        public HintAdorner(TextBox tb, string text) : base(tb)
        {
            _tb = tb; _text = text;
            IsHitTestVisible = false;
            tb.TextChanged += (_, _) => InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (_tb.Text.Length > 0) return;
            var ft = new FormattedText(_text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(Theme.UiFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), _tb.FontSize,
                new SolidColorBrush(Color.FromArgb(0x70, 0x8a, 0x8a, 0x8a)), VisualTreeHelper.GetDpi(_tb).PixelsPerDip);
            dc.DrawText(ft, new System.Windows.Point(_tb.Padding.Left + 1, (_tb.ActualHeight - ft.Height) / 2));
        }
    }
}
