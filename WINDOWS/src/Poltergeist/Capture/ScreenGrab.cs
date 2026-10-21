using System.Drawing;
using System.Windows.Forms;
using SkiaSharp;

namespace Poltergeist;

static class ScreenGrab
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);

    /// <summary>Every monitor together, in physical pixels (the app is per-monitor DPI aware, so these match what is on screen).</summary>
    public static Rectangle VirtualBounds => SystemInformation.VirtualScreen;

    public static SKBitmap Capture(Rectangle r)
    {
        using var gdi = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(gdi))
        {
            // BitBlt with CAPTUREBLT so layered / transparent windows are included (CopyFromScreen rejects that flag combination).
            var dst = g.GetHdc();
            var src = GetDC(IntPtr.Zero);
            try { BitBlt(dst, 0, 0, r.Width, r.Height, src, r.X, r.Y, 0x00CC0020 | 0x40000000); }
            finally { ReleaseDC(IntPtr.Zero, src); g.ReleaseHdc(dst); }
        }
        return Imaging.FromGdi(gdi);
    }
}
