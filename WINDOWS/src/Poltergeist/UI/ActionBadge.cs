using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Poltergeist;

/// <summary>
/// A small pill near the top of the screen saying what Poltergeist is doing right now ("Taking a screenshot of an area",
/// "Recording full screen"), with an icon, so a wrong hotkey is noticed straight away.
/// </summary>
sealed class ActionBadge : Window
{
    static ActionBadge? _current;
    readonly DispatcherTimer _timer = new();
    bool _closing;

    public const string Camera = "", Record = "", Edit = "", Window_ = "", Screen = "";

    public static void Show(string glyph, string text, int ms = 1600, bool recording = false)
    {
        _current?.Dismiss(false);
        _current = new ActionBadge(glyph, text, ms, recording);
        _current.Show();
    }

    public static void Hide() => _current?.Dismiss(true);

    ActionBadge(string glyph, string text, int ms, bool recording)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Foreground = Theme.Text;
        Opacity = 0;

        var icon = new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(15), VerticalAlignment = VerticalAlignment.Center,
            Background = recording ? new SolidColorBrush(Color.FromArgb(0x33, 0xff, 0x45, 0x3a)) : Theme.AccentSoft,
            Child = new TextBlock
            {
                Text = glyph, FontFamily = Theme.IconFont, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Foreground = recording ? new SolidColorBrush(Color.FromRgb(0xff, 0x45, 0x3a)) : Theme.Accent,
            },
        };
        var label = Theme.Label(text, 13.5, Theme.Text, FontWeights.Medium);
        label.Margin = new Thickness(10, 0, 4, 0);
        label.VerticalAlignment = VerticalAlignment.Center;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(icon); row.Children.Add(label);
        Content = new Border
        {
            Margin = new Thickness(18), Padding = new Thickness(8, 8, 18, 8), CornerRadius = new CornerRadius(24),
            Background = Theme.Card, BorderBrush = Theme.BorderStrong, BorderThickness = new Thickness(1), Child = row,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 22, ShadowDepth = 5, Opacity = 0.55, Color = Colors.Black },
        };

        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            Native.SetWindowLong(h, Native.GWL_EXSTYLE, Native.GetWindowLong(h, Native.GWL_EXSTYLE) | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED);
            Native.SetWindowDisplayAffinity(h, Native.WDA_EXCLUDEFROMCAPTURE);     // never ends up in the screenshot or recording
        };
        Loaded += (_, _) =>
        {
            Place();
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
            if (ms > 0) { _timer.Interval = TimeSpan.FromMilliseconds(ms); _timer.Tick += (_, _) => { _timer.Stop(); Dismiss(true); }; _timer.Start(); }
        };
    }

    /// <summary>Top centre of the screen the mouse is on.</summary>
    void Place()
    {
        var h = new WindowInteropHelper(this).Handle;
        var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        int w = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX);
        Native.SetWindowPos(h, new IntPtr(-1), screen.Left + (screen.Width - w) / 2, screen.Top + (int)(20 * dpi.DpiScaleY), 0, 0, 0x0001 | Native.SWP_NOACTIVATE);
    }

    void Dismiss(bool animated)
    {
        if (_closing) return;
        _closing = true;
        _timer.Stop();
        if (ReferenceEquals(_current, this)) _current = null;
        if (!animated) { Close(); return; }
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(260));
        fade.Completed += (_, _) => { try { Close(); } catch { } };
        BeginAnimation(OpacityProperty, fade);
    }
}
