using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace Poltergeist;

/// <summary>The little bar that floats beside a recording: time, pause, mute mic, stop, discard. Hidden from the video itself.</summary>
sealed class RecordingControls : Window
{
    readonly ScreenRecorder _rec;
    readonly System.Drawing.Rectangle _region;
    readonly System.Windows.Shapes.Ellipse _dot = new() { Width = 9, Height = 9, Fill = new SolidColorBrush(Color.FromRgb(0xff, 0x45, 0x3a)), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _time = new() { FontFamily = Theme.Mono, FontSize = 13, Foreground = Theme.Text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 6, 0), Text = "0:00" };
    readonly Button _pause, _mic;

    public RecordingControls(ScreenRecorder rec, System.Drawing.Rectangle region)
    {
        _rec = rec;
        _region = region;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = Theme.UiFont;
        Foreground = Theme.Text;

        _pause = Theme.Icon("", "Pause", rec.TogglePause);
        _mic = Theme.Icon("", "Mute microphone", () => rec.SetMicMuted(!rec.MicMuted));
        _mic.Visibility = rec.MicEnabled ? Visibility.Visible : Visibility.Collapsed;
        var discard = Theme.Icon("", "Discard recording", rec.Abort);
        var stop = new Button
        {
            Style = Theme.Style("Pill"), Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(14, 5, 14, 5),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new System.Windows.Shapes.Rectangle { Width = 9, Height = 9, RadiusX = 2, RadiusY = 2, Fill = Theme.Accent, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = "Stop", Foreground = Theme.Accent, Margin = new Thickness(7, 0, 0, 0), FontWeight = FontWeights.Medium },
                },
            },
            ToolTip = "Stop and upload",
        };
        stop.Click += (_, _) => rec.RequestStop();

        var stopEdit = Theme.Pill("Stop & edit", () => rec.RequestStop(edit: true), prominent: false);
        stopEdit.Padding = new Thickness(12, 5, 12, 5);
        stopEdit.Margin = new Thickness(6, 0, 0, 0);
        stopEdit.ToolTip = "Stop and open the video editor";
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new Border { Margin = new Thickness(6, 0, 0, 0), Child = _dot });
        row.Children.Add(_time);
        row.Children.Add(_pause);
        row.Children.Add(_mic);
        row.Children.Add(discard);
        row.Children.Add(stopEdit);
        row.Children.Add(stop);

        Content = new Border
        {
            Margin = new Thickness(14), Padding = new Thickness(8, 6, 8, 6), CornerRadius = new CornerRadius(20),
            Background = Theme.Card, BorderBrush = Theme.BorderStrong, BorderThickness = new Thickness(1), Child = row,
            Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 4, Opacity = 0.55, Color = Colors.Black },
        };

        var blink = new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(700)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        _dot.BeginAnimation(OpacityProperty, blink);

        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            Native.SetWindowDisplayAffinity(h, Native.WDA_EXCLUDEFROMCAPTURE);                  // never part of the video
            Native.SetWindowLong(h, Native.GWL_EXSTYLE, Native.GetWindowLong(h, Native.GWL_EXSTYLE) | Native.WS_EX_TOOLWINDOW);
        };
        Loaded += (_, _) => Place();
        ContentRendered += (_, _) => Place();
    }

    /// <summary>Below the recording if there's room, else above it, else along the bottom of the screen.</summary>
    void Place()
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        int w = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX), ht = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
        var screen = System.Windows.Forms.Screen.FromRectangle(_region).WorkingArea;
        int x = Math.Max(screen.Left, Math.Min(screen.Right - w, _region.X + _region.Width / 2 - w / 2));
        int gap = 8;
        int y = _region.Bottom + gap;
        if (y + ht > screen.Bottom)
        {
            y = _region.Y - ht - gap;
            if (y < screen.Top) y = screen.Bottom - ht - 16;
        }
        Native.SetWindowPos(h, new IntPtr(-1), x, y, 0, 0, 0x0001 | Native.SWP_NOACTIVATE);
    }

    public void Update()
    {
        var t = _rec.Elapsed;
        _time.Text = t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        _pause.Content = _rec.IsPaused ? "" : "";
        _pause.ToolTip = _rec.IsPaused ? "Resume" : "Pause";
        _mic.Content = _rec.MicMuted ? "" : "";
        _mic.Foreground = _rec.MicMuted ? Theme.Danger : Theme.Muted;
        _mic.ToolTip = _rec.MicMuted ? "Unmute microphone" : "Mute microphone";
        _dot.Fill = _rec.IsPaused ? Theme.Warning : new SolidColorBrush(Color.FromRgb(0xff, 0x45, 0x3a));
        if (_rec.IsPaused) _dot.BeginAnimation(OpacityProperty, null); 
        else if (_dot.Opacity >= 1) _dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(700)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
    }
}

/// <summary>A thin accent outline around the recorded area (outside it, so it isn't in the video), click-through.</summary>
sealed class RegionOutline : Window
{
    readonly System.Drawing.Rectangle _bounds;

    public RegionOutline(System.Drawing.Rectangle region)
    {
        _bounds = System.Drawing.Rectangle.Inflate(region, 3, 3);
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        IsHitTestVisible = false;
        Content = new Border { BorderBrush = new SolidColorBrush(Color.FromArgb(0xe0, 0xf2, 0x7d, 0xb3)), BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(2) };
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            Native.SetWindowDisplayAffinity(h, Native.WDA_EXCLUDEFROMCAPTURE);
            Native.SetWindowLong(h, Native.GWL_EXSTYLE, Native.GetWindowLong(h, Native.GWL_EXSTYLE) | Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
        };
        Loaded += (_, _) => Place();
        ContentRendered += (_, _) => Place();
    }

    void Place()
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h != IntPtr.Zero) Native.SetWindowPos(h, new IntPtr(-1), _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }
}
