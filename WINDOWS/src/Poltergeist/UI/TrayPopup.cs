using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Poltergeist.Core;

namespace Poltergeist;

/// <summary>The menu that opens from the tray icon: capture, record, upload, recent links, settings. Dark, rounded, ours.</summary>
sealed class TrayPopup : Window
{
    static TrayPopup? _open;
    static DateTime _closedAt = DateTime.MinValue;

    public static void Toggle()
    {
        // Clicking the tray icon deactivates (and so closes) an open menu just before the click arrives: don't reopen it at once.
        if (_open != null) { _open.SafeClose(); return; }
        if ((DateTime.UtcNow - _closedAt).TotalMilliseconds < 250) return;
        _open = new TrayPopup();
        _open.Show();
    }

    readonly StackPanel _items = new();

    TrayPopup()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        FontFamily = Theme.UiFont;
        Foreground = Theme.Text;
        Opacity = 0;

        Build();
        Content = new Border
        {
            Margin = new Thickness(14), Width = 292, Background = Theme.Card, BorderBrush = Theme.BorderStrong,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16), Padding = new Thickness(6), Child = _items,
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Opacity = 0.6, Color = Colors.Black },
        };

        Deactivated += (_, _) => SafeClose();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) SafeClose(); };
        Closed += (_, _) => { _open = null; _closedAt = DateTime.UtcNow; };
        Loaded += (_, _) =>
        {
            PlaceNearTray();
            Opacity = 1;
            Activate();
        };
        ContentRendered += (_, _) => PlaceNearTray();
    }

    bool _closing;
    internal void SafeClose() { if (_closing) return; _closing = true; try { Close(); } catch { } }

    void Build()
    {
        var a = App.Current;
        var s = a.Settings;

        var header = new Grid { Margin = new Thickness(10, 8, 10, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var ghost = GhostIcon.Element(18, Theme.Accent);
        ghost.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(ghost);
        var name = Theme.Label("Poltergeist", 14, Theme.Text, FontWeights.SemiBold);
        name.Margin = new Thickness(9, 0, 0, 0);
        Grid.SetColumn(name, 1);
        header.Children.Add(name);
        _items.Children.Add(header);

        if (a.UploadStatus is { Length: > 0 } status)
        {
            var pill = new Border
            {
                Background = Theme.AccentSoft, BorderBrush = Theme.AccentLine, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(4, 0, 4, 6),
                Child = Theme.Label(status.EndsWith("…") ? status : "Uploading " + status, 12, Theme.Accent, FontWeights.Medium),
            };
            _items.Children.Add(pill);
        }
        if (a.Recorder != null)
            _items.Children.Add(Item("", "Stop recording", null, () => a.Recorder?.RequestStop(), danger: true));

        _items.Children.Add(Section("Capture"));
        _items.Children.Add(Item("", "Capture area", s.Binding(HotKeyAction.Area).Display, () => a.Capture(CaptureMode.Area)));
        _items.Children.Add(Item("", "Capture full screen", s.Binding(HotKeyAction.Fullscreen).Display, () => a.Capture(CaptureMode.Fullscreen)));
        _items.Children.Add(Item("", "Capture window", s.Binding(HotKeyAction.Window).Display, () => a.Capture(CaptureMode.Window)));
        _items.Children.Add(Item("", "Capture area & edit", s.Binding(HotKeyAction.EditArea).Display, () => a.Capture(CaptureMode.Area, edit: true)));

        _items.Children.Add(Section("Record"));
        _items.Children.Add(Item("", "Record area", s.Binding(HotKeyAction.RecordArea).Display, () => a.StartRecording(CaptureMode.Area)));
        _items.Children.Add(Item("", "Record full screen", s.Binding(HotKeyAction.RecordFullscreen).Display, () => a.StartRecording(CaptureMode.Fullscreen)));
        _items.Children.Add(Item("", "Record window", s.Binding(HotKeyAction.RecordWindow).Display, () => a.StartRecording(CaptureMode.Window)));

        _items.Children.Add(Section("Upload"));
        _items.Children.Add(Item("", "Upload files…", null, () => a.ChooseAndUploadFiles()));
        _items.Children.Add(Item("", "Paste clipboard image", null, () => a.UploadClipboardImage()));

        if (a.History.Items.Count > 0)
        {
            _items.Children.Add(Section("Recent uploads"));
            foreach (var h in a.History.Items.Take(5))
            {
                var item = h;
                _items.Children.Add(Item("", Trim(item.Name, 30), Ago(item.Date), () => a.CopyText(s.LinkFormat.Format(item.Url, item.Name)), keepOpenText: "Copied"));
            }
        }

        _items.Children.Add(new Border { Height = 1, Background = Theme.Border, Margin = new Thickness(6, 8, 6, 6) });
        _items.Children.Add(Item("", "Settings…", null, () => a.ShowSettings()));
        _items.Children.Add(Item("", "Quit Poltergeist", null, () => a.Quit()));
    }

    static string Trim(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    static string Ago(DateTime d)
    {
        var t = DateTime.Now - d;
        if (t.TotalMinutes < 1) return "now";
        if (t.TotalHours < 1) return $"{(int)t.TotalMinutes}m";
        if (t.TotalDays < 1) return $"{(int)t.TotalHours}h";
        return $"{(int)t.TotalDays}d";
    }

    static UIElement Section(string text)
    {
        var t = Theme.Label(text.ToUpperInvariant(), 10.5, Theme.Muted, FontWeights.SemiBold);
        t.Margin = new Thickness(12, 10, 0, 3);
        return t;
    }

    UIElement Item(string glyph, string label, string? hint, Action action, bool danger = false, string? keepOpenText = null)
    {
        var grid = new Grid { Background = Brushes.Transparent };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new TextBlock { Text = glyph, FontFamily = Theme.IconFont, FontSize = 14, Foreground = danger ? Theme.Danger : Theme.Muted, VerticalAlignment = VerticalAlignment.Center };
        var text = Theme.Label(label, 13, danger ? Theme.Danger : Theme.Text);
        text.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(text, 1);
        grid.Children.Add(icon);
        grid.Children.Add(text);
        TextBlock? hintText = null;
        if (hint != null)
        {
            hintText = Theme.Label(hint, 11.5, Theme.Muted);
            hintText.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(hintText, 2);
            grid.Children.Add(hintText);
        }
        var border = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 7, 10, 7), Background = Brushes.Transparent, Child = grid, Cursor = Cursors.Hand };
        border.MouseEnter += (_, _) => border.Background = Theme.Hover;
        border.MouseLeave += (_, _) => border.Background = Brushes.Transparent;
        border.MouseLeftButtonUp += async (_, _) =>
        {
            if (keepOpenText != null)
            {
                action();
                if (hintText != null) { hintText.Text = keepOpenText; hintText.Foreground = Theme.Accent; }
                await Task.Delay(450);
                SafeClose();
                return;
            }
            SafeClose();
            await Task.Delay(140);                       // let the menu vanish so it isn't in the screenshot
            action();
        };
        return border;
    }

    /// <summary>Opens beside the tray: the nearest corner of the work area to where the icon is (so any taskbar edge works).</summary>
    void PlaceNearTray()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var cursor = System.Windows.Forms.Cursor.Position;
        var screen = System.Windows.Forms.Screen.FromPoint(cursor);
        var wa = screen.WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        int w = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX), h = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
        int margin = (int)(6 * dpi.DpiScaleX);
        int x = cursor.X < wa.Left + wa.Width / 2 ? wa.Left + margin : wa.Right - w - margin;
        int y = cursor.Y < wa.Top + wa.Height / 2 ? wa.Top + margin : wa.Bottom - h - margin;
        Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, 0x0001 | Native.SWP_NOZORDER);      // SWP_NOSIZE
    }
}
