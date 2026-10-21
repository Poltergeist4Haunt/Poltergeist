using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace Poltergeist;

/// <summary>
/// Every normal Poltergeist window: no Windows title bar, a slim dark one of our own (ghost, name, section, then minimise /
/// maximise / close) that melts into the content, and DWM's dark mode, rounded corners and quiet border.
/// </summary>
class ChromeWindow : Window
{
    const double BarHeight = 44;
    readonly Grid _root = new();
    readonly Border _body = new();
    readonly TextBlock _section = new();
    readonly TextBlock _maxGlyph = new();
    readonly Button _minBtn, _maxBtn, _closeBtn;
    readonly StackPanel _extra = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

    public ChromeWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        Background = Theme.Bg;
        Foreground = Theme.Text;
        FontFamily = Theme.UiFont;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Icon = GhostIcon.AppImage();
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = BarHeight,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });

        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(BarHeight) });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // ---- title bar
        var bar = new Grid { Background = Brushes.Transparent };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        brand.Children.Add(GhostIcon.Element(16, Theme.Accent));
        brand.Children.Add(new TextBlock
        {
            Text = "Poltergeist", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text,
            Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        });
        _section.FontSize = 13;
        _section.Foreground = Theme.Muted;
        _section.VerticalAlignment = VerticalAlignment.Center;
        _section.Margin = new Thickness(0);
        _section.Visibility = Visibility.Collapsed;
        var sep = new TextBlock { Text = "/", FontSize = 13, Foreground = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)), Margin = new Thickness(9, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        _section.Tag = sep;
        brand.Children.Add(sep);
        brand.Children.Add(_section);
        bar.Children.Add(brand);

        Grid.SetColumn(_extra, 1);
        WindowChrome.SetIsHitTestVisibleInChrome(_extra, true);
        bar.Children.Add(_extra);

        _minBtn = CaptionButton("", "Minimise", () => WindowState = WindowState.Minimized);
        _maxBtn = CaptionButton("", "Maximise", () => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized);
        _closeBtn = CaptionButton("", "Close", Close, close: true);
        _maxGlyph = (TextBlock)_maxBtn.Content;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        buttons.Children.Add(_minBtn);
        buttons.Children.Add(_maxBtn);
        buttons.Children.Add(_closeBtn);
        Grid.SetColumn(buttons, 2);
        bar.Children.Add(buttons);

        var hairline = new Border { Height = 1, Background = Theme.Border, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false };
        var barHost = new Grid();
        barHost.Children.Add(bar);
        barHost.Children.Add(hairline);
        _root.Children.Add(barHost);

        Grid.SetRow(_body, 1);
        _root.Children.Add(_body);
        base.Content = _root;

        StateChanged += (_, _) => OnState();
        SourceInitialized += (_, _) =>
        {
            Native.StyleWindow(new WindowInteropHelper(this).Handle);
            OnState();
        };
    }

    /// <summary>The window's section name after "Poltergeist /", e.g. "Settings".</summary>
    public string Section
    {
        get => _section.Text;
        set
        {
            _section.Text = value;
            var vis = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
            _section.Visibility = vis;
            if (_section.Tag is UIElement sep) sep.Visibility = vis;
            Title = string.IsNullOrEmpty(value) ? "Poltergeist" : "Poltergeist – " + value;
        }
    }

    /// <summary>Everything under the title bar.</summary>
    public UIElement? Body { get => _body.Child; set => _body.Child = value; }

    /// <summary>Hides maximise for windows that shouldn't have it.</summary>
    public bool ShowMaximize { set => _maxBtn.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }

    /// <summary>Extra controls left of the window buttons (e.g. a help button).</summary>
    public void AddToTitleBar(UIElement e) => _extra.Children.Add(e);

    void OnState()
    {
        bool max = WindowState == WindowState.Maximized;
        _maxGlyph.Text = max ? "" : "";
        // A maximised borderless window hangs over the screen edge by its resize border: pull the content back in.
        _root.Margin = max ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
    }

    Button CaptionButton(string glyph, string tip, Action click, bool close = false)
    {
        var icon = new TextBlock
        {
            Text = glyph, FontFamily = Theme.IconFont, FontSize = 10, Foreground = Theme.Muted,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var btn = new Button
        {
            Content = icon, Focusable = false, Cursor = Cursors.Arrow,
            Template = BuildCaptionTemplate(close),
        };
        WindowChrome.SetIsHitTestVisibleInChrome(btn, true);
        btn.Click += (_, _) => click();
        btn.ToolTip = tip;
        return btn;
    }

    static ControlTemplate BuildCaptionTemplate(bool close)
    {
        var tpl = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border), "bd");
        border.SetValue(WidthProperty, 46.0);
        border.SetValue(HeightProperty, BarHeight);
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(cp);
        tpl.VisualTree = border;

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, close ? new SolidColorBrush(Color.FromRgb(0xe8, 0x11, 0x23)) : Theme.Hover, "bd"));
        tpl.Triggers.Add(hover);
        var press = new Trigger { Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, Value = true };
        press.Setters.Add(new Setter(Border.BackgroundProperty, close ? new SolidColorBrush(Color.FromRgb(0xb0, 0x0d, 0x1a)) : Theme.Hover, "bd"));
        tpl.Triggers.Add(press);
        return tpl;
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        // Make the glyph turn white on hover/close-hover without a second template: handled by tint on enter/leave of the bar buttons.
    }
}
