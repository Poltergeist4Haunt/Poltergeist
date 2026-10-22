using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Poltergeist;

/// <summary>A bigger look at one gallery picture or video, with Copy link and Open.</summary>
sealed class MediaPreviewWindow : ChromeWindow
{
    readonly MediaElement? _video;
    bool _playing = true;

    public MediaPreviewWindow(string name, string url, bool video, Action copy, string openUrl)
    {
        Section = name.Length > 36 ? name[..35] + "…" : name;
        Width = 960; Height = 680; MinWidth = 560; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var stage = new Grid { Background = new SolidColorBrush(Color.FromRgb(0x05, 0x05, 0x05)) };
        var status = Theme.Label("Loading…", 13, Theme.Muted);
        status.HorizontalAlignment = HorizontalAlignment.Center; status.VerticalAlignment = VerticalAlignment.Center;
        stage.Children.Add(status);

        if (video)
        {
            _video = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, Stretch = Stretch.Uniform, Source = new Uri(url) };
            _video.MediaOpened += (_, _) => status.Visibility = Visibility.Collapsed;
            _video.MediaFailed += (_, _) => status.Text = "Can't play this here. Use Open to watch it in your browser.";
            _video.MediaEnded += (_, _) => { _video.Position = TimeSpan.Zero; _video.Pause(); _playing = false; };
            stage.Children.Add(_video);
            stage.MouseLeftButtonUp += (_, _) => { if (_playing) _video.Pause(); else _video.Play(); _playing = !_playing; };
            Loaded += (_, _) => _video.Play();
        }
        else
        {
            var img = new System.Windows.Controls.Image { Stretch = Stretch.Uniform, Margin = new Thickness(12) };
            stage.Children.Add(img);
            _ = LoadImage(url, img, status);
        }

        var copyBtn = Theme.Pill("Copy link", copy);
        var openBtn = Theme.Pill("Open in browser", () => App.OpenUrl(openUrl), false);
        var close = Theme.Pill("Close", Close, false);
        foreach (var b in new[] { copyBtn, openBtn, close }) b.Margin = new Thickness(8, 0, 0, 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        buttons.Children.Add(copyBtn); buttons.Children.Add(openBtn); buttons.Children.Add(close);
        var hint = Theme.Label(video ? "Click the video to pause or play" : "", 12, Theme.Muted);
        hint.VerticalAlignment = VerticalAlignment.Center; hint.Margin = new Thickness(16, 0, 0, 0);
        var bar = new Grid { Height = 56, Background = Theme.Card };
        bar.Children.Add(hint); bar.Children.Add(buttons);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(stage);
        var barBorder = new Border { BorderBrush = Theme.Border, BorderThickness = new Thickness(0, 1, 0, 0), Child = bar };
        Grid.SetRow(barBorder, 1); root.Children.Add(barBorder);
        Body = root;

        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Closed += (_, _) => { try { _video?.Close(); } catch { } };
    }

    static async Task LoadImage(string url, System.Windows.Controls.Image img, TextBlock status)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0 Safari/537.36");
            var bytes = await http.GetByteArrayAsync(url);
            var bi = new BitmapImage();
            bi.BeginInit(); bi.StreamSource = new MemoryStream(bytes); bi.CacheOption = BitmapCacheOption.OnLoad; bi.EndInit(); bi.Freeze();
            img.Source = bi;
            status.Visibility = Visibility.Collapsed;
        }
        catch { status.Text = "Couldn't load the preview. Use Open to see it in your browser."; }
    }
}
