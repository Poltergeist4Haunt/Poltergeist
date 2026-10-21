using System.IO.Compression;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Poltergeist.Core;

namespace Poltergeist;

/// <summary>
/// Recording and the video editor use ffmpeg. It isn't bundled (licensing, size), so it is downloaded once into
/// %APPDATA%\Poltergeist\tools, or picked up from next to the app / PATH if already there.
/// </summary>
static class FfmpegSetup
{
    static readonly string[] Sources =
    {
        "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip",
        "https://github.com/BtbN/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-gpl.zip",
    };

    public static string? InstalledPath(string name)
    {
        var f = Path.Combine(Paths.Tools, name + ".exe");
        return File.Exists(f) ? f : null;
    }

    public static async Task InstallAsync(IProgress<double> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Paths.Tools);
        var zip = Path.Combine(Paths.Temp, "ffmpeg-download.zip");
        Exception? last = null;
        foreach (var src in Sources)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
                using var resp = await http.GetAsync(src, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? 0;
                await using (var net = await resp.Content.ReadAsStreamAsync(ct))
                await using (var fs = File.Create(zip))
                {
                    var buf = new byte[1 << 17];
                    long got = 0;
                    int n;
                    while ((n = await net.ReadAsync(buf, ct)) > 0)
                    {
                        await fs.WriteAsync(buf.AsMemory(0, n), ct);
                        got += n;
                        if (total > 0) progress.Report(Math.Min(0.95, got / (double)total * 0.95));
                    }
                }
                using (var archive = ZipFile.OpenRead(zip))
                {
                    foreach (var name in new[] { "ffmpeg.exe", "ffprobe.exe" })
                    {
                        var entry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith("/bin/" + name, StringComparison.OrdinalIgnoreCase))
                                    ?? throw new InvalidDataException(name + " wasn't in the download.");
                        entry.ExtractToFile(Path.Combine(Paths.Tools, name), true);
                    }
                }
                try { File.Delete(zip); } catch { }
                Ffmpeg.FfmpegPath = InstalledPath("ffmpeg");
                Ffmpeg.FfprobePath = InstalledPath("ffprobe");
                progress.Report(1);
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { last = e; }
        }
        throw new InvalidOperationException("Couldn't download ffmpeg. " + (last?.Message ?? ""));
    }

    /// <summary>Asks the user to download ffmpeg; true if it is ready afterwards.</summary>
    public static async Task<bool> OfferInstallAsync()
    {
        var dlg = new FfmpegDialog();
        dlg.Show();
        dlg.Activate();
        return await dlg.Result.Task;
    }
}

/// <summary>"Poltergeist needs ffmpeg" with a Download button and progress.</summary>
sealed class FfmpegDialog : ChromeWindow
{
    public TaskCompletionSource<bool> Result { get; } = new();
    readonly ProgressBar _bar = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 14, 0, 0) };
    readonly TextBlock _status = Theme.Sub("");
    readonly Button _go;
    readonly Button _cancel;
    readonly CancellationTokenSource _cts = new();

    public FfmpegDialog()
    {
        Section = "Video tools";
        Width = 460; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowMaximize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;

        _go = Theme.Pill("Download", Download);
        _cancel = Theme.Pill("Not now", () => Close(), prominent: false);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        _cancel.Margin = new Thickness(0, 0, 8, 0);
        buttons.Children.Add(_cancel);
        buttons.Children.Add(_go);

        var stack = new StackPanel { Margin = new Thickness(24, 20, 24, 22) };
        stack.Children.Add(Theme.Label("Recording and video editing need ffmpeg", 16, Theme.Text, FontWeights.SemiBold, true));
        var sub = Theme.Sub("Poltergeist can download it for you (about 30 MB, once). It's stored in your user folder and used only by Poltergeist.");
        sub.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(sub);
        stack.Children.Add(_bar);
        _status.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(_status);
        stack.Children.Add(buttons);
        Body = stack;

        Closed += (_, _) => { _cts.Cancel(); Result.TrySetResult(Ffmpeg.Available); };
    }

    async void Download()
    {
        _go.IsEnabled = false;
        _cancel.Content = "Cancel";
        _bar.Visibility = Visibility.Visible;
        _status.Text = "Downloading…";
        try
        {
            await FfmpegSetup.InstallAsync(new Progress<double>(p => _bar.Value = p), _cts.Token);
            Close();
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            _status.Text = e.Message + " You can also put ffmpeg.exe and ffprobe.exe next to Poltergeist.exe.";
            _status.Foreground = Theme.Danger;
            _go.IsEnabled = true;
            _go.Content = "Try again";
        }
    }
}
