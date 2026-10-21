using System.Windows.Interop;
using Poltergeist.Core;

namespace Poltergeist;

/// <summary>Global shortcuts through RegisterHotKey (no extra permission, works over every app).</summary>
public sealed class HotKeyManager : IDisposable
{
    readonly HwndSource _source;
    readonly Dictionary<int, Action> _actions = new();
    readonly List<int> _ids = new();
    int _next = 1;

    public HotKeyManager()
    {
        _source = new HwndSource(new HwndSourceParameters("PoltergeistHotKeys") { ParentWindow = Native.HWND_MESSAGE, Width = 0, Height = 0 });
        _source.AddHook(Hook);
    }

    /// <summary>Returns false if another app already owns the combination.</summary>
    public bool Register(HotKeyBinding b, Action action)
    {
        if (!b.IsSet) return true;
        int id = _next++;
        if (!Native.RegisterHotKey(_source.Handle, id, (uint)b.Modifiers | Native.MOD_NOREPEAT, (uint)b.Vk)) return false;
        _actions[id] = action;
        _ids.Add(id);
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in _ids) Native.UnregisterHotKey(_source.Handle, id);
        _ids.Clear();
        _actions.Clear();
    }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var a))
        {
            handled = true;
            Application.Current.Dispatcher.BeginInvoke(a);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.Dispose();
    }
}
