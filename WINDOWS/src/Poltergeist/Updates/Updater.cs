using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace Poltergeist;

static class AppInfo
{
    /// <summary>The version this build calls itself. The website's <c>downloads/version.txt</c> holds the newest one.</summary>
    public const string Version = "0.2.0";
    public const string Site = "https://poltergeist4haunt.github.io/Poltergeist-Site/";
    public const string VersionUrl = Site + "downloads/version.txt";
    public const string DownloadUrl = Site + "downloads/Poltergeist-win-x64.zip";
    public const string CreatorUrl = "https://haunt.gg/pix";
    public const string FriendUrl = "https://haunt.gg/SandMan";
}

/// <summary>Looks at the website for a newer version, and swaps the exe for it (then starts it again) if the user says yes.</summary>
static class Updater
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>The version on the website, or null if it can't be reached.</summary>
    public static async Task<string?> LatestAsync()
    {
        try
        {
            var text = (await Http.GetStringAsync(AppInfo.VersionUrl + "?t=" + DateTime.UtcNow.Ticks)).Trim();
            if (text.Length is 0 or > 24 || !text.All(c => char.IsLetterOrDigit(c) || c is '.' or '-')) return null;      // not a version number (an error page, say)
            return text;
        }
        catch { return null; }
    }

    /// <summary>Asks the site on launch (or when the user clicks "Check for updates"). With <paramref name="manual"/> it also says "you're up to date".</summary>
    public static async Task CheckAsync(bool manual)
    {
        var latest = await LatestAsync();
        if (latest == null)
        {
            if (manual) Notifier.Info("Couldn't check for updates", "Check your internet and try again.");
            return;
        }
        if (latest == AppInfo.Version)
        {
            if (manual) Notifier.Info("You're up to date", "Poltergeist " + AppInfo.Version + " is the newest version.");
            return;
        }
        new UpdateDialog(latest).Show();
    }

    /// <summary>Downloads the new exe, then hands over to a little script that waits for us to quit, swaps the file and relaunches.</summary>
    public static async Task InstallAsync(IProgress<double> progress, CancellationToken ct)
    {
        var target = Environment.ProcessPath ?? throw new InvalidOperationException("Can't find where Poltergeist is running from.");
        var work = Path.Combine(Paths.Temp, "update-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var zip = Path.Combine(work, "new.zip");

        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
        using (var resp = await http.GetAsync(AppInfo.DownloadUrl + "?t=" + DateTime.UtcNow.Ticks, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? 0;
            await using var net = await resp.Content.ReadAsStreamAsync(ct);
            await using var fs = File.Create(zip);
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

        string newExe = Path.Combine(work, "Poltergeist.exe");
        using (var archive = ZipFile.OpenRead(zip))
        {
            var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals("Poltergeist.exe", StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidDataException("The download didn't contain Poltergeist.exe.");
            entry.ExtractToFile(newExe, true);
        }
        if (new FileInfo(newExe).Length < 5_000_000) throw new InvalidDataException("The downloaded file looks wrong.");
        progress.Report(1);

        var script = Path.Combine(work, "update.cmd");
        var pid = Environment.ProcessId;
        File.WriteAllText(script, string.Join("\r\n", new[]
        {
            "@echo off",
            ":wait",
            $"tasklist /FI \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul && (timeout /t 1 /nobreak >nul & goto wait)",
            "set tries=0",
            ":copy",
            $"copy /Y \"{newExe}\" \"{target}\" >nul 2>nul && goto done",
            "set /a tries+=1",
            "if %tries% GEQ 20 goto done",
            "timeout /t 1 /nobreak >nul",
            "goto copy",
            ":done",
            $"start \"\" \"{target}\"",
            "(goto) 2>nul & del \"%~f0\"",
        }), new UTF8Encoding(false));

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
        Application.Current.Dispatcher.Invoke(() => App.Current.Quit());
    }
}

/// <summary>"Your version is out of date. Update and relaunch?" with Yeah and Nah.</summary>
sealed class UpdateDialog : ChromeWindow
{
    readonly ProgressBar _bar = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 14, 0, 0) };
    readonly TextBlock _status = Theme.Sub("");
    readonly Button _yes, _no;
    readonly CancellationTokenSource _cts = new();

    public UpdateDialog(string latest)
    {
        Section = "Update";
        Width = 480; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowMaximize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;

        _yes = Theme.Pill("Yeah", Install);
        _no = Theme.Pill("Nah", () => Close(), prominent: false);
        _no.Margin = new Thickness(0, 0, 8, 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(_no);
        buttons.Children.Add(_yes);

        var stack = new StackPanel { Margin = new Thickness(24, 20, 24, 22) };
        stack.Children.Add(Theme.Label("Your version is out of date", 17, Theme.Text, FontWeights.SemiBold, true));
        var sub = Theme.Sub($"You have {AppInfo.Version}, the newest is {latest}. Want to update and relaunch? It only takes a moment.");
        sub.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(sub);
        stack.Children.Add(_bar);
        _status.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(_status);
        stack.Children.Add(buttons);
        Body = stack;
        Closed += (_, _) => _cts.Cancel();
    }

    async void Install()
    {
        _yes.IsEnabled = false; _no.Content = "Cancel";
        _bar.Visibility = Visibility.Visible;
        _status.Text = "Downloading the update…";
        try { await Updater.InstallAsync(new Progress<double>(p => _bar.Value = p), _cts.Token); }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            _status.Text = "Couldn't update: " + e.Message + " You can download it again from the site.";
            _status.Foreground = Theme.Danger;
            _yes.IsEnabled = true; _yes.Content = "Try again";
        }
    }
}
