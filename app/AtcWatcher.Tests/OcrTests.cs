using System.Drawing;
using AtcWatcher.Core;
using Xunit;

namespace AtcWatcher.Tests;

/// <summary>End-to-end on a real capture from the sim. Needs the Windows English OCR pack.</summary>
public class OcrTests
{
    [Fact]
    public async Task Reads_real_handoff_capture_and_chooses_acknowledge()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "handoff.png");
        Assert.True(File.Exists(path), $"missing {path}");

        var ocr = new WindowsOcr();
        using var bmp = new Bitmap(path);
        using var scaled = ScreenCapture.Upscale(bmp, 2);
        var lines = await ocr.ReadAsync(scaled);
        var options = OptionParser.Parse(lines.Select(l => l.Text));

        Assert.Contains(options, o => o.Number == "1" && o.Text.Contains("Acknowledge", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(options, o => o.Number == "2" && o.Text.Contains("Say Again", StringComparison.OrdinalIgnoreCase));

        var (chosen, _) = new Decider(new Settings { Callsign = "Speedbird NAO9210" }).Choose(options);
        Assert.NotNull(chosen);
        Assert.Equal("1", chosen!.Number);

        // OCR may read the O as a zero; the fuzzy form must match either way.
        Assert.Equal(Fuzzy.Normalize("Speedbird NAO9210"), Fuzzy.Normalize(CallsignDetector.Detect(lines.Select(l => l.Text)) ?? ""));
    }

    [Fact]
    public async Task Panel_finder_locates_options_in_a_full_frame()
    {
        // Paste the panel capture into a larger dark frame to mimic a full screen.
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "handoff.png");
        using var panel = new Bitmap(path);
        using var frame = new Bitmap(1920, 1080);
        using (var g = Graphics.FromImage(frame))
        {
            g.Clear(Color.FromArgb(30, 40, 60));
            g.DrawImage(panel, 300, 500);
        }
        var ocr = new WindowsOcr();
        var lines = await PanelFinder.ReadUpscaledAsync(ocr, frame);
        var found = PanelFinder.Locate(lines, new Rectangle(0, 0, 1920, 1080));
        Assert.NotNull(found);
        Assert.True(found!.OptionCount >= 2);
        // The region must include where the options actually are (bottom of the pasted panel).
        Assert.True(found.Region.Contains(new Point(320, 500 + panel.Height - 40)));
    }
}
