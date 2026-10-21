using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Poltergeist;

/// <summary>The little preview that slides into the bottom-right corner after an upload, with a Copy link button; it fades away.</summary>
sealed class PreviewToast : Window
{
    static PreviewToast? _current;
    readonly DispatcherTimer _timer = new();
    bool _closing;

    public static void ShowFor(string name, string copyText, byte[]? thumb)
    {
        _current?.Dismiss(false);
        _current = new PreviewToast(name, copyText, thumb);
        _current.Show();
    }

    /// <summary>A plain message in the same corner popup (used instead of Windows notifications).</summary>
    public static void ShowMessage(string title, string message, bool error = false)
    {
        _current?.Dismiss(false);
        _current = new PreviewToast(message, null, null, title, error);
        _current.Show();
    }

    PreviewToast(string name, string? copyText, byte[]? thumb, string label = "Uploaded", bool error = false)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Width = 372; SizeToContent = SizeToContent.Height;                     // a 16px margin around the 340×92 card leaves room for its shadow
        Foreground = Theme.Text;
        Opacity = 0;

        var picture = new Border { Width = 64, Height = 64, CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.FromArgb(15, 255, 255, 255)), ClipToBounds = true };
        if (thumb != null)
        {
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit(); bi.StreamSource = new MemoryStream(thumb); bi.CacheOption = BitmapCacheOption.OnLoad; bi.EndInit(); bi.Freeze();
                picture.Child = new System.Windows.Controls.Image { Source = bi, Stretch = Stretch.UniformToFill };
            }
            catch { }
        }
        if (picture.Child == null)
            picture.Child = new TextBlock { Text = "", FontFamily = Theme.IconFont, FontSize = 22, Foreground = Theme.Muted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

        var copy = Theme.Pill("Copy link", () => { App.Current.CopyText(copyText ?? ""); Dismiss(true); });
        copy.Padding = new Thickness(14, 5, 14, 5);
        copy.HorizontalAlignment = HorizontalAlignment.Left;

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        text.Children.Add(Theme.Label(label, 11, error ? Theme.Danger : Theme.Accent, FontWeights.Medium));
        var title = Theme.Label(name, 13, Theme.Text, FontWeights.Medium);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.Margin = new Thickness(0, 2, 0, 8);
        title.MaxWidth = 190;
        if (copyText == null) { title.TextWrapping = TextWrapping.Wrap; title.TextTrimming = TextTrimming.None; title.Margin = new Thickness(0, 2, 0, 0); title.MaxHeight = 60; }
        text.Children.Add(title);
        if (copyText != null) text.Children.Add(copy);

        var close = Theme.Icon("", "Dismiss", () => Dismiss(true));
        close.Width = 24; close.Height = 24; close.FontSize = 10;
        close.VerticalAlignment = VerticalAlignment.Top;

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(picture);
        Grid.SetColumn(text, 1); row.Children.Add(text);
        Grid.SetColumn(close, 2); row.Children.Add(close);

        Content = new Border
        {
            Margin = new Thickness(16), Padding = new Thickness(14), MinHeight = 92, Background = Theme.Card, BorderBrush = Theme.Border,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(18), Child = row,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Opacity = 0.55, Color = Colors.Black },
        };

        MouseEnter += (_, _) => _timer.Stop();
        MouseLeave += (_, _) => Restart(2);
        _timer.Tick += (_, _) => { _timer.Stop(); Dismiss(true); };

        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            Native.SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | Native.SWP_NOACTIVATE);   // topmost, keep size/position
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - Width; Top = wa.Bottom - 124;
        };
        Loaded += (_, _) =>
        {
            var wa = SystemParameters.WorkArea;
            Top = wa.Bottom - ActualHeight;
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
            Restart(6);
        };
    }

    void Restart(double seconds)
    {
        _timer.Stop();
        _timer.Interval = TimeSpan.FromSeconds(seconds);
        _timer.Start();
    }

    void Dismiss(bool animated)
    {
        if (_closing) return;
        _closing = true;
        _timer.Stop();
        if (ReferenceEquals(_current, this)) _current = null;
        if (!animated) { Close(); return; }
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(450)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }
}
