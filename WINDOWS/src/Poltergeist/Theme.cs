using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Poltergeist;

/// <summary>Haunt's palette and small helpers for building UI in code.</summary>
static class Theme
{
    static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    public static Brush Bg => Res("BgBrush");
    public static Brush Card => Res("CardBrush");
    public static Brush Field => Res("FieldBrush");
    public static Brush Border => Res("BorderBrush");
    public static Brush BorderStrong => Res("BorderStrongBrush");
    public static Brush Accent => Res("AccentBrush");
    public static Brush AccentSoft => Res("AccentSoftBrush");
    public static Brush AccentLine => Res("AccentLineBrush");
    public static Brush Text => Res("TextBrush");
    public static Brush Muted => Res("MutedBrush");
    public static Brush Warning => Res("WarningBrush");
    public static Brush Danger => Res("DangerBrush");
    public static Brush Hover => Res("HoverBrush");

    public static readonly System.Windows.Media.Color AccentColor = System.Windows.Media.Color.FromRgb(0xf2, 0x7d, 0xb3);
    public static readonly FontFamily UiFont = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily IconFont = new("Segoe MDL2 Assets");
    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    public static Style Style(string key) => (Style)Application.Current.FindResource(key);

    // ---- builders -----------------------------------------------------------------------------------------------------

    public static TextBlock Label(string text, double size = 13, Brush? color = null, FontWeight? weight = null, bool wrap = false) => new()
    {
        Text = text, FontSize = size, Foreground = color ?? Text, FontWeight = weight ?? FontWeights.Normal,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, FontFamily = UiFont,
    };

    public static TextBlock Title(string text) => Label(text, 15, null, FontWeights.SemiBold);
    public static TextBlock Sub(string text) => Label(text, 12, Muted, null, true);

    public static Button Pill(string text, Action click, bool prominent = true)
    {
        var b = new Button { Content = text, Style = Style(prominent ? "Pill" : "Ghost") };
        b.Click += (_, _) => click();
        return b;
    }

    public static Button Icon(string glyph, string tip, Action click)
    {
        var b = new Button { Content = glyph, Style = Style("IconBtn"), ToolTip = tip };
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>A rounded dark panel with a hairline border.</summary>
    public static Border CardPanel(params UIElement[] children)
    {
        var stack = new StackPanel();
        foreach (var c in children) { if (stack.Children.Count > 0 && c is FrameworkElement fe && fe.Margin == default) fe.Margin = new Thickness(0, 12, 0, 0); stack.Children.Add(c); }
        return new Border
        {
            Background = Card, BorderBrush = Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(18),
            Padding = new Thickness(18), Child = stack,
        };
    }

    public static ImageSource Glyph(string text, double size, Brush brush)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var ft = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(IconFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), size, brush, 1.0);
            dc.DrawText(ft, new System.Windows.Point(0, 0));
        }
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(size * 1.5), (int)Math.Ceiling(size * 1.5), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(dv);
        return rtb;
    }
}
