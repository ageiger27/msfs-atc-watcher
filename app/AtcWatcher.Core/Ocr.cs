using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace AtcWatcher.Core;

public sealed record OcrLine(string Text, Rectangle Bounds);

/// <summary>Thin wrapper over the OCR engine that ships with Windows 10/11.</summary>
public sealed class WindowsOcr
{
    private readonly OcrEngine _engine;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WindowsOcr()
    {
        _engine = OcrEngine.TryCreateFromUserProfileLanguages()
                  ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"))
                  ?? throw new InvalidOperationException(
                      "Windows OCR is not available. Install the English language pack with " +
                      "'Optical character recognition' under Settings > Time & Language > Language & region.");
    }

    public string LanguageTag => _engine.RecognizerLanguage.LanguageTag;

    /// <summary>Recognises text in a bitmap. Bounds are in the bitmap's own pixel space.</summary>
    public async Task<List<OcrLine>> ReadAsync(Bitmap bitmap)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var source = FitToEngine(bitmap, out var scale);
            using var sb = ToSoftwareBitmap(source);
            var result = await _engine.RecognizeAsync(sb);
            var lines = new List<OcrLine>();
            foreach (var line in result.Lines)
            {
                Rectangle? union = null;
                foreach (var w in line.Words)
                {
                    var r = w.BoundingRect;
                    var rect = new Rectangle((int)(r.X / scale), (int)(r.Y / scale),
                                             (int)Math.Ceiling(r.Width / scale), (int)Math.Ceiling(r.Height / scale));
                    union = union is null ? rect : Rectangle.Union(union.Value, rect);
                }
                lines.Add(new OcrLine(line.Text, union ?? Rectangle.Empty));
            }
            lines.Sort((a, b) => a.Bounds.Top.CompareTo(b.Bounds.Top));
            return lines;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The engine rejects images over MaxImageDimension; shrink if needed.</summary>
    private Bitmap FitToEngine(Bitmap bmp, out double scale)
    {
        var max = (int)OcrEngine.MaxImageDimension;
        var largest = Math.Max(bmp.Width, bmp.Height);
        if (largest <= max)
        {
            scale = 1.0;
            return (Bitmap)bmp.Clone();
        }
        scale = (double)max / largest;
        var dst = new Bitmap((int)(bmp.Width * scale), (int)(bmp.Height * scale), PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.DrawImage(bmp, 0, 0, dst.Width, dst.Height);
        return dst;
    }

    private static SoftwareBitmap ToSoftwareBitmap(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stride = data.Stride;
            var bytes = new byte[bmp.Width * 4 * bmp.Height];
            for (var y = 0; y < bmp.Height; y++)
                Marshal.Copy(data.Scan0 + y * stride, bytes, y * bmp.Width * 4, bmp.Width * 4);
            return SoftwareBitmap.CreateCopyFromBuffer(bytes.AsBuffer(), BitmapPixelFormat.Bgra8,
                                                       bmp.Width, bmp.Height, BitmapAlphaMode.Ignore);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
