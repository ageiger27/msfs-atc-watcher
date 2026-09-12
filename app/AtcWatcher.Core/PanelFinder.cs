using System.Drawing;

namespace AtcWatcher.Core;

public sealed record PanelSearchResult(Rectangle Region, int OptionCount, string ScreenName, List<AtcOption> Options);

/// <summary>
/// Locates the ATC panel with no user input: OCR every screen, look for the numbered option
/// lines ("1 - ...", "2 - ..."), and build a capture region around them with room for the
/// option list to grow.
/// </summary>
public static class PanelFinder
{
    public static async Task<PanelSearchResult?> FindAsync(WindowsOcr ocr, Action<string>? progress = null)
    {
        PanelSearchResult? best = null;
        foreach (var (name, bounds, _) in ScreenCapture.Screens)
        {
            progress?.Invoke($"Scanning {name} ({bounds.Width}×{bounds.Height})…");
            using var bmp = ScreenCapture.Capture(bounds);
            var lines = await ReadUpscaledAsync(ocr, bmp);
            var candidate = Locate(lines, bounds);
            if (candidate is not null && (best is null || candidate.OptionCount > best.OptionCount))
                best = candidate with { ScreenName = name };
        }
        return best;
    }

    /// <summary>
    /// OCR at 2x when the engine allows it (small panel fonts lose digits at native resolution),
    /// returning bounds in the original bitmap's pixel space.
    /// </summary>
    public static async Task<List<OcrLine>> ReadUpscaledAsync(WindowsOcr ocr, Bitmap bmp)
    {
        var scale = Math.Max(bmp.Width, bmp.Height) * 2 <= Windows.Media.Ocr.OcrEngine.MaxImageDimension ? 2 : 1;
        using var scaled = ScreenCapture.Upscale(bmp, scale);
        var lines = await ocr.ReadAsync(scaled);
        if (scale == 1) return lines;
        return lines.Select(l => new OcrLine(l.Text, new Rectangle(
            l.Bounds.X / scale, l.Bounds.Y / scale,
            (int)Math.Ceiling(l.Bounds.Width / (double)scale), (int)Math.Ceiling(l.Bounds.Height / (double)scale)))).ToList();
    }

    /// <summary>Pure function so it can be unit-tested with canned OCR output.</summary>
    public static PanelSearchResult? Locate(List<OcrLine> lines, Rectangle screen)
    {
        var numbered = new List<(OcrLine Line, AtcOption Opt)>();
        foreach (var l in lines)
            if (OptionParser.TryParse(l.Text, out var opt) && l.Bounds.Height > 0)
                numbered.Add((l, opt));
        if (numbered.Count == 0) return null;

        // Group into vertical clusters: consecutive numbered lines close together.
        numbered.Sort((a, b) => a.Line.Bounds.Top.CompareTo(b.Line.Bounds.Top));
        var clusters = new List<List<(OcrLine Line, AtcOption Opt)>>();
        foreach (var item in numbered)
        {
            var last = clusters.LastOrDefault();
            if (last is not null)
            {
                var prev = last[^1].Line.Bounds;
                var gap = item.Line.Bounds.Top - prev.Bottom;
                if (gap < prev.Height * 3 && Math.Abs(item.Line.Bounds.Left - last[0].Line.Bounds.Left) < prev.Height * 4)
                {
                    last.Add(item);
                    continue;
                }
            }
            clusters.Add(new() { item });
        }

        // The ATC menu always starts at option 1 and has at least two entries.
        var bestCluster = clusters
            .Where(c => c.Any(x => x.Opt.Number == "1") && c.Count >= 2)
            .OrderByDescending(c => c.Count)
            .FirstOrDefault();
        if (bestCluster is null) return null;

        var union = bestCluster[0].Line.Bounds;
        foreach (var x in bestCluster.Skip(1)) union = Rectangle.Union(union, x.Line.Bounds);
        var lineH = (int)bestCluster.Average(x => x.Line.Bounds.Height);
        var rowPitch = bestCluster.Count > 1
            ? (bestCluster[^1].Line.Bounds.Top - bestCluster[0].Line.Bounds.Top) / (bestCluster.Count - 1)
            : lineH * 2;

        // Room for up to ~8 options above/below the ones we can see, plus generous width for long texts.
        var extraRows = Math.Max(0, 8 - bestCluster.Count);
        var top = union.Top - rowPitch * extraRows - lineH;
        var bottom = union.Bottom + rowPitch * 2;
        var left = union.Left - lineH;
        var width = Math.Max((int)(union.Width * 1.6), 480) + lineH * 2;

        var region = Rectangle.Intersect(
            new Rectangle(left, top, width, bottom - top),
            new Rectangle(0, 0, screen.Width, screen.Height));
        region.Offset(screen.Left, screen.Top);   // bitmap space -> virtual-screen space

        return new PanelSearchResult(region, bestCluster.Count, "", bestCluster.Select(x => x.Opt).ToList());
    }
}
