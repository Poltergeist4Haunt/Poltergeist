using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Poltergeist.Core;

namespace Poltergeist;

/// <summary>Click, then press the new shortcut. Esc cancels, Delete clears it.</summary>
sealed class HotKeyButton : Button
{
    readonly HotKeyAction _action;
    readonly TextBlock _message;
    bool _recording;

    public HotKeyButton(HotKeyAction action, TextBlock message)
    {
        _action = action;
        _message = message;
        Style = Theme.Style("Ghost");
        MinWidth = 120;
        FontFamily = Theme.Mono;
        FontSize = 12.5;
        Focusable = true;
        Refresh();
        Click += (_, _) => { if (_recording) Stop(); else Start(); };
        LostKeyboardFocus += (_, _) => { if (_recording) Stop(); };
    }

    void Refresh()
    {
        Content = _recording ? "Press keys…" : App.Current.Settings.Binding(_action).Display;
        Style = Theme.Style(_recording ? "Pill" : "Ghost");
    }

    void Start()
    {
        _message.Text = "";
        _recording = true;
        App.Current.HotKeys.UnregisterAll();         // so the current shortcut can be pressed again without firing a capture
        Refresh();
        Focus();
        Keyboard.Focus(this);
    }

    void Stop()
    {
        _recording = false;
        Refresh();
        App.Current.RegisterHotkeys();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!_recording) { base.OnPreviewKeyDown(e); return; }
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.None) return;

        var s = App.Current.Settings;
        if (key == Key.Escape) { Stop(); return; }
        int mods = 0;
        var m = Keyboard.Modifiers;
        if (m.HasFlag(ModifierKeys.Alt)) mods |= HotKeyBinding.Alt;
        if (m.HasFlag(ModifierKeys.Control)) mods |= HotKeyBinding.Ctrl;
        if (m.HasFlag(ModifierKeys.Shift)) mods |= HotKeyBinding.Shift;
        if (m.HasFlag(ModifierKeys.Windows)) mods |= HotKeyBinding.Win;

        if ((key is Key.Delete or Key.Back) && mods == 0) { s.SetBinding(_action, HotKeyBinding.None); Stop(); return; }

        bool standalone = key is >= Key.F1 and <= Key.F24 || key == Key.PrintScreen;
        if (!standalone && (mods & (HotKeyBinding.Ctrl | HotKeyBinding.Alt | HotKeyBinding.Win)) == 0)
        {
            _message.Text = "Include Ctrl, Alt or Win";
            return;
        }
        int vk = KeyInterop.VirtualKeyFromKey(key);
        foreach (HotKeyAction other in Enum.GetValues<HotKeyAction>())
        {
            if (other == _action) continue;
            var b = s.Binding(other);
            if (b.IsSet && b.Vk == vk && b.Modifiers == mods) { _message.Text = $"Already used by “{other.Title()}”"; return; }
        }
        s.SetBinding(_action, new HotKeyBinding(vk, mods, LabelFor(key)));
        Stop();
    }

    static string LabelFor(Key k)
    {
        if (k is >= Key.D0 and <= Key.D9) return ((char)('0' + (k - Key.D0))).ToString();
        if (k is >= Key.NumPad0 and <= Key.NumPad9) return "Num" + (k - Key.NumPad0);
        return k switch
        {
            Key.Space => "Space", Key.Return => "Enter", Key.Tab => "Tab", Key.Left => "←", Key.Right => "→", Key.Up => "↑", Key.Down => "↓",
            Key.PrintScreen => "PrtScn", Key.OemTilde => "`", Key.OemMinus => "-", Key.OemPlus => "=", Key.OemComma => ",", Key.OemPeriod => ".",
            Key.OemQuestion => "/", Key.OemSemicolon => ";", Key.OemQuotes => "'", Key.OemOpenBrackets => "[", Key.OemCloseBrackets => "]", Key.OemPipe => "\\",
            _ => k.ToString(),
        };
    }
}
