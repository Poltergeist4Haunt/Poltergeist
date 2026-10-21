using System.Windows.Forms;
using Poltergeist.Core;

namespace Poltergeist;

/// <summary>The notification-area icon: the Poltergeist ghost, with a red dot while recording. Clicking opens <see cref="TrayPopup"/>.</summary>
public sealed class TrayHost : IDisposable
{
    readonly NotifyIcon _icon;
    System.Drawing.Icon? _current;
    bool _recording;
    string _status = "";

    public TrayHost()
    {
        _icon = new NotifyIcon { Visible = true, Text = "Poltergeist" };
        Refresh();
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button is MouseButtons.Left or MouseButtons.Right) TrayPopup.Toggle();
        };
        // Re-draw when the user flips Windows between light and dark.
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, _) => Application.Current.Dispatcher.BeginInvoke(Refresh);
    }

    public bool Recording
    {
        get => _recording;
        set { _recording = value; Refresh(); }
    }

    public void SetStatus(string status)
    {
        _status = status;
        _icon.Text = string.IsNullOrEmpty(status) ? "Poltergeist" : ("Poltergeist · " + status);
        if (_icon.Text.Length > 62) _icon.Text = _icon.Text[..62];
    }

    void Refresh()
    {
        var size = SystemInformation.SmallIconSize.Width;
        var next = GhostIcon.Tray(Math.Max(16, size * 2), _recording);       // drawn larger and scaled by Windows, so it stays crisp
        _icon.Icon = next;
        _current?.Dispose();
        _current = next;
    }

    public void Balloon(string title, string text, bool error = false)
    {
        _icon.ShowBalloonTip(5000, title, text, error ? ToolTipIcon.Error : ToolTipIcon.None);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _current?.Dispose();
    }
}
