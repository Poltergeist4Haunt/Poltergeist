using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Poltergeist.Core;

namespace Poltergeist;

public partial class App : Application
{
    public static new App Current => (App)Application.Current;

    Mutex? _single;
    string _apiKey = "";
    bool _capturing;
    SettingsWindow? _settingsWindow;
    readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    readonly Dictionary<string, Task<byte[]?>> _thumbs = new();
    readonly Dictionary<string, byte[]> _clipImages = new();
    readonly Dictionary<string, long> _sizes = new();

    public AppSettings Settings { get; private set; } = new();
    public HistoryStore History { get; private set; } = null!;
    public UploadQueue Uploads { get; } = new();
    public HotKeyManager HotKeys { get; private set; } = null!;
    public TrayHost Tray { get; private set; } = null!;
    public ScreenRecorder? Recorder { get; set; }
    public string UploadStatus { get; private set; } = "";

    public string ApiKey
    {
        get => _apiKey;
        set { _apiKey = value.Trim(); ApiKeyStore.Save(_apiKey); }
    }

    // ---- start / quit ----------------------------------------------------------------------------------------------------

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _single = new Mutex(true, @"Local\gg.haunt.Poltergeist", out bool first);
        if (!first)
        {
            MessageBox.Show("Poltergeist is already running (look for the ghost in the tray). If you just downloaded a new version, quit the old one first: right-click the tray ghost, Quit, or end Poltergeist in Task Manager.",
                "Poltergeist", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, ex) =>
        {
            Log("UI exception: " + ex.Exception);
            ex.Handled = true;
            Notifier.Error("Poltergeist hit a problem", ex.Exception.Message);
        };
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log("Task exception: " + ex.Exception); ex.SetObserved(); };

        Settings = AppSettings.Load(Paths.Settings);
        _apiKey = ApiKeyStore.Load();
        History = new HistoryStore(Paths.History, 100) { Post = OnUi };
        History.Prune(Settings.HistoryClear);

        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Settings.Save(Paths.Settings); };
        Settings.PropertyChanged += (_, args) =>
        {
            _saveTimer.Stop(); _saveTimer.Start();
            if (args.PropertyName == nameof(AppSettings.LaunchAtLogin)) ApplyLaunchAtLogin();
        };

        Ffmpeg.FfmpegPath = FfmpegSetup.InstalledPath("ffmpeg");
        Ffmpeg.FfprobePath = FfmpegSetup.InstalledPath("ffprobe");

        Uploads.Post = OnUi;
        Uploads.ApiKey = () => _apiKey;
        Uploads.StripMetadata = () => Settings.StripMetadata;
        Uploads.Succeeded += OnUploadSucceeded;
        Uploads.Failed += OnUploadFailed;
        Uploads.StatusChanged += s => { UploadStatus = s; Tray?.SetStatus(s); };

        Notifier.Init((action, url) =>
        {
            if (string.IsNullOrEmpty(url)) return;
            if (action == "copy") CopyText(url);
            else OpenUrl(url);
        });

        if (Ffmpeg.Available) _ = Task.Run(async () => { try { await Encoders.PickH264Async(); } catch { } });     // so recording starts without that lookup
        Tray = new TrayHost();
        HotKeys = new HotKeyManager();
        RegisterHotkeys();

        // Hand unused memory back to Windows now and then, so the tray app sits small while idle.
        var usage = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        usage.Tick += (_, _) => Settings.TotalSeconds += 30;                 // time the app has been open, for the stats page
        usage.Start();
        var trim = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        trim.Tick += (_, _) => TrimMemory();
        trim.Start();
        _ = Task.Delay(4000).ContinueWith(_ => Dispatcher.BeginInvoke(TrimMemory));

        if (string.IsNullOrEmpty(_apiKey)) ShowSettings("Account");
        foreach (var f in e.Args.Where(File.Exists)) UploadFile(f);

        // A few seconds after launch, ask the website whether there's a newer version.
        _ = Task.Delay(4000).ContinueWith(_ => Dispatcher.BeginInvoke(() => { if (Recorder == null) _ = Updater.CheckAsync(manual: false); }));
    }

    void TrimMemory()
    {
        if (Recorder != null) return;
        try
        {
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            Native.EmptyWorkingSet(Process.GetCurrentProcess().Handle);
        }
        catch { }
    }

    public void Quit()
    {
        try { Recorder?.Abort(); } catch { }
        History.Prune(Settings.HistoryClear, quitting: true);
        Settings.Save(Paths.Settings);
        HotKeys?.Dispose();
        Tray?.Dispose();
        Notifier.Shutdown();
        try { _single?.ReleaseMutex(); } catch { }
        Shutdown();
    }

    // ---- hotkeys ---------------------------------------------------------------------------------------------------------

    public void RegisterHotkeys()
    {
        HotKeys.UnregisterAll();
        var failed = new List<string>();
        foreach (HotKeyAction a in Enum.GetValues<HotKeyAction>())
        {
            var action = a;
            var b = Settings.Binding(a);
            if (!HotKeys.Register(b, () => Perform(action))) failed.Add($"{b.Display} ({a.Title()})");
        }
        if (failed.Count > 0) Notifier.Info("Some shortcuts are taken", "Another app already uses: " + string.Join(", ", failed) + ". Change them in Settings → General.");
    }

    void Perform(HotKeyAction a)
    {
        switch (a)
        {
            case HotKeyAction.Area: Capture(CaptureMode.Area); break;
            case HotKeyAction.Fullscreen: Capture(CaptureMode.Fullscreen); break;
            case HotKeyAction.Window: Capture(CaptureMode.Window); break;
            case HotKeyAction.EditArea: Capture(CaptureMode.Area, edit: true); break;
            case HotKeyAction.RecordArea: StartRecording(CaptureMode.Area); break;
            case HotKeyAction.RecordFullscreen: StartRecording(CaptureMode.Fullscreen); break;
            case HotKeyAction.RecordWindow: StartRecording(CaptureMode.Window); break;
        }
    }

    // ---- screenshots -----------------------------------------------------------------------------------------------------

    public async void Capture(CaptureMode mode, bool edit = false)
    {
        if (_capturing || Recorder != null) return;
        _capturing = true;
        try
        {
            var (glyph, what, hint) = Describe(mode, edit || Settings.EditBeforeUpload);
            var r = await CaptureService.RunAsync(mode, Settings.FreezeOnCapture, hint, glyph);
            if (r.Cancelled) return;
            if (mode == CaptureMode.Fullscreen && r.File != null) ActionBadge.Show(glyph, what, 1500);       // no overlay for this one, so say it afterwards
            if (r.Error != null || r.File == null) { ReportProblem("Screenshot", r.Error ?? "Nothing was captured."); return; }

            Sounds.Play(Settings.CaptureSound);
            Settings.TotalScreenshots++;
            ImageEffects.ApplyToFile(r.File, Settings);
            if (Settings.SaveScreenshots) SaveCopy(r.File, Settings.SaveFolder);
            if (edit || Settings.EditBeforeUpload) OpenImageEditor(r.File);
            else UploadFile(r.File, deleteAfter: true);
        }
        finally { _capturing = false; }
    }

    /// <summary>What the user is doing, in words and an icon (for the on-screen badge and the selection hint).</summary>
    static (string Glyph, string What, string Hint) Describe(CaptureMode mode, bool edit) => mode switch
    {
        CaptureMode.Area when edit => (ActionBadge.Edit, "Screenshot of an area, then editing", "Screenshot + editor  ·  drag to select  ·  Esc to cancel"),
        CaptureMode.Area => (ActionBadge.Camera, "Taking a screenshot of an area", "Taking a screenshot  ·  drag to select  ·  Esc to cancel"),
        CaptureMode.Window when edit => (ActionBadge.Edit, "Screenshot of a window, then editing", "Screenshot + editor  ·  click a window  ·  Esc to cancel"),
        CaptureMode.Window => (ActionBadge.Window_, "Taking a screenshot of a window", "Taking a screenshot  ·  click a window  ·  Esc to cancel"),
        _ => (ActionBadge.Screen, edit ? "Full-screen screenshot, then editing" : "Took a full-screen screenshot", ""),
    };

    public void OpenImageEditor(string file)
    {
        try
        {
            var w = new ImageEditorWindow(file, deleteWhenDone: true);
            w.Show();
            w.Activate();
        }
        catch (Exception e)
        {
            ReportProblem("Editor", "Couldn't open the editor: " + e.Message);
            UploadFile(file, deleteAfter: true);
        }
    }

    static void SaveCopy(string file, string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var dest = Path.Combine(folder, Path.GetFileName(file));
            File.Copy(file, dest, true);
        }
        catch { }
    }

    // ---- uploading -------------------------------------------------------------------------------------------------------

    public void UploadFile(string file, bool deleteAfter = false)
    {
        if (!File.Exists(file)) { Uploads.Report(Path.GetFileName(file), "File not found."); return; }
        _thumbs[file] = Task.Run(() => MakeThumbnail(file));
        var ext = Path.GetExtension(file).ToLowerInvariant();
        if (Settings.CopyLink && Settings.CopyImage && MediaClip.ImageExtensions.Contains(ext))
        {
            try { if (new FileInfo(file).Length < 40_000_000) _clipImages[file] = File.ReadAllBytes(file); } catch { }
        }
        try { _sizes[file] = new FileInfo(file).Length; } catch { }
        Uploads.Enqueue(file, deleteAfter);
    }

    public void UploadFiles(IEnumerable<string> files) { foreach (var f in files) UploadFile(f); }

    public void ChooseAndUploadFiles()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose files to upload", Multiselect = true,
            Filter = "Images and videos|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.avif;*.mp4;*.webm;*.mov;*.mkv|All files|*.*",
        };
        if (dlg.ShowDialog() == true) UploadFiles(dlg.FileNames);
    }

    public void UploadClipboardImage()
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                UploadFiles(Clipboard.GetFileDropList().Cast<string>().Where(File.Exists));
                return;
            }
            if (!Clipboard.ContainsImage()) { Notifier.Info("Nothing to upload", "Copy an image first."); return; }
            var src = Clipboard.GetImage();
            if (src == null) return;
            var file = Path.Combine(Paths.Temp, $"Clipboard {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png");
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(src));
            using (var fs = File.Create(file)) enc.Save(fs);
            UploadFile(file, deleteAfter: true);
        }
        catch (Exception e) { ReportProblem("Clipboard", e.Message); }
    }

    static byte[]? MakeThumbnail(string file)
    {
        var ext = Path.GetExtension(file).ToLowerInvariant();
        if (MediaClip.ImageExtensions.Contains(ext) || ext == ".gif") return Imaging.Thumbnail(file);
        if (MediaClip.VideoExtensions.Contains(ext) && Ffmpeg.Available)
        {
            try
            {
                var tmp = Path.Combine(Paths.Temp, "frame-" + Guid.NewGuid().ToString("N")[..8] + ".png");
                var r = Proc.RunAsync(Ffmpeg.Require(), new[] { "-v", "error", "-y", "-ss", "0.5", "-i", file, "-frames:v", "1", tmp }).GetAwaiter().GetResult();
                if (r.Ok && File.Exists(tmp))
                {
                    var t = Imaging.Thumbnail(tmp);
                    File.Delete(tmp);
                    return t;
                }
            }
            catch { }
        }
        return null;
    }

    async void OnUploadSucceeded(UploadJob job, UploadResult result)
    {
        var s = Settings;
        var name = job.Name;
        var url = result.Url;
        History.Add(url, name);
        Settings.TotalUploads++;
        if (job.File != null && _sizes.Remove(job.File, out var bytesSent)) Settings.BytesUploaded += bytesSent;

        byte[]? thumb = null;
        if (job.File != null && _thumbs.Remove(job.File, out var t)) { try { thumb = await t; } catch { } }
        var copyText = s.LinkFormat.Format(url, name);

        if (s.CopyLink)
        {
            if (job.File != null && _clipImages.Remove(job.File, out var bytes)) CopyImageBytes(bytes);
            else CopyText(copyText);
        }
        if (job.File != null) _clipImages.Remove(job.File);

        Sounds.Play(s.SuccessSound);
        if (s.OpenInBrowser) OpenUrl(url);
        if (s.ShowToast) PreviewToast.ShowFor(name, copyText, thumb);
        if (s.NotifyOnSuccess) Notifier.Success(name, url, thumb);
    }

    void OnUploadFailed(UploadJob job, string message)
    {
        if (job.File != null) { _thumbs.Remove(job.File); _clipImages.Remove(job.File); }
        Sounds.Play(Settings.ErrorSound);
        if (Settings.NotifyOnError) Notifier.Error("Upload failed", $"{job.Name}: {message}");
    }

    /// <summary>Something went wrong before an upload could even start: a red row in the list plus a notification.</summary>
    public void ReportProblem(string what, string message)
    {
        Uploads.Report(what, message);
        Sounds.Play(Settings.ErrorSound);
        if (Settings.NotifyOnError) Notifier.Error(what, message);
    }

    // ---- videos ----------------------------------------------------------------------------------------------------------

    /// <summary>Renders what the editor describes (or just trims / copies) and uploads the result; the source is deleted afterwards if asked.</summary>
    public async void ExportAndUpload(string source, VideoProject project, bool deleteSource, bool toClipboard = false)
    {
        var job = Uploads.BeginTask("Preparing " + Path.GetFileName(source));
        try
        {
            var prepared = await VideoExporter.PrepareAsync(source, project, Settings.Bitrate, p => Uploads.TaskProgress(job, p));
            Uploads.EndTask(job);
            var final = prepared ?? source;
            if (toClipboard)
            {
                var keep = Path.Combine(Paths.Temp, Path.GetFileName(final));
                if (!string.Equals(keep, final, StringComparison.OrdinalIgnoreCase)) File.Copy(final, keep, true);
                var list = new System.Collections.Specialized.StringCollection { keep };
                Clipboard.SetFileDropList(list);
                Notifier.Info("Copied to clipboard", "The video is on your clipboard. Paste it where you want it.");
                return;
            }
            bool temp = prepared != null;
            if (temp && deleteSource) { try { File.Delete(source); } catch { } }
            UploadFile(final, deleteAfter: temp || deleteSource);
        }
        catch (Exception e)
        {
            Uploads.EndTask(job, e is ExportException ? e.Message : "Couldn't prepare the video: " + e.Message);
            Sounds.Play(Settings.ErrorSound);
            if (Settings.NotifyOnError) Notifier.Error("Video failed", e.Message);
        }
    }

    // ---- recording -------------------------------------------------------------------------------------------------------

    public async void StartRecording(CaptureMode mode)
    {
        if (Recorder != null) { Recorder.RequestStop(); return; }
        if (_capturing) return;
        _capturing = true;
        try
        {
            if (!Ffmpeg.Available && !await FfmpegSetup.OfferInstallAsync()) return;
            string what = mode switch { CaptureMode.Area => "Recording an area", CaptureMode.Window => "Recording a window", _ => "Recording full screen" };
            System.Drawing.Rectangle region;
            if (mode == CaptureMode.Fullscreen) region = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).Bounds;
            else
            {
                var picked = await CaptureService.PickAsync(mode, freeze: true,
                    hint: mode == CaptureMode.Window ? "Recording a window  ·  click it  ·  Esc to cancel" : "Recording an area  ·  drag to select  ·  Esc to cancel",
                    glyph: ActionBadge.Record);
                if (picked == null) return;
                picked.Frozen?.Dispose();
                region = picked.Bounds;
            }
            await Task.Delay(50);                              // the overlay needs a moment to disappear
            var rec = new ScreenRecorder(region, Settings) { Label = what };
            if (mode == CaptureMode.Fullscreen) ActionBadge.Show(ActionBadge.Record, what, 2200, recording: true);
            Recorder = rec;
            Tray.Recording = true;
            rec.Finished += (file, error) => OnRecordingFinished(file, error, rec.EditRequested);
            await rec.StartAsync();
        }
        catch (Exception e)
        {
            Recorder = null;
            Tray.Recording = false;
            ReportProblem("Recording", e.Message);
        }
        finally { _capturing = false; }
    }

    async void OnRecordingFinished(string? file, string? error, bool edit)
    {
        Recorder = null;
        Tray.Recording = false;
        if (error != null) { ReportProblem("Recording", error); return; }
        if (file == null) return;                              // discarded
        Settings.TotalRecordings++;
        if (Settings.KeepRecordings)
        {
            var folder = Settings.RecordFolder;
            _ = Task.Run(() => { try { SaveCopy(file, folder); } catch { } });     // a big copy must not hold up the editor
        }
        if (edit || Settings.EditRecordings)
        {
            try { var w = new VideoEditorWindow(file, deleteWhenDone: true); w.Show(); w.Activate(); return; }
            catch (Exception e) { Log("Video editor: " + e); ReportProblem("Editor", "Couldn't open the video editor: " + e.Message); }
        }
        var made = await ProjectFactory.ForFileAsync(file);
        if (made == null) { UploadFile(file, deleteAfter: true); return; }
        ExportAndUpload(file, made.Value.Project, deleteSource: true);
    }

    // ---- windows ---------------------------------------------------------------------------------------------------------

    public void ShowSettings(string? tab = null)
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(tab);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        else if (tab != null) _settingsWindow.SelectTab(tab);
        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------------

    void OnUi(Action a)
    {
        if (Dispatcher.CheckAccess()) a(); else Dispatcher.BeginInvoke(a);
    }

    public void CopyText(string text) => Retry(() => Clipboard.SetText(text));

    public void CopyImageBytes(byte[] encoded)
    {
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit(); bi.StreamSource = new MemoryStream(encoded); bi.CacheOption = BitmapCacheOption.OnLoad; bi.EndInit(); bi.Freeze();
            Retry(() => Clipboard.SetImage(bi));
        }
        catch { }
    }

    public void CopyImageFile(string file)
    {
        try { CopyImageBytes(File.ReadAllBytes(file)); } catch { }
    }

    static void Retry(Action a)
    {
        for (int i = 0; i < 6; i++)
        {
            try { a(); return; }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(40); }       // another app has the clipboard open for a moment
        }
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    void ApplyLaunchAtLogin()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (k == null) return;
            if (Settings.LaunchAtLogin) k.SetValue("Poltergeist", $"\"{Environment.ProcessPath}\"");
            else k.DeleteValue("Poltergeist", false);
        }
        catch { }
    }

    static void Log(string text)
    {
        try
        {
            Directory.CreateDirectory(Paths.Data);
            File.AppendAllText(Path.Combine(Paths.Data, "log.txt"), $"[{DateTime.Now:s}] {text}\n");
        }
        catch { }
    }
}
