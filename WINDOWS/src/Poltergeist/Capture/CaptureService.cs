using System.Drawing;
using System.Windows.Forms;
using SkiaSharp;

namespace Poltergeist;

public enum CaptureMode { Area, Window, Fullscreen }

/// <summary>Picks something on screen and turns it into a PNG file.</summary>
static class CaptureService
{
    public sealed record Picked(Rectangle Bounds, SKBitmap? Frozen);

    /// <summary>
    /// Lets the user choose an area or a window. With <paramref name="freeze"/> the screen is stilled and dimmed first, and the
    /// frozen picture comes back with the rectangle so the result is exactly what they saw.
    /// </summary>
    public static async Task<Picked?> PickAsync(CaptureMode mode, bool freeze, string? hint = null)
    {
        var virt = ScreenGrab.VirtualBounds;
        var windows = mode == CaptureMode.Window ? Native.TopLevelWindows() : new List<Native.WindowInfo>();
        SKBitmap? frozen = freeze ? ScreenGrab.Capture(virt) : null;
        var kind = mode == CaptureMode.Window ? SelectionOverlay.Kind.Window : SelectionOverlay.Kind.Area;
        hint ??= mode == CaptureMode.Window ? "Click a window  ·  Esc to cancel" : "Drag to select  ·  Esc to cancel";

        var tcs = new TaskCompletionSource<Rectangle?>();
        var overlays = new List<SelectionOverlay>();
        foreach (var screen in Screen.AllScreens)
        {
            BitmapSourceHolder? holder = null;
            if (frozen != null)
            {
                using var part = Imaging.Crop(frozen, new Rectangle(screen.Bounds.X - virt.X, screen.Bounds.Y - virt.Y, screen.Bounds.Width, screen.Bounds.Height));
                holder = new BitmapSourceHolder(Imaging.ToBitmapSource(part));
            }
            var o = new SelectionOverlay(screen.Bounds, holder?.Source, freeze, kind, windows, hint);
            o.Done += r => tcs.TrySetResult(r);
            overlays.Add(o);
        }
        foreach (var o in overlays) o.Show();

        var rect = await tcs.Task;
        foreach (var o in overlays) o.Close();
        if (rect == null) { frozen?.Dispose(); return null; }
        return new Picked(rect.Value, frozen);
    }

    sealed record BitmapSourceHolder(System.Windows.Media.Imaging.BitmapSource Source);

    public sealed record Result(string? File, bool Cancelled, string? Error);

    public static async Task<Result> RunAsync(CaptureMode mode, bool freeze)
    {
        try
        {
            SKBitmap shot;
            if (mode == CaptureMode.Fullscreen)
            {
                var screen = Screen.FromPoint(Cursor.Position);
                shot = ScreenGrab.Capture(screen.Bounds);
            }
            else
            {
                var picked = await PickAsync(mode, freeze);
                if (picked == null) return new Result(null, true, null);
                if (picked.Frozen != null)
                {
                    var virt = ScreenGrab.VirtualBounds;
                    var r = picked.Bounds;
                    shot = Imaging.Crop(picked.Frozen, new Rectangle(r.X - virt.X, r.Y - virt.Y, r.Width, r.Height));
                    picked.Frozen.Dispose();
                }
                else
                {
                    await Task.Delay(220);                       // let the overlay disappear before the real grab
                    shot = ScreenGrab.Capture(picked.Bounds);
                }
            }
            using (shot)
            {
                var file = Path.Combine(Paths.Temp, $"Screenshot {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png");
                await File.WriteAllBytesAsync(file, Imaging.EncodePng(shot));
                return new Result(file, false, null);
            }
        }
        catch (Exception e)
        {
            return new Result(null, false, "The screenshot failed: " + e.Message);
        }
    }
}
