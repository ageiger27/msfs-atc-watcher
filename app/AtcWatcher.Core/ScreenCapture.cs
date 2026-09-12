using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace AtcWatcher.Core;

public static class ScreenCapture
{
    /// <summary>Copies a rectangle of the desktop (physical pixels, virtual-screen coordinates).</summary>
    public static Bitmap Capture(Rectangle r)
    {
        var bmp = new Bitmap(Math.Max(1, r.Width), Math.Max(1, r.Height), PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(r.Left, r.Top, 0, 0, bmp.Size, CopyPixelOperation.SourceCopy);
        return bmp;
    }

    public static Bitmap Upscale(Bitmap src, int scale)
    {
        if (scale <= 1) return (Bitmap)src.Clone();
        var dst = new Bitmap(src.Width * scale, src.Height * scale, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(src, 0, 0, dst.Width, dst.Height);
        return dst;
    }

    public static Rectangle VirtualScreen => SystemInformation.VirtualScreen;

    public static IReadOnlyList<(string Name, Rectangle Bounds, bool Primary)> Screens =>
        Screen.AllScreens.Select(s => (s.DeviceName, s.Bounds, s.Primary)).ToList();
}
