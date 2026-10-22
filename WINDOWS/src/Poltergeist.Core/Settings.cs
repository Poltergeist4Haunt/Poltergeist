using System.Text.Json;
using System.Text.Json.Serialization;

namespace Poltergeist.Core;

public enum HotKeyAction { Area, Fullscreen, Window, EditArea, RecordArea, RecordFullscreen, RecordWindow }

public static class HotKeyActionExtensions
{
    public static string Title(this HotKeyAction a) => a switch
    {
        HotKeyAction.Area => "Capture area",
        HotKeyAction.Fullscreen => "Capture full screen",
        HotKeyAction.Window => "Capture window",
        HotKeyAction.EditArea => "Capture area & edit",
        HotKeyAction.RecordArea => "Record area",
        HotKeyAction.RecordFullscreen => "Record full screen",
        HotKeyAction.RecordWindow => "Record window",
        _ => a.ToString(),
    };

    public static HotKeyBinding Default(this HotKeyAction a) => a switch
    {
        HotKeyAction.Area => new HotKeyBinding(0x34, HotKeyBinding.Ctrl | HotKeyBinding.Shift, "4"),
        HotKeyAction.Fullscreen => new HotKeyBinding(0x33, HotKeyBinding.Ctrl | HotKeyBinding.Shift, "3"),
        HotKeyAction.Window => new HotKeyBinding(0x35, HotKeyBinding.Ctrl | HotKeyBinding.Shift, "5"),
        HotKeyAction.EditArea => new HotKeyBinding(0x32, HotKeyBinding.Ctrl | HotKeyBinding.Shift, "2"),
        HotKeyAction.RecordArea => new HotKeyBinding(0x36, HotKeyBinding.Ctrl | HotKeyBinding.Shift, "6"),
        HotKeyAction.RecordFullscreen => new HotKeyBinding(0x37, HotKeyBinding.Ctrl | HotKeyBinding.Shift, "7"),
        HotKeyAction.RecordWindow => new HotKeyBinding(0x38, HotKeyBinding.Ctrl | HotKeyBinding.Shift, "8"),
        _ => HotKeyBinding.None,
    };
}

/// <summary>A global shortcut: a Windows virtual-key code plus modifier flags. <see cref="Vk"/> &lt; 0 means "no shortcut".</summary>
public readonly record struct HotKeyBinding(int Vk, int Modifiers, string Label)
{
    public const int Alt = 1, Ctrl = 2, Shift = 4, Win = 8;          // same values as Windows' MOD_* flags
    public static readonly HotKeyBinding None = new(-1, 0, "");
    [JsonIgnore] public bool IsSet => Vk >= 0;

    [JsonIgnore]
    public string Display
    {
        get
        {
            if (!IsSet) return "Not set";
            var parts = new List<string>();
            if ((Modifiers & Ctrl) != 0) parts.Add("Ctrl");
            if ((Modifiers & Alt) != 0) parts.Add("Alt");
            if ((Modifiers & Shift) != 0) parts.Add("Shift");
            if ((Modifiers & Win) != 0) parts.Add("Win");
            parts.Add(Label);
            return string.Join(" + ", parts);
        }
    }
}

/// <summary>Embed customisation, mirroring the Haunt dashboard's editor (kept on this PC; Haunt has no public endpoint for it yet).</summary>
public sealed class EmbedConfig
{
    public string AuthorName { get; set; } = "";
    public string AuthorUrl { get; set; } = "";
    public string ProviderName { get; set; } = "Image Host";
    public string ProviderUrl { get; set; } = "https://i.haunt.gg";
    public string Title { get; set; } = "{file.name}";
    public string Color { get; set; } = "#f27db3";
    public string Description { get; set; } = "{file.size}";
    public bool ShowTimestamp { get; set; } = true;
    public string DiscordText { get; set; } = "## {file.name}\n-# {file.size} • uploaded by {user.name}";
    public bool DiscordAccent { get; set; } = true;
    public bool DiscordSpoiler { get; set; }
    public string Button1Label { get; set; } = "Open";
    public string Button1Link { get; set; } = "{file.url}";
    public string Button2Label { get; set; } = "Original file";
    public string Button2Link { get; set; } = "{file.rawUrl}";
}

/// <summary>Everything the user can change. Saved to %APPDATA%\Poltergeist\settings.json (the API key is stored encrypted, separately).</summary>
public sealed class AppSettings : ObservableObject
{
    // ---- after upload
    bool _copyLink = true, _copyImage, _openInBrowser, _showToast = true;
    LinkFormat _linkFormat = LinkFormat.Url;
    public bool CopyLink { get => _copyLink; set => Set(ref _copyLink, value); }
    public bool CopyImage { get => _copyImage; set => Set(ref _copyImage, value); }
    public bool OpenInBrowser { get => _openInBrowser; set => Set(ref _openInBrowser, value); }
    public bool ShowToast { get => _showToast; set => Set(ref _showToast, value); }
    public LinkFormat LinkFormat { get => _linkFormat; set => Set(ref _linkFormat, value); }

    // ---- sounds and notifications ("None" = silent)
    string _successSound = "Windows Notify Messaging", _errorSound = "Windows Critical Stop";
    bool _notifyOnSuccess = true, _notifyOnError = true;
    public string SuccessSound { get => _successSound; set => Set(ref _successSound, value); }
    public string ErrorSound { get => _errorSound; set => Set(ref _errorSound, value); }
    public bool NotifyOnSuccess { get => _notifyOnSuccess; set => Set(ref _notifyOnSuccess, value); }
    public bool NotifyOnError { get => _notifyOnError; set => Set(ref _notifyOnError, value); }

    string _captureSound = "None";
    /// <summary>Played when a screenshot is taken. A name from Windows' sounds, "None", or the full path of your own file.</summary>
    public string CaptureSound { get => _captureSound; set => Set(ref _captureSound, value); }

    // ---- image effects, applied to every screenshot (like ShareX's image effects)
    bool _fxOn, _fxShadow, _fxRound;
    int _fxRadius = 14, _fxBorder, _fxShadowSize = 18;
    string _fxBorderColor = "#f27db3";
    public bool FxOn { get => _fxOn; set => Set(ref _fxOn, value); }
    public bool FxRound { get => _fxRound; set => Set(ref _fxRound, value); }
    public int FxRadius { get => _fxRadius; set => Set(ref _fxRadius, value); }
    public int FxBorder { get => _fxBorder; set => Set(ref _fxBorder, value); }
    public string FxBorderColor { get => _fxBorderColor; set => Set(ref _fxBorderColor, value); }
    public bool FxShadow { get => _fxShadow; set => Set(ref _fxShadow, value); }
    public int FxShadowSize { get => _fxShadowSize; set => Set(ref _fxShadowSize, value); }

    // ---- usage numbers for the stats page
    long _totalSeconds, _bytesUploaded;
    int _totalUploads, _totalScreenshots, _totalRecordings;
    DateTime _firstRun = DateTime.Now;
    public long TotalSeconds { get => _totalSeconds; set => Set(ref _totalSeconds, value); }
    public long BytesUploaded { get => _bytesUploaded; set => Set(ref _bytesUploaded, value); }
    public int TotalUploads { get => _totalUploads; set => Set(ref _totalUploads, value); }
    public int TotalScreenshots { get => _totalScreenshots; set => Set(ref _totalScreenshots, value); }
    public int TotalRecordings { get => _totalRecordings; set => Set(ref _totalRecordings, value); }
    public DateTime FirstRun { get => _firstRun; set => Set(ref _firstRun, value); }

    // ---- privacy, history
    bool _stripMetadata = true;
    string _historyClear = "never";
    public bool StripMetadata { get => _stripMetadata; set => Set(ref _stripMetadata, value); }
    /// <summary>"never", "quit", "day" or "week".</summary>
    public string HistoryClear { get => _historyClear; set => Set(ref _historyClear, value); }

    // ---- screenshots
    bool _freezeOnCapture = true, _saveScreenshots, _editBeforeUpload;
    string _saveFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Poltergeist");
    public bool FreezeOnCapture { get => _freezeOnCapture; set => Set(ref _freezeOnCapture, value); }
    public bool SaveScreenshots { get => _saveScreenshots; set => Set(ref _saveScreenshots, value); }
    public string SaveFolder { get => _saveFolder; set => Set(ref _saveFolder, value); }
    public bool EditBeforeUpload { get => _editBeforeUpload; set => Set(ref _editBeforeUpload, value); }

    // ---- recordings
    bool _recordSystemAudio, _recordMic, _keepRecordings = true, _editRecordings = true;
    string _micDeviceId = "";
    int _bitrate = 8, _fps = 30;
    string _recordFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Poltergeist");
    public bool RecordSystemAudio { get => _recordSystemAudio; set => Set(ref _recordSystemAudio, value); }
    public bool RecordMic { get => _recordMic; set => Set(ref _recordMic, value); }
    public string MicDeviceId { get => _micDeviceId; set => Set(ref _micDeviceId, value); }
    public int Bitrate { get => _bitrate; set => Set(ref _bitrate, value); }
    public int Fps { get => _fps; set => Set(ref _fps, value); }
    public bool KeepRecordings { get => _keepRecordings; set => Set(ref _keepRecordings, value); }
    public string RecordFolder { get => _recordFolder; set => Set(ref _recordFolder, value); }
    /// <summary>Open the video editor after a recording stops.</summary>
    public bool EditRecordings { get => _editRecordings; set => Set(ref _editRecordings, value); }

    // ---- general
    bool _launchAtLogin;
    public bool LaunchAtLogin { get => _launchAtLogin; set => Set(ref _launchAtLogin, value); }

    public Dictionary<HotKeyAction, HotKeyBinding> Hotkeys { get; set; } = new();
    public EmbedConfig Embed { get; set; } = new();

    public HotKeyBinding Binding(HotKeyAction a) => Hotkeys.TryGetValue(a, out var b) ? b : a.Default();
    public void SetBinding(HotKeyAction a, HotKeyBinding b) { Hotkeys[a] = b; OnPropertyChanged(nameof(Hotkeys)); }

    /// <summary>For edits to nested objects (embed fields) that don't raise their own change notifications.</summary>
    public void MarkChanged() => OnPropertyChanged(nameof(Embed));

    // ---- persistence ------------------------------------------------------------------------------------------------

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        IncludeFields = false,
    };

    public static AppSettings Load(string file)
    {
        try
        {
            if (File.Exists(file))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file), Json) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save(string file)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, file, true);
        }
        catch { }
    }
}
