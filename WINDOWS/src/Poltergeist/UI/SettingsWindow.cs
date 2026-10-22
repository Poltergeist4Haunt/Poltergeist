using System.Collections.Specialized;
using System.Net.Http;
using System.Windows.Media.Imaging;
using SkiaSharp;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using Poltergeist.Core;

namespace Poltergeist;

sealed class SettingsWindow : ChromeWindow
{
    static readonly (string Name, string Glyph)[] Tabs =
    {
        ("Upload", "\uE898"), ("Gallery", "\uEB9F"), ("Account", "\uE8D7"), ("General", "\uE713"), ("Recordings", "\uE7C8"),
        ("Effects", "\uE790"), ("Embed", "\uE8A5"), ("Domain", "\uE774"),
    };

    readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 4, 0) };
    readonly Dictionary<string, RadioButton> _tabButtons = new();
    string _tab = "Upload";
    StackPanel? _uploadList;

    AppSettings S => App.Current.Settings;

    readonly Grid _shell = new();
    string? _startTab;

    public SettingsWindow(string? tab)
    {
        Section = "Settings";
        Width = 968; Height = 760; MinWidth = 640; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _startTab = tab;

        _scroll.SizeChanged += (_, _) => FitTiles();
        Body = _shell;
        BuildLayout();

        AllowDrop = true;
        Drop += (_, e) => DropFiles(e);
        Loaded += (_, _) => SelectTab(_startTab ?? (string.IsNullOrEmpty(App.Current.ApiKey) ? "Account" : "Upload"));
        App.Current.Uploads.Jobs.CollectionChanged += JobsChanged;
        Closed += (_, _) => { App.Current.Uploads.Jobs.CollectionChanged -= JobsChanged; DisposeGallery(); };
    }

    /// <summary>The menu either runs down the left (like the Haunt dashboard) or across the top; the footer with the credits is always there.</summary>
    void BuildLayout()
    {
        (_scroll.Parent as Panel)?.Children.Remove(_scroll);
        _shell.Children.Clear();
        _shell.RowDefinitions.Clear();
        _shell.ColumnDefinitions.Clear();
        _tabButtons.Clear();
        _shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var main = new Grid();
        bool side = S.SettingsLayout != "top";
        if (side)
        {
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(212) });
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var list = new StackPanel { Margin = new Thickness(14, 18, 14, 14) };
            foreach (var (name, glyph) in Tabs) list.Children.Add(MakeTab(name, glyph, side: true));
            var sideBar = new Border
            {
                Background = Theme.Card, BorderBrush = Theme.Border, BorderThickness = new Thickness(0, 0, 1, 0),
                Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list },
            };
            main.Children.Add(sideBar);
            _scroll.Margin = new Thickness(26, 18, 20, 0);
            Grid.SetColumn(_scroll, 1);
            main.Children.Add(_scroll);
        }
        else
        {
            main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var strip = new WrapPanel { Margin = new Thickness(24, 16, 24, 12) };
            foreach (var (name, glyph) in Tabs) strip.Children.Add(MakeTab(name, glyph, side: false));
            main.Children.Add(strip);
            _scroll.Margin = new Thickness(24, 0, 20, 0);
            Grid.SetRow(_scroll, 1);
            main.Children.Add(_scroll);
        }
        _shell.Children.Add(main);
        var footer = Footer();
        Grid.SetRow(footer, 1);
        _shell.Children.Add(footer);
    }

    RadioButton MakeTab(string name, string glyph, bool side)
    {
        var rb = new RadioButton { Style = Theme.Style(side ? "SideTab" : "Tab"), GroupName = "tabs", Margin = side ? new Thickness(0, 0, 0, 4) : new Thickness(0, 0, 6, 4) };
        var n = name;
        rb.Content = TabContent(glyph, name, rb);
        rb.Checked += (_, _) => { _tab = n; Render(); };
        _tabButtons[name] = rb;
        return rb;
    }

    /// <summary>Credits and the version, along the bottom of every page.</summary>
    UIElement Footer()
    {
        TextBlock T(string s, Brush? c = null) => new() { Text = s, FontSize = 12, Foreground = c ?? Theme.Muted, VerticalAlignment = VerticalAlignment.Center };
        TextBlock Link(string text, string? url)
        {
            var tb = T(text, Theme.Accent);
            tb.Cursor = Cursors.Hand;
            if (url != null) tb.MouseLeftButtonUp += (_, _) => App.OpenUrl(url);
            tb.MouseEnter += (_, _) => tb.TextDecorations = TextDecorations.Underline;
            tb.MouseLeave += (_, _) => tb.TextDecorations = null;
            return tb;
        }
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(T("Created by "));
        left.Children.Add(Link("Pix/Nico", AppInfo.CreatorUrl));
        left.Children.Add(T("   ·   "));
        left.Children.Add(Link("A Person with a Creative Mind.", AppInfo.FriendUrl));
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(T("v" + AppInfo.Version + "   ·   "));
        var check = Link("Check for updates", null);
        check.MouseLeftButtonUp += (_, _) => _ = Updater.CheckAsync(manual: true);
        right.Children.Add(check);
        var g = new Grid { Margin = new Thickness(22, 0, 22, 0), Height = 38 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(left);
        Grid.SetColumn(right, 1); g.Children.Add(right);
        return new Border { BorderBrush = Theme.Border, BorderThickness = new Thickness(0, 1, 0, 0), Child = g, Background = Theme.Bg };
    }

    static StackPanel TabContent(string glyph, string name, RadioButton owner)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        var g = new TextBlock { Text = glyph, FontFamily = Theme.IconFont, FontSize = 13, Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        var t = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center };
        // The tab style colours the content presenter's foreground; mirror it onto the pieces.
        var bind = new System.Windows.Data.Binding("Foreground") { Source = owner };
        g.SetBinding(TextBlock.ForegroundProperty, bind);
        t.SetBinding(TextBlock.ForegroundProperty, bind);
        sp.Children.Add(g);
        sp.Children.Add(t);
        return sp;
    }

    public void SelectTab(string name)
    {
        if (_tabButtons.TryGetValue(name, out var rb)) { rb.IsChecked = true; if (_tab == name) Render(); }
    }

    void Render()
    {
        _uploadList = null;
        _tiles.Clear(); _pics.Clear();
        DisposeGallery();
        _scroll.VerticalScrollBarVisibility = _tab == "Gallery" ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        _scroll.Content = _tab switch
        {
            "Upload" => UploadTab(),
            "Gallery" => GalleryTab(),
            "Effects" => EffectsTab(),
            "Account" => AccountTab(),
            "General" => GeneralTab(),
            "Recordings" => RecordingsTab(),
            "Embed" => EmbedTab(),
            _ => DomainTab(),
        };
        _scroll.ScrollToTop();
    }

    static StackPanel Pad(StackPanel p) { p.Margin = new Thickness(0, 0, 0, 28); return p; }

    // ---- Upload ------------------------------------------------------------------------------------------------------------

    void DropFiles(DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) App.Current.UploadFiles(files.Where(f => !Directory.Exists(f)));
        e.Handled = true;
    }

    UIElement UploadTab()
    {
        var page = new List<UIElement>();
        if (string.IsNullOrEmpty(App.Current.ApiKey)) page.Add(Ui.Banner("Add your API key in the Account tab before uploading."));

        var icon = new TextBlock { Text = "", FontFamily = Theme.IconFont, FontSize = 30, Foreground = Theme.Accent, HorizontalAlignment = HorizontalAlignment.Center };
        var title = Theme.Label("Drop files here", 16, Theme.Text, FontWeights.SemiBold);
        title.HorizontalAlignment = HorizontalAlignment.Center; title.Margin = new Thickness(0, 10, 0, 4);
        var hint = Theme.Label("Images and videos Haunt supports: PNG, JPEG, WebP, GIF, AVIF, MP4, WebM, MOV", 12, Theme.Muted);
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        var choose = Theme.Pill("Choose files…", App.Current.ChooseAndUploadFiles);
        choose.HorizontalAlignment = HorizontalAlignment.Center; choose.Margin = new Thickness(0, 14, 0, 0);
        var inner = new StackPanel { Margin = new Thickness(0, 34, 0, 34) };
        foreach (var c in new UIElement[] { icon, title, hint, choose }) inner.Children.Add(c);
        var zone = new Border
        {
            CornerRadius = new CornerRadius(18), Background = Theme.Card, Child = inner, AllowDrop = true,
            BorderBrush = Theme.AccentLine, BorderThickness = new Thickness(1.5),
        };
        // dashed outline
        var dashed = new Grid();
        dashed.Children.Add(zone);
        dashed.Children.Add(new System.Windows.Shapes.Rectangle
        {
            RadiusX = 18, RadiusY = 18, Stroke = Theme.Accent, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 5, 4 }, Opacity = 0.4,
            IsHitTestVisible = false, Margin = new Thickness(1),
        });
        zone.DragEnter += (_, _) => { zone.Background = Theme.AccentSoft; title.Text = "Release to upload"; };
        zone.DragLeave += (_, _) => { zone.Background = Theme.Card; title.Text = "Drop files here"; };
        zone.Drop += (_, e) => { zone.Background = Theme.Card; title.Text = "Drop files here"; DropFiles(e); };
        page.Add(dashed);

        _uploadList = new StackPanel();
        var listCard = new StackPanel();
        var head = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(Theme.Title("Uploads"));
        var clear = Theme.Pill("Clear finished", () => App.Current.Uploads.ClearFinished(), false);
        Grid.SetColumn(clear, 1);
        head.Children.Add(clear);
        listCard.Children.Add(head);
        listCard.Children.Add(_uploadList);
        var card = new Border { Background = Theme.Card, BorderBrush = Theme.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(18), Padding = new Thickness(18), Child = listCard, Tag = "jobs" };
        FillJobs();
        card.Visibility = App.Current.Uploads.Jobs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        page.Add(card);
        _jobsCard = card;
        return Pad(Ui.Page(page.ToArray()));
    }

    Border? _jobsCard;

    void JobsChanged(object? s, NotifyCollectionChangedEventArgs e)
    {
        if (_uploadList == null) return;
        FillJobs();
        if (_jobsCard != null) _jobsCard.Visibility = App.Current.Uploads.Jobs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void FillJobs()
    {
        if (_uploadList == null) return;
        _uploadList.Children.Clear();
        foreach (var j in App.Current.Uploads.Jobs) _uploadList.Children.Add(new JobRow(j));
    }

    // ---- Account -----------------------------------------------------------------------------------------------------------

    UIElement AccountTab()
    {
        var pw = new PasswordBox { Password = App.Current.ApiKey };
        var plain = new TextBox { Text = App.Current.ApiKey, Visibility = Visibility.Collapsed };
        var status = Theme.Label("", 12, Theme.Muted);
        void UpdateStatus()
        {
            bool has = !string.IsNullOrEmpty(App.Current.ApiKey);
            status.Text = has ? "Key saved, encrypted for your Windows account." : "No key set. Uploads are disabled.";
            status.Foreground = has ? Theme.Muted : Theme.Warning;
        }
        UpdateStatus();
        bool syncing = false;
        pw.PasswordChanged += (_, _) => { if (syncing) return; syncing = true; plain.Text = pw.Password; App.Current.ApiKey = pw.Password; UpdateStatus(); syncing = false; };
        plain.TextChanged += (_, _) => { if (syncing) return; syncing = true; pw.Password = plain.Text; App.Current.ApiKey = plain.Text; UpdateStatus(); syncing = false; };

        var show = Theme.Pill("Show", () => { }, false);
        show.Click += (_, _) =>
        {
            bool reveal = plain.Visibility != Visibility.Visible;
            plain.Visibility = reveal ? Visibility.Visible : Visibility.Collapsed;
            pw.Visibility = reveal ? Visibility.Collapsed : Visibility.Visible;
            show.Content = reveal ? "Hide" : "Show";
        };
        var paste = Theme.Pill("Paste", () => { if (Clipboard.ContainsText()) pw.Password = Clipboard.GetText().Trim(); }, false);
        paste.Margin = new Thickness(8, 0, 0, 0);
        show.Margin = new Thickness(8, 0, 0, 0);

        var fieldHost = new Grid();
        fieldHost.Children.Add(pw);
        fieldHost.Children.Add(plain);
        var keyRow = new Grid();
        keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        keyRow.Children.Add(fieldHost);
        Grid.SetColumn(show, 1); keyRow.Children.Add(show);
        Grid.SetColumn(paste, 2); keyRow.Children.Add(paste);

        var key = Theme.CardPanel(
            Theme.Label("API key", 17, Theme.Text, FontWeights.SemiBold),
            Theme.Sub("Poltergeist uploads to your Haunt Image Host with an API key."),
            keyRow, status);

        var steps = new List<UIElement> { Theme.Title("Create a key") };
        string[] lines =
        {
            "Open your Haunt dashboard and create an API key.", "Name it anything, e.g. “Poltergeist”.",
            "Under Image Host, enable Upload and Read.", "Paste the key above.",
        };
        for (int i = 0; i < lines.Length; i++) steps.Add(Theme.Label($"{i + 1}.  {lines[i]}", 13, Theme.Muted, null, true));
        var open = Theme.Pill("Open haunt.gg", () => App.OpenUrl("https://haunt.gg"));
        open.HorizontalAlignment = HorizontalAlignment.Left;
        steps.Add(open);
        return Pad(Ui.Page(key, Theme.CardPanel(steps.ToArray()), StatsBlock()));
    }

    // ---- General -----------------------------------------------------------------------------------------------------------

    UIElement GeneralTab()
    {
        var s = S;
        var after = Theme.CardPanel(
            Theme.Title("After upload"),
            Ui.Toggle("Copy to clipboard", "Copy the result when an upload finishes.", s.CopyLink, v => s.CopyLink = v),
            Ui.Toggle("Copy the image instead of the link", "Photos go on the clipboard as an image; videos still copy the link.", s.CopyImage, v => s.CopyImage = v),
            Ui.Row("Link format", "What gets copied when it's a link.",
                Ui.Combo(Enum.GetValues<LinkFormat>().Select(f => (f.Title(), f)), s.LinkFormat, v => s.LinkFormat = v, 230)),
            Ui.Toggle("Preview popup", "A small thumbnail with a Copy button in the corner.", s.ShowToast, v => s.ShowToast = v),
            Ui.Toggle("Open in browser", "Open the link when an upload finishes.", s.OpenInBrowser, v => s.OpenInBrowser = v));

        var notify = Theme.CardPanel(
            Theme.Title("Sounds and notifications"),
            SoundRow("Capture sound", "When a screenshot is taken.", s.CaptureSound, v => s.CaptureSound = v),
            SoundRow("Success sound", "When an upload finishes.", s.SuccessSound, v => s.SuccessSound = v),
            SoundRow("Error sound", "When something fails.", s.ErrorSound, v => s.ErrorSound = v),
            Ui.Toggle("Notify when an upload finishes", null, s.NotifyOnSuccess, v => s.NotifyOnSuccess = v),
            Ui.Toggle("Notify when something fails", null, s.NotifyOnError, v => s.NotifyOnError = v));

        var recent = Theme.CardPanel(
            Theme.Title("Recent uploads"),
            Ui.Row("Clear automatically", "The list of recent uploads in the tray menu.",
                Ui.Combo(new[] { ("Never", "never"), ("When Poltergeist quits", "quit"), ("After 1 day", "day"), ("After 7 days", "week") }, s.HistoryClear, v => s.HistoryClear = v, 210)),
            RightAligned(Theme.Pill("Clear now", () => { App.Current.History.Clear(); App.Current.Uploads.ClearFinished(); }, false)));

        var privacy = Theme.CardPanel(
            Theme.Title("Privacy"),
            Ui.Toggle("Strip photo metadata", "Removes location, camera and other EXIF data from JPEG and PNG before upload. GIFs, WebP, AVIF and videos are uploaded as-is.",
                s.StripMetadata, v => s.StripMetadata = v));

        var folderText = Theme.Label(Pretty(s.SaveFolder), 12, s.SaveScreenshots ? Theme.Text : Theme.Muted);
        folderText.TextTrimming = TextTrimming.CharacterEllipsis;
        folderText.VerticalAlignment = VerticalAlignment.Center;
        var shots = Theme.CardPanel(
            Theme.Title("Screenshots"),
            Ui.Toggle("Freeze and dim the screen while selecting", "Takes a still of the screen when you press the hotkey, so what you select is exactly what was there.", s.FreezeOnCapture, v => s.FreezeOnCapture = v),
            Ui.Toggle("Edit before uploading", $"Opens a quick editor for arrows, boxes, text and blur. {s.Binding(HotKeyAction.EditArea).Display} always opens it, whatever this is set to.", s.EditBeforeUpload, v => s.EditBeforeUpload = v),
            Ui.Toggle("Save a copy of screenshots", "Keep each screenshot in a folder on this PC. It's still uploaded and the link is still copied.", s.SaveScreenshots, v => { s.SaveScreenshots = v; folderText.Foreground = v ? Theme.Text : Theme.Muted; }),
            FolderRow(folderText, s.SaveFolder, v => { s.SaveFolder = v; folderText.Text = Pretty(v); }));

        var startup = Theme.CardPanel(
            Ui.Toggle("Launch at login", "Start Poltergeist when you sign in to Windows.", s.LaunchAtLogin, v => s.LaunchAtLogin = v),
            Ui.Row("Settings menu", "A sidebar down the left like Haunt's dashboard, or tabs across the top.",
                Ui.Combo(new[] { ("Sidebar", "side"), ("Top tabs", "top") }, s.SettingsLayout, v =>
                {
                    s.SettingsLayout = v;
                    Dispatcher.BeginInvoke(() => { BuildLayout(); SelectTab("General"); });
                }, 150)));

        // hotkeys
        var msg = Theme.Label("", 11, Theme.Warning);
        var hkHead = new Grid();
        hkHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hkHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hkHead.Children.Add(Theme.Title("Hotkeys"));
        var reset = Theme.Pill("Reset to defaults", () =>
        {
            foreach (var a in Enum.GetValues<HotKeyAction>()) s.SetBinding(a, a.Default());
            App.Current.RegisterHotkeys();
            Render();
        }, false);
        Grid.SetColumn(reset, 1);
        hkHead.Children.Add(reset);
        var hk = new List<UIElement> { hkHead, Theme.Sub("Click a shortcut, then press the new keys. Esc cancels, Delete clears it."), msg };
        foreach (var a in Enum.GetValues<HotKeyAction>())
            hk.Add(Ui.Row(a.Title(), null, new HotKeyButton(a, msg)));
        return Pad(Ui.Page(after, notify, recent, privacy, shots, startup, Theme.CardPanel(hk.ToArray())));
    }

    static UIElement RightAligned(UIElement e)
    {
        if (e is FrameworkElement fe) fe.HorizontalAlignment = HorizontalAlignment.Right;
        return e;
    }

    static string Pretty(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? "~" + path[home.Length..] : path;
    }

    static UIElement FolderRow(TextBlock label, string current, Action<string> set)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(label);
        var b = Theme.Pill("Choose folder…", () =>
        {
            var dlg = new OpenFolderDialog { Title = "Choose a folder", InitialDirectory = Directory.Exists(current) ? current : null };
            if (dlg.ShowDialog() == true) set(dlg.FolderName);
        });
        b.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(b, 1);
        g.Children.Add(b);
        return g;
    }

    // ---- Recordings --------------------------------------------------------------------------------------------------------

    static bool MicAllowed()
    {
        try
        {
            foreach (var path in new[]
            {
                @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone",
                @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone\NonPackaged",
            })
            {
                using var k = Registry.CurrentUser.OpenSubKey(path);
                if (k?.GetValue("Value") is string v && v.Equals("Deny", StringComparison.OrdinalIgnoreCase)) return false;
            }
        }
        catch { }
        return true;
    }

    static List<(string Name, string Id)> Microphones()
    {
        var list = new List<(string, string)>();
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)) list.Add((d.FriendlyName, d.ID));
        }
        catch { }
        return list.OrderBy(x => x.Item1).ToList();
    }

    UIElement RecordingsTab()
    {
        var s = S;
        var micOk = MicAllowed();
        var ff = Ffmpeg.Available;

        // tools & permissions
        var ffRow = Ui.Row("Video tools (ffmpeg)", "Needed for recording and the video editor. Downloaded once, stored in your user folder.",
            ff ? Ui.Chip("Ready", true) : (UIElement)Theme.Pill("Download", async () =>
            {
                if (await FfmpegSetup.OfferInstallAsync()) Render();
            }));
        var micRow = Ui.Row("Microphone access", micOk ? "Only used when “Record microphone” is on." : "Windows is blocking microphone access for desktop apps.",
            micOk ? Ui.Chip("Allowed", true) : (UIElement)Theme.Pill("Open Windows settings", () => App.OpenUrl("ms-settings:privacy-microphone")));
        var perms = Theme.CardPanel(
            Theme.Title("Setup"),
            Theme.Sub("Windows doesn't ask permission to capture the screen, so there's nothing to allow for that."),
            ffRow, micRow);

        var mics = Microphones();
        var micItems = new List<(string, string)> { ("System default", "") };
        micItems.AddRange(mics.Select(m => (m.Name, m.Id)));
        if (!string.IsNullOrEmpty(s.MicDeviceId) && mics.All(m => m.Id != s.MicDeviceId)) micItems.Add(("Unavailable device", s.MicDeviceId));
        var micPicker = Ui.Combo(micItems, s.MicDeviceId, v => s.MicDeviceId = v, 260);
        micPicker.IsEnabled = s.RecordMic;

        var audio = Theme.CardPanel(
            Theme.Title("Audio"),
            Ui.Toggle("Record desktop audio", "Everything your PC plays, whichever output you're using: speakers, headphones or Bluetooth.", s.RecordSystemAudio, v => s.RecordSystemAudio = v),
            Ui.Toggle("Record microphone", "Kept on its own track so you can cut it out in the editor. It pauses and resumes together with the recording.", s.RecordMic, v => { s.RecordMic = v; micPicker.IsEnabled = v; }),
            Ui.Row("Microphone", null, micPicker));

        var mb = new TextBlock();
        var info = Theme.Sub("");
        void UpdateInfo() => info.Text = $"About {s.Bitrate * 60 / 8} MB per minute. Haunt's per-file upload limit is 75 MB on Free, 150 MB on Premium and 250 MB on the Image Host plan, so long recordings at a high bitrate may be refused.";
        UpdateInfo();
        var quality = Theme.CardPanel(
            Theme.Title("Quality"),
            Ui.Row("Bitrate", "Higher looks sharper but makes bigger files.",
                Ui.Combo(new[] { 2, 4, 8, 12, 20, 35 }.Select(b => ($"{b} Mbps", b)), s.Bitrate, v => { s.Bitrate = v; UpdateInfo(); }, 140)),
            Ui.Row("Frame rate", "60 fps is smoother for games and scrolling.",
                Ui.Combo(new[] { 15, 24, 30, 60 }.Select(f => ($"{f} fps", f)), s.Fps, v => s.Fps = v, 140)),
            info);

        var editing = Theme.CardPanel(
            Theme.Title("Editing"),
            Ui.Toggle("Edit recordings before uploading", "After you stop, an editor opens: trim the ends, cut out sections, change speed, and add arrows, boxes, text or blur that can move over time. Plain trimming is instant; anything else re-encodes at your bitrate above.",
                s.EditRecordings, v => s.EditRecordings = v));

        var folderText = Theme.Label(Pretty(s.RecordFolder), 12, s.KeepRecordings ? Theme.Text : Theme.Muted);
        folderText.TextTrimming = TextTrimming.CharacterEllipsis;
        folderText.VerticalAlignment = VerticalAlignment.Center;
        var backup = Theme.CardPanel(
            Theme.Title("Backup"),
            Ui.Toggle("Keep a copy of recordings", "Saved to this folder as well as uploaded. Turn it off to delete the local file after uploading.", s.KeepRecordings, v => { s.KeepRecordings = v; folderText.Foreground = v ? Theme.Text : Theme.Muted; }),
            FolderRow(folderText, s.RecordFolder, v => { s.RecordFolder = v; folderText.Text = Pretty(v); }));

        var help = new List<UIElement> { Theme.Title("How it works") };
        foreach (var line in new[]
        {
            "Press a record hotkey (change them in General → Hotkeys). Area: drag over the region. Window: click the window. Full screen: starts straight away on the screen your mouse is on.",
            "A small control bar sits beside the recording with pause, mute-mic and stop. It's never part of the video.",
            "Stop saves and uploads. Discard throws the recording away. Press the hotkey again to stop and upload.",
            "Recordings are MP4 (H.264 video, AAC audio). Desktop audio and the mic are kept on separate tracks while you edit, and mixed into one for upload.",
        }) help.Add(Theme.Label("•  " + line, 12, Theme.Muted, null, true));
        return Pad(Ui.Page(perms, audio, quality, editing, backup, Theme.CardPanel(help.ToArray())));
    }

    // ---- Embed -------------------------------------------------------------------------------------------------------------

    int _embedMode;

    UIElement EmbedTab()
    {
        var e = S.Embed;
        void Touch() => S.MarkChanged();

        var banner = Ui.Banner("Embed settings are saved on this PC only. Haunt's public API docs don't expose an embed endpoint yet, so changes here won't appear on shared links until that's wired up.");

        var seg = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (name, idx) in new[] { ("OpenGraph", 0), ("Discord", 1) })
        {
            var rb = new RadioButton { Style = Theme.Style("Tab"), Content = name, GroupName = "embedmode", IsChecked = _embedMode == idx, Margin = new Thickness(0, 0, 6, 0) };
            var i = idx;
            rb.Checked += (_, _) => { if (_embedMode != i) { _embedMode = i; Render(); } };
            seg.Children.Add(rb);
        }

        var vars = new Grid();
        vars.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        vars.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var user = Chips("User", new[] { ("{user.id}", "User ID"), ("{user.name}", "Username"), ("{user.uploads}", "Total uploads"), ("{user.uploads.today}", "Uploads today"), ("{user.storage}", "Used storage") });
        var file = Chips("File", new[] { ("{file.id}", "File ID"), ("{file.name}", "File name"), ("{file.originalName}", "Original name"), ("{file.size}", "File size"), ("{file.uploadAt}", "Upload date"), ("{file.url}", "Upload page"), ("{file.rawUrl}", "Direct link") });
        vars.Children.Add(user);
        Grid.SetColumn(file, 1); vars.Children.Add(file);
        var varsCard = Theme.CardPanel(Theme.Title("Available variables"), vars);

        var parts = new List<UIElement> { banner, seg, varsCard };
        parts.Add(_embedMode == 0 ? OpenGraphCards(e, Touch) : DiscordCards(e, Touch));
        return Pad(Ui.Page(parts.ToArray()));
    }

    static UIElement Chips(string title, (string Var, string Label)[] items)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        sp.Children.Add(Theme.Label(title, 12, Theme.Muted));
        foreach (var (v, l) in items)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            row.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)), CornerRadius = new CornerRadius(5), Padding = new Thickness(7, 2, 7, 2),
                Child = new TextBlock { Text = v, FontFamily = Theme.Mono, FontSize = 12, Foreground = Theme.Text },
            });
            var label = Theme.Label(l, 12, Theme.Muted);
            label.Margin = new Thickness(8, 0, 0, 0);
            label.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(label);
            sp.Children.Add(row);
        }
        return sp;
    }

    static Grid TwoCols(UIElement a, UIElement b)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(a);
        Grid.SetColumn(b, 2); g.Children.Add(b);
        return g;
    }

    static UIElement Area(string label, string value, bool mono, int height, Action<string> set)
    {
        var sp = new StackPanel();
        var l = Theme.Label(label, 13, Theme.Text, FontWeights.Medium);
        l.Margin = new Thickness(0, 0, 0, 6);
        sp.Children.Add(l);
        var tb = new TextBox { Style = Theme.Style("FieldArea"), Text = value, Height = height, Padding = new Thickness(12, 8, 12, 8) };
        if (mono) tb.FontFamily = Theme.Mono;
        tb.TextChanged += (_, _) => set(tb.Text);
        sp.Children.Add(tb);
        return sp;
    }

    static Brush Swatch(string hex) => ColorRgba.TryParse(hex, out var c) ? new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)) : Theme.Accent;

    static UIElement ColorField(EmbedConfig e, Action touch, string label)
    {
        var dot = new Ellipse { Width = 28, Height = 28, Fill = Swatch(e.Color), Margin = new Thickness(0, 0, 8, 0) };
        var box = Ui.Box(e.Color, "#f27db3", v => { e.Color = v; dot.Fill = Swatch(v); touch(); });
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(dot);
        Grid.SetColumn(box, 1); row.Children.Add(box);
        var sp = new StackPanel();
        var l = Theme.Label(label, 13, Theme.Text, FontWeights.Medium);
        l.Margin = new Thickness(0, 0, 0, 6);
        sp.Children.Add(l);
        sp.Children.Add(row);
        return sp;
    }

    static readonly (string Name, string Url)[] Providers = { ("Image Host", "https://i.haunt.gg"), ("Haunt", "https://haunt.gg") };

    UIElement OpenGraphCards(EmbedConfig e, Action touch)
    {
        bool custom = !Providers.Any(p => p.Name == e.ProviderName);
        var urlBox = Ui.Box(e.ProviderUrl, "https://…", v => { e.ProviderUrl = v; touch(); });
        urlBox.IsEnabled = custom;
        var nameBox = Ui.Box(e.ProviderName, "Provider name", v => { e.ProviderName = v; touch(); });
        nameBox.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        nameBox.Margin = new Thickness(0, 8, 0, 0);

        var items = Providers.Select(p => (p.Name, p.Name)).Append(("Custom", "Custom")).ToList();
        var picker = Ui.Combo(items, custom ? "Custom" : e.ProviderName, choice =>
        {
            var p = Providers.FirstOrDefault(x => x.Name == choice);
            if (p.Name != null) { e.ProviderName = p.Name; e.ProviderUrl = p.Url; }
            else { e.ProviderName = ""; e.ProviderUrl = ""; }
            touch();
            Render();
        }, 220);
        picker.HorizontalAlignment = HorizontalAlignment.Stretch;
        var provCol = new StackPanel();
        provCol.Children.Add(Theme.Label("Provider", 13, Theme.Text, FontWeights.Medium));
        ((TextBlock)provCol.Children[0]).Margin = new Thickness(0, 0, 0, 6);
        provCol.Children.Add(picker);
        provCol.Children.Add(nameBox);
        var urlCol = new StackPanel();
        urlCol.Children.Add(Theme.Label("Provider URL", 13, Theme.Text, FontWeights.Medium));
        ((TextBlock)urlCol.Children[0]).Margin = new Thickness(0, 0, 0, 6);
        urlCol.Children.Add(urlBox);

        var timestamp = Ui.Toggle("Show timestamp", "Append an upload timestamp to the embed.", e.ShowTimestamp, v => { e.ShowTimestamp = v; touch(); });

        var form = Theme.CardPanel(
            TwoCols(Ui.Field("Author name", e.AuthorName, "Optional", v => { e.AuthorName = v; touch(); }), Ui.Field("Author URL", e.AuthorUrl, "Optional", v => { e.AuthorUrl = v; touch(); })),
            TwoCols(provCol, urlCol),
            TwoCols(Ui.Field("Embed title", e.Title, "{file.name}", v => { e.Title = v; touch(); }), ColorField(e, touch, "Embed color")),
            Area("Embed description", e.Description, false, 72, v => { e.Description = v; touch(); }),
            timestamp);

        // preview
        var stack = new StackPanel { Margin = new Thickness(12) };
        if (!string.IsNullOrEmpty(e.AuthorName)) stack.Children.Add(Theme.Label(Tpl.Render(e.AuthorName), 12, Theme.Text, FontWeights.SemiBold));
        var title = Theme.Label(Tpl.Render(e.Title), 15, new SolidColorBrush(Color.FromRgb(0x00, 0xa8, 0xfc)), FontWeights.SemiBold, true);
        title.Margin = new Thickness(0, 6, 0, 0);
        stack.Children.Add(title);
        var desc = Theme.Label(Tpl.Render(e.Description), 13, new SolidColorBrush(Color.FromArgb(0xd9, 0xf2, 0xf2, 0xf2)), null, true);
        desc.Margin = new Thickness(0, 6, 0, 0);
        stack.Children.Add(desc);
        stack.Children.Add(Placeholder(""));
        if (e.ShowTimestamp) { var ts = Theme.Label(Tpl.Sample["file.uploadAt"], 11, Theme.Muted); ts.Margin = new Thickness(0, 8, 0, 0); stack.Children.Add(ts); }
        return Ui.Page(form, PreviewCard(e.Color, true, stack));
    }

    static UIElement Placeholder(string glyph) => new Border
    {
        Height = 120, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 10, 0, 0), Background = new SolidColorBrush(Color.FromArgb(15, 255, 255, 255)),
        Child = new TextBlock { Text = glyph, FontFamily = Theme.IconFont, FontSize = 24, Foreground = Theme.Muted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
    };

    static UIElement PreviewCard(string colorHex, bool accent, UIElement content)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (accent) row.Children.Add(new Border { Width = 4, Background = Swatch(colorHex) });
        Grid.SetColumn((FrameworkElement)content, 1);
        row.Children.Add(content);
        var discord = new Border { Background = new SolidColorBrush(Color.FromRgb(0x2b, 0x2d, 0x31)), CornerRadius = new CornerRadius(6), Child = row, ClipToBounds = true };
        return Theme.CardPanel(Theme.Title("Preview"), discord);
    }

    UIElement DiscordCards(EmbedConfig e, Action touch)
    {
        var colorField = ColorField(e, touch, "Accent color");
        colorField.Visibility = e.DiscordAccent ? Visibility.Visible : Visibility.Collapsed;
        var accent = Ui.Toggle("Accent color", "A colored bar along the side of the embed.", e.DiscordAccent, v => { e.DiscordAccent = v; touch(); colorField.Visibility = v ? Visibility.Visible : Visibility.Collapsed; });
        var spoiler = Ui.Toggle("Hide behind a spoiler", "Readers have to click the embed to reveal it.", e.DiscordSpoiler, v => { e.DiscordSpoiler = v; touch(); });
        var look = Theme.CardPanel(accent, colorField, spoiler);

        var text = Theme.CardPanel(
            Area("Text block", e.DiscordText, true, 84, v => { e.DiscordText = v; touch(); }),
            Theme.Sub("Discord markdown works here: headings, **bold**, *italic*, lists, links, code and -# small text."));

        var buttons = Theme.CardPanel(
            Theme.Label("Link buttons", 13, Theme.Text, FontWeights.Medium),
            TwoCols(Ui.Field("Button 1 label", e.Button1Label, "Open", v => { e.Button1Label = v; touch(); }), Ui.Field("Button 1 link", e.Button1Link, "{file.url}", v => { e.Button1Link = v; touch(); })),
            TwoCols(Ui.Field("Button 2 label", e.Button2Label, "Original file", v => { e.Button2Label = v; touch(); }), Ui.Field("Button 2 link", e.Button2Link, "{file.rawUrl}", v => { e.Button2Link = v; touch(); })));

        var stack = new StackPanel { Margin = new Thickness(12) };
        stack.Children.Add(Theme.Label(Tpl.Render(e.DiscordText), 13, Theme.Text, null, true));
        stack.Children.Add(Placeholder(e.DiscordSpoiler ? "" : ""));
        var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var label in new[] { e.Button1Label, e.Button2Label })
            btns.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 8, 0),
                Child = Theme.Label(label + " ↗", 12, Theme.Text, FontWeights.Medium),
            });
        stack.Children.Add(btns);
        return Ui.Page(look, text, buttons, PreviewCard(e.Color, e.DiscordAccent, stack));
    }

    // ---- sounds (Windows' own, or any file you pick, like ShareX) --------------------------------------------------------

    UIElement SoundRow(string title, string sub, string current, Action<string> set)
    {
        const string CustomTag = "\u0001custom";
        var items = new List<(string, string)> { ("None", Sounds.None) };
        items.AddRange(Sounds.Names.Select(n => (n, n)));
        if (Sounds.IsCustom(current)) items.Add(("Your file: " + Path.GetFileName(current), current));
        items.Add(("Choose a file…", CustomTag));
        var combo = Ui.Combo(items, current, v =>
        {
            if (v == CustomTag)
            {
                var dlg = new OpenFileDialog { Title = "Choose a sound", Filter = "Sounds|*.wav;*.mp3;*.wma;*.m4a;*.aac|All files|*.*" };
                if (dlg.ShowDialog() == true) { set(dlg.FileName); Sounds.Play(dlg.FileName); }
                Dispatcher.BeginInvoke(Render);
                return;
            }
            set(v);
            Sounds.Play(v);
        }, 260);
        return Ui.Row(title, sub, combo);
    }

    // ---- Gallery -----------------------------------------------------------------------------------------------------------

    static readonly HttpClient Thumbs = MakeHttp();

    readonly List<Border> _tiles = new();
    readonly List<Border> _pics = new();
    string _galleryMode = "haunt";                  // "haunt" (everything on your image host) or "pc" (uploads made from this PC)
    List<RemoteUpload>? _remote;
    string? _remoteError;
    bool _remoteLoading;

    async void LoadRemote()
    {
        if (_remoteLoading) return;
        _remoteLoading = true; _remoteError = null;
        try { _remote = await new HauntClient().ListUploadsAsync(App.Current.ApiKey, 100, CancellationToken.None); }
        catch (Exception e) { _remoteError = e.Message; }
        _remoteLoading = false;
        if (IsLoaded && _tab == "Gallery") Render();
    }

    GalleryWeb? _gallery;
    bool _webFailed;
    string? _webFailMessage;

    void DisposeGallery() { _gallery?.Dispose(); _gallery = null; }

    List<WebEntry> GalleryEntries()
    {
        var fmt = App.Current.Settings.LinkFormat;
        bool Video(string n) => new[] { ".mp4", ".webm", ".mov", ".mkv" }.Contains(Path.GetExtension(n).ToLowerInvariant());
        if (_galleryMode == "pc")
            return App.Current.History.Items.Select(h => new WebEntry(h.Name, h.Url, LinkFormat.Direct.Format(h.Url, h.Name), h.Date.ToString("MMM d, h:mm tt"), Video(h.Name), fmt.Format(h.Url, h.Name))).ToList();
        return (_remote ?? new List<RemoteUpload>()).Select(u => new WebEntry(u.Name, u.Url, LinkFormat.Direct.Format(u.Url, u.Name),
            u.Created is { } d ? d.ToString("MMM d, h:mm tt") : "", u.IsVideo, fmt.Format(u.Url, u.Name))).ToList();
    }

    UIElement GalleryTab()
    {
        // header: which gallery, refresh; below it: the gallery itself (filling the rest of the window)
        var page = new Grid();
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var head = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var seg = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, mode) in new[] { ("Haunt gallery", "haunt"), ("This PC", "pc") })
        {
            var rb = new RadioButton { Style = Theme.Style("Tab"), Content = label, GroupName = "gallerymode", IsChecked = _galleryMode == mode, Margin = new Thickness(0, 0, 6, 0) };
            var m = mode;
            rb.Checked += (_, _) => { if (_galleryMode != m) { _galleryMode = m; Render(); } };
            seg.Children.Add(rb);
        }
        head.Children.Add(seg);
        UIElement refresh = _galleryMode == "haunt"
            ? Theme.Pill("Refresh", () => { _remote = null; _remoteError = null; Render(); }, false)
            : Theme.Pill("Clear list", () => { App.Current.History.Clear(); Render(); }, false);
        Grid.SetColumn(refresh, 1);
        head.Children.Add(refresh);
        page.Children.Add(head);

        void Body(UIElement e) { Grid.SetRow(e, 1); page.Children.Add(e); }

        if (_galleryMode == "haunt")
        {
            if (string.IsNullOrEmpty(App.Current.ApiKey)) { Body(Ui.Banner("Add your API key in the Account tab to see your Haunt gallery.")); return page; }
            if (_remote == null && _remoteError == null)
            {
                LoadRemote();
                Body(Theme.Label("Loading your gallery…", 13, Theme.Muted));
                return page;
            }
            if (_remoteError != null)
            {
                var err = new StackPanel();
                err.Children.Add(Ui.Banner(_remoteError));
                var retry = Theme.Pill("Try again", () => { _remote = null; _remoteError = null; Render(); });
                retry.HorizontalAlignment = HorizontalAlignment.Left; retry.Margin = new Thickness(0, 12, 0, 0);
                err.Children.Add(retry);
                Body(err);
                return page;
            }
        }

        var entries = GalleryEntries();
        if (entries.Count == 0)
        {
            Body(Theme.Sub(_galleryMode == "haunt" ? "Nothing on your image host yet." : "Uploads you make from this PC show up here."));
            return page;
        }

        if (!_webFailed)
        {
            _gallery = new GalleryWeb(entries, text => App.Current.CopyText(text), url => App.OpenUrl(url),
                msg => Dispatcher.BeginInvoke(() => { _webFailed = true; _webFailMessage = msg; if (IsLoaded && _tab == "Gallery") Render(); }));
            Body(_gallery);
            return page;
        }

        // Edge's browser component isn't available: plain tiles (pictures may not load, the host only talks to browsers)
        var fallback = new StackPanel();
        fallback.Children.Add(Ui.Banner("Previews need Microsoft's WebView2 (it comes with Windows 11 and Edge). " + (_webFailMessage ?? "")));
        var wrap = new WrapPanel { Margin = new Thickness(0, 14, -12, 0) };
        foreach (var e in entries)
        {
            var entry = e;
            wrap.Children.Add(Tile(entry.Name, DateTime.MinValue, entry.Direct, entry.Video, entry.Url, entry.Direct, () => App.Current.CopyText(entry.CopyText)));
        }
        fallback.Children.Add(wrap);
        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Body(fallback);
        Dispatcher.BeginInvoke(FitTiles, System.Windows.Threading.DispatcherPriority.Loaded);
        return page;
    }

    UIElement Tile(string name, DateTime when, string thumbUrl, bool video, string openUrl, string directUrl, Action copy)
    {
        var glyph = new TextBlock { Text = video ? "\uE714" : "\uEB9F", FontFamily = Theme.IconFont, FontSize = 26, Foreground = Theme.Muted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var img = new System.Windows.Controls.Image { Stretch = Stretch.UniformToFill };
        var pic = new Border { Height = 108, Background = new SolidColorBrush(Color.FromArgb(15, 255, 255, 255)), ClipToBounds = true, CornerRadius = new CornerRadius(10), Cursor = Cursors.Hand };
        var inner = new Grid();
        inner.Children.Add(glyph); inner.Children.Add(img);
        pic.Child = inner;
        pic.MouseLeftButtonUp += (_, _) => new MediaPreviewWindow(name, directUrl, video, copy, openUrl) { Owner = this }.Show();
        pic.ToolTip = "Click for a bigger preview";
        _ = LoadThumb(thumbUrl, img, glyph);

        var title = Theme.Label(name, 12, Theme.Text, FontWeights.Medium);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.Margin = new Thickness(2, 8, 2, 0);
        var date = Theme.Label(when == DateTime.MinValue ? " " : when.ToString("MMM d, h:mm tt"), 11, Theme.Muted);
        date.Margin = new Thickness(2, 1, 2, 8);
        var copyBtn = Theme.Pill("Copy link", copy);
        copyBtn.Padding = new Thickness(10, 4, 10, 4); copyBtn.FontSize = 11.5;
        var openBtn = Theme.Pill("Open", () => App.OpenUrl(openUrl), false);
        openBtn.Padding = new Thickness(10, 4, 10, 4); openBtn.FontSize = 11.5; openBtn.Margin = new Thickness(6, 0, 0, 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(copyBtn); buttons.Children.Add(openBtn);

        var stack = new StackPanel();
        stack.Children.Add(pic); stack.Children.Add(title); stack.Children.Add(date); stack.Children.Add(buttons);
        var tile = new Border
        {
            Width = 176, Margin = new Thickness(0, 0, 12, 12), Padding = new Thickness(10), Background = Theme.Card,
            BorderBrush = Theme.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16), Child = stack,
        };
        _tiles.Add(tile); _pics.Add(pic);
        return tile;
    }

    /// <summary>Spreads the tiles over the whole width of the window, however wide it is.</summary>
    void FitTiles()
    {
        if (_tiles.Count == 0) return;
        double w = _scroll.ViewportWidth;
        if (w < 100) return;
        int cols = Math.Max(1, (int)((w + 12) / (176 + 12)));
        double tileW = Math.Floor((w + 12) / cols - 12);
        foreach (var t in _tiles) t.Width = tileW;
        foreach (var p in _pics) p.Height = Math.Max(90, (tileW - 20) * 0.62);
    }

    static readonly SemaphoreSlim VideoSlots = new(2);          // a couple of video frames at a time

    static HttpClient MakeHttp()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        c.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "image/avif,image/webp,image/png,image/*,*/*;q=0.8");
        return c;
    }

    static bool IsVideoUrl(string url)
    {
        try { return new[] { ".mp4", ".webm", ".mov", ".mkv" }.Contains(Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant()); }
        catch { return false; }
    }

    /// <summary>A small preview: the picture itself, or one frame from a video (needs ffmpeg).</summary>
    async Task LoadThumb(string url, System.Windows.Controls.Image img, TextBlock glyph)
    {
        BitmapImage? shown = null;
        try
        {
            byte[]? bytes = null;
            if (IsVideoUrl(url)) bytes = await VideoFrame(url);
            else
            {
                try { bytes = await Thumbs.GetByteArrayAsync(url); } catch { }
                if (bytes == null) shown = await ViaWindows(url, 360);       // Windows' own downloader, in case the host only likes browsers
            }
            if (shown == null && bytes != null)
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.StreamSource = new MemoryStream(bytes);
                bi.DecodePixelWidth = 360;
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.EndInit();
                bi.Freeze();
                shown = bi;
            }
        }
        catch { }
        if (shown != null && IsLoaded) { img.Source = shown; glyph.Visibility = Visibility.Collapsed; }
    }

    static async Task<BitmapImage?> ViaWindows(string url, int width)
    {
        try
        {
            var bi = new BitmapImage();
            var done = new TaskCompletionSource<bool>();
            bi.BeginInit();
            bi.UriSource = new Uri(url);
            bi.DecodePixelWidth = width;
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.EndInit();
            if (!bi.IsDownloading) { bi.Freeze(); return bi; }
            bi.DownloadCompleted += (_, _) => done.TrySetResult(true);
            bi.DownloadFailed += (_, _) => done.TrySetResult(false);
            if (await Task.WhenAny(done.Task, Task.Delay(15000)) != done.Task || !done.Task.Result) return null;
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    static async Task<byte[]?> VideoFrame(string url)
    {
        if (!Ffmpeg.Available) return null;
        await VideoSlots.WaitAsync();
        try
        {
            using var tool = ToolProcess.Start(Ffmpeg.Require(), new[]
            {
                "-v", "error", "-nostdin", "-user_agent", "Mozilla/5.0", "-ss", "1", "-i", url, "-frames:v", "1",
                "-vf", "scale=360:-2", "-f", "image2pipe", "-c:v", "png", "-",
            });
            using var ms = new MemoryStream();
            var copy = tool.Output.CopyToAsync(ms);
            if (await Task.WhenAny(copy, Task.Delay(20000)) != copy) return null;
            return ms.Length > 0 ? ms.ToArray() : null;
        }
        catch { return null; }
        finally { VideoSlots.Release(); }
    }

    // ---- Effects -----------------------------------------------------------------------------------------------------------

    UIElement SliderRow(string title, string? sub, double min, double max, double value, Action<double> set, string unit = "")
    {
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, Width = 170, SmallChange = 1, LargeChange = 4 };
        var label = Theme.Label($"{(int)value}{unit}", 12, Theme.Muted);
        label.Width = 44; label.TextAlignment = TextAlignment.Right; label.VerticalAlignment = VerticalAlignment.Center;
        slider.ValueChanged += (_, e) => { var v = Math.Round(e.NewValue); label.Text = $"{(int)v}{unit}"; set(v); };
        var box = new StackPanel { Orientation = Orientation.Horizontal };
        box.Children.Add(slider); box.Children.Add(label);
        return Ui.Row(title, sub, box);
    }

    UIElement EffectsTab()
    {
        var s = S;
        var preview = new System.Windows.Controls.Image { Stretch = Stretch.Uniform, MaxHeight = 220, HorizontalAlignment = HorizontalAlignment.Center };
        void Refresh()
        {
            using var sample = SampleShot();
            if (s.FxOn && ImageEffects.Wanted(s))
            {
                using var fx = ImageEffects.Apply(sample, s);
                preview.Source = Imaging.ToBitmapSource(fx);
            }
            else preview.Source = Imaging.ToBitmapSource(sample);
        }
        Action<Action> change = a => { a(); Refresh(); };

        var colorBox = Ui.Box(s.FxBorderColor, "#f27db3", v => change(() => s.FxBorderColor = v));
        colorBox.Width = 110;

        var master = Theme.CardPanel(
            Theme.Title("Image effects"),
            Theme.Sub("Touch-ups added to every screenshot you take, before it's edited or uploaded."),
            Ui.Toggle("Use image effects", null, s.FxOn, v => change(() => s.FxOn = v)));
        var look = Theme.CardPanel(
            Ui.Toggle("Rounded corners", null, s.FxRound, v => change(() => s.FxRound = v)),
            SliderRow("Corner size", null, 4, 60, s.FxRadius, v => change(() => s.FxRadius = (int)v), " px"),
            SliderRow("Border", "0 for none", 0, 16, s.FxBorder, v => change(() => s.FxBorder = (int)v), " px"),
            Ui.Row("Border colour", null, colorBox),
            Ui.Toggle("Drop shadow", "Adds a soft transparent margin around the shot.", s.FxShadow, v => change(() => s.FxShadow = v)),
            SliderRow("Shadow size", null, 6, 50, s.FxShadowSize, v => change(() => s.FxShadowSize = (int)v), " px"));
        var prev = Theme.CardPanel(Theme.Title("Preview"), new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x16)), CornerRadius = new CornerRadius(12), Padding = new Thickness(18), Child = preview,
        });
        Refresh();
        return Pad(Ui.Page(master, look, prev));
    }

    static SKBitmap SampleShot()
    {
        var bmp = new SKBitmap(420, 240, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var c = new SKCanvas(bmp);
        using (var bg = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(420, 240), new[] { new SKColor(40, 36, 56), new SKColor(18, 22, 34) }, null, SKShaderTileMode.Clamp) })
            c.DrawRect(0, 0, 420, 240, bg);
        using var panel = new SKPaint { Color = new SKColor(255, 255, 255, 22), IsAntialias = true };
        c.DrawRoundRect(24, 24, 180, 96, 10, 10, panel);
        c.DrawRoundRect(222, 24, 174, 44, 10, 10, panel);
        c.DrawRoundRect(222, 80, 174, 136, 10, 10, panel);
        c.DrawRoundRect(24, 134, 180, 82, 10, 10, panel);
        using var accent = new SKPaint { Color = new SKColor(0xf2, 0x7d, 0xb3), IsAntialias = true };
        c.DrawCircle(60, 70, 16, accent);
        return bmp;
    }

    // ---- Stats -------------------------------------------------------------------------------------------------------------

    UIElement StatsBlock()
    {
        var s = S;
        var t = TimeSpan.FromSeconds(s.TotalSeconds);
        string time = t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{t.Minutes}m";
        string data = s.BytesUploaded >= 1_000_000_000 ? $"{s.BytesUploaded / 1e9:0.0} GB" : $"{s.BytesUploaded / 1e6:0.#} MB";
        Border Stat(string label, string value)
        {
            var sp = new StackPanel();
            sp.Children.Add(Theme.Label(value, 26, Theme.Text, FontWeights.SemiBold));
            var l = Theme.Label(label, 12.5, Theme.Muted);
            l.Margin = new Thickness(0, 2, 0, 0);
            sp.Children.Add(l);
            return new Border { Background = Theme.Card, BorderBrush = Theme.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(18), Padding = new Thickness(20), Margin = new Thickness(0, 0, 12, 12), Child = sp, MinWidth = 190 };
        }
        var grid = new WrapPanel();
        foreach (var (l, v) in new[]
        {
            ("Time with Poltergeist open", time), ("Uploads", s.TotalUploads.ToString("N0")), ("Screenshots taken", s.TotalScreenshots.ToString("N0")),
            ("Recordings", s.TotalRecordings.ToString("N0")), ("Data uploaded", data), ("Using it since", s.FirstRun.ToString("MMM d, yyyy")),
        }) grid.Children.Add(Stat(l, v));
        var hint = Theme.Sub("Counted on this PC only. Time adds up while Poltergeist runs in the background.");
        hint.Margin = new Thickness(0, 4, 0, 0);
        var page = new StackPanel();
        var heading = Theme.Label("Stats", 17, Theme.Text, FontWeights.SemiBold);
        heading.Margin = new Thickness(0, 6, 0, 12);
        page.Children.Add(heading);
        page.Children.Add(grid);
        page.Children.Add(hint);
        return page;
    }

    // ---- Domain ------------------------------------------------------------------------------------------------------------

    UIElement DomainTab()
    {
        var open = Theme.Pill("Open dashboard", () => App.OpenUrl("https://haunt.gg"));
        open.HorizontalAlignment = HorizontalAlignment.Left;
        return Pad(Ui.Page(
            Ui.Banner("Choosing an upload domain isn't wired up yet. Haunt's public API docs don't list a domains endpoint, so for now pick your domain on the dashboard and Poltergeist will use it automatically."),
            Theme.CardPanel(
                Theme.Label("Upload domain", 17, Theme.Text, FontWeights.SemiBold),
                Theme.Sub("Links come back from Haunt already on your active domain (default i.haunt.gg)."),
                open)));
    }
}
