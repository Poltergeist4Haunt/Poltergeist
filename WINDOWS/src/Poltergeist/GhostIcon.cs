using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using SkiaSharp;

namespace Poltergeist;

/// <summary>
/// The Poltergeist ghost: the same shape as the menu-bar icon on the Mac (an 18×18 box, two eyes cut out). Used for the tray,
/// the title bars and the app icon.
/// </summary>
static class GhostIcon
{
    // SVG path data, y pointing down. Even-odd fill leaves the eyes open.
    public static string PathData => Poltergeist.Core.AppIcon.GhostPath;

    /// <summary>For XAML-style use: a WPF geometry in the 18×18 box.</summary>
    public static Geometry Geometry()
    {
        var g = System.Windows.Media.Geometry.Parse("F0 " + PathData);
        g.Freeze();
        return g;
    }

    /// <summary>A small ghost element for title bars.</summary>
    public static FrameworkElement Element(double size, Brush fill) => new System.Windows.Shapes.Path
    {
        Data = Geometry(), Fill = fill, Stretch = Stretch.Uniform, Width = size, Height = size,
    };

    public static SKPath SkiaPath() => Poltergeist.Core.AppIcon.Ghost();

    // ---- tray ------------------------------------------------------------------------------------------------------------

    static bool TaskbarIsLight()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }
        catch { return false; }
    }

    /// <summary>The tray icon at <paramref name="px"/> pixels: white on a dark taskbar, black on a light one; an optional red dot.</summary>
    public static System.Drawing.Icon Tray(int px, bool recordingDot)
    {
        using var bmp = Poltergeist.Core.AppIcon.RenderGhost(px, TaskbarIsLight() ? new SKColor(20, 20, 20) : SKColors.White, recordingDot);
        return ToIcon(bmp);
    }

    static System.Drawing.Icon ToIcon(SKBitmap bmp)
    {
        using var gdi = new System.Drawing.Bitmap(bmp.Width, bmp.Height, PixelFormat.Format32bppPArgb);
        var data = gdi.LockBits(new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try { Marshal.Copy(bmp.Bytes, 0, data.Scan0, bmp.Bytes.Length); }
        finally { gdi.UnlockBits(data); }
        var hicon = gdi.GetHicon();
        try { return (System.Drawing.Icon)System.Drawing.Icon.FromHandle(hicon).Clone(); }
        finally { Native.DestroyIcon(hicon); }
    }

    // ---- window icon -----------------------------------------------------------------------------------------------------

    /// <summary>The app icon as a WPF image (pink ghost on a dark rounded tile), used for the taskbar entry of open windows.</summary>
    public static ImageSource AppImage(int px = 64)
    {
        using var bmp = RenderAppIcon(px);
        var wb = new System.Windows.Media.Imaging.WriteableBitmap(px, px, 96, 96, System.Windows.Media.PixelFormats.Pbgra32, null);
        wb.WritePixels(new Int32Rect(0, 0, px, px), bmp.Bytes, px * 4, 0);
        wb.Freeze();
        return wb;
    }

    public static SKBitmap RenderAppIcon(int px) => Poltergeist.Core.AppIcon.Render(px);
}
