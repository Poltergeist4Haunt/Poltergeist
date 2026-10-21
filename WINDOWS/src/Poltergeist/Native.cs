using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace Poltergeist;

/// <summary>The few Win32 calls WPF doesn't wrap.</summary>
static class Native
{
    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_NOREPEAT = 0x4000;
    public static readonly IntPtr HWND_MESSAGE = new(-3);

    [DllImport("psapi.dll")] public static extern bool EmptyWorkingSet(IntPtr process);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int index, int value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hWnd, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT rect, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int value, int size);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] public static extern bool PlaySound(string? name, IntPtr module, uint flags);

    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    public const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
    public const uint WDA_NONE = 0, WDA_EXCLUDEFROMCAPTURE = 0x11;
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }

    // ---- DWM look: dark title area, rounded corners, a quiet border --------------------------------------------------

    public static void StyleWindow(IntPtr hwnd)
    {
        int on = 1, round = 2, border = 0x001c1c1c, caption = 0x00090909;
        DwmSetWindowAttribute(hwnd, 20, ref on, 4);        // DWMWA_USE_IMMERSIVE_DARK_MODE
        DwmSetWindowAttribute(hwnd, 33, ref round, 4);     // DWMWA_WINDOW_CORNER_PREFERENCE = round
        DwmSetWindowAttribute(hwnd, 34, ref border, 4);    // DWMWA_BORDER_COLOR
        DwmSetWindowAttribute(hwnd, 35, ref caption, 4);   // DWMWA_CAPTION_COLOR
    }

    /// <summary>The exact on-screen rectangle of a window (without the invisible resize margin Windows 10/11 add).</summary>
    public static Rectangle VisibleBounds(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, 9, out RECT r, Marshal.SizeOf<RECT>()) == 0) return r.ToRectangle();   // EXTENDED_FRAME_BOUNDS
        return GetWindowRect(hwnd, out r) ? r.ToRectangle() : Rectangle.Empty;
    }

    public sealed record WindowInfo(IntPtr Handle, string Title, Rectangle Bounds);

    /// <summary>Visible top-level windows, front to back.</summary>
    public static List<WindowInfo> TopLevelWindows(bool includeOwn = true)
    {
        var list = new List<WindowInfo>();
        var self = Environment.ProcessId;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
            if (DwmGetWindowAttribute(h, 14, out int cloaked, 4) == 0 && cloaked != 0) return true;     // hidden UWP / other-desktop windows
            var cls = new StringBuilder(64);
            GetClassName(h, cls, cls.Capacity);
            var c = cls.ToString();
            if (c is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
            GetWindowThreadProcessId(h, out var pid);
            if (!includeOwn && pid == self) return true;
            var title = new StringBuilder(256);
            GetWindowText(h, title, title.Capacity);
            var b = VisibleBounds(h);
            if (b.Width < 40 || b.Height < 40) return true;
            list.Add(new WindowInfo(h, title.ToString(), b));
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
