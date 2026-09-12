using AtcWatcher.Core;
using Xunit;

namespace AtcWatcher.Tests;

public class RulesTests
{
    private static Decider MakeDecider(string callsign = "Speedbird NAO9210") =>
        new(new Settings { Callsign = callsign });

    [Theory]
    [InlineData("Acknowledge Handoff", Verdict.Press)]
    [InlineData("Say Again", Verdict.Deny)]
    [InlineData("Salt Lake Center, Speedbird NAO9210, 12,000 ft.", Verdict.Press)]
    [InlineData("Salt Lake Center, Speedbird NA09210 , 12,000 ft.", Verdict.Press)]
    [InlineData("Salt Lake Center, Speedbird NAO9210, FL350", Verdict.Press)]
    [InlineData("Denver Center, N172SP, 12,000 ft.", Verdict.Press)]
    [InlineData("Tune COM1 to 128.05", Verdict.Press)]
    [InlineData("Switch to 128.05", Verdict.Press)]
    [InlineData("Contact Salt Lake Center on 128.05", Verdict.Press)]
    [InlineData("Contact Salt Lake Center", Verdict.Press)]
    [InlineData("Tune Salt Lake Center", Verdict.Press)]
    [InlineData("Tune ATIS", Verdict.Deny)]
    [InlineData("Tune nearest ATIS", Verdict.Deny)]
    [InlineData("Request vector to next waypoint", Verdict.Deny)]
    [InlineData("[ Request Cruising Altitude Increase I", Verdict.Deny)]
    [InlineData("Cancel IFR", Verdict.Deny)]
    [InlineData("Request frequency change", Verdict.Deny)]
    [InlineData("Change cruising altitude", Verdict.Deny)]
    [InlineData("Nearest airport list", Verdict.Deny)]
    [InlineData("Descend and maintain 5,000, Speedbird NAO9210", Verdict.Press)]
    [InlineData("Roger", Verdict.Press)]
    [InlineData("Salt Lake Center, Speedbird NAO9210, request altitude change", Verdict.Deny)]
    [InlineData("Unable", Verdict.Deny)]
    public void Classifies_real_panel_texts(string text, Verdict expected)
    {
        var d = MakeDecider();
        var v = d.Classify(new AtcOption("1", text, text));
        Assert.Equal(expected, v.Verdict);
    }

    [Fact]
    public void Callsign_match_is_fuzzy_and_optional()
    {
        var noRules = new Settings { Callsign = "N172SP", AllowPatterns = new(), DenyPatterns = new() };
        var d = new Decider(noRules);
        Assert.Equal(Verdict.Press, d.Classify(new AtcOption("1", "Level 10,000, Nl 72SP", "")).Verdict);
        Assert.Equal(Verdict.Skip, d.Classify(new AtcOption("1", "Level 10,000", "")).Verdict);
        var empty = new Decider(new Settings { Callsign = "", AllowPatterns = new(), DenyPatterns = new() });
        Assert.Equal(Verdict.Skip, empty.Classify(new AtcOption("1", "anything", "")).Verdict);
    }

    [Theory]
    [InlineData("1 - Acknowledge Handoff", "1", "Acknowledge Handoff")]
    [InlineData("2. Say Again", "2", "Say Again")]
    [InlineData("3) Cancel IFR", "3", "Cancel IFR")]
    [InlineData("4 Tune ATIS", "4", "Tune ATIS")]
    public void Parses_numbered_options(string line, string number, string text)
    {
        Assert.True(OptionParser.TryParse(line, out var opt));
        Assert.Equal(number, opt.Number);
        Assert.Equal(text, opt.Text);
    }

    [Theory]
    [InlineData("10,000 ft.")]
    [InlineData("Salt Lake Center, Speedbird NAO9210 , 12,000 ft.")]
    [InlineData("128.05")]
    [InlineData("")]
    public void Ignores_history_lines(string line)
    {
        Assert.False(OptionParser.TryParse(line, out _));
    }

    [Fact]
    public void Detects_callsign_from_controller_lines()
    {
        var lines = new[]
        {
            "Salt Lake Center, Speedbird NAO9210 , 12,000 ft.",
            "SALT LAKE CITY",
            "Speedbird NAO9210 , Salt Lake Center, Altimeter 2996, radar contact, continue to KU06M.",
            "Speedbird NAO9210 contact Salt Lake Center on 128.05. Good day.",
            "1 - Acknowledge Handoff",
            "2 - Say Again",
        };
        Assert.Equal("Speedbird NAO9210", CallsignDetector.Detect(lines));

        var ga = new[] { "N172SP, descend and maintain 5,000", "N172SP contact Seattle Approach on 120.10" };
        Assert.Equal("N172SP", CallsignDetector.Detect(ga));
    }

    [Fact]
    public void Panel_finder_builds_a_region_around_the_option_cluster()
    {
        var screen = new System.Drawing.Rectangle(0, 0, 3440, 1440);
        var lines = new List<OcrLine>
        {
            new("Speedbird NAO9210 contact Salt Lake Center on 128.05.", new(200, 1100, 400, 22)),
            new("1 - Acknowledge Handoff", new(150, 1180, 260, 24)),
            new("2 - Say Again", new(150, 1224, 150, 24)),
            new("1 - Unrelated on the other side", new(3000, 100, 200, 20)),
        };
        var r = PanelFinder.Locate(lines, screen);
        Assert.NotNull(r);
        Assert.Equal(2, r!.OptionCount);
        Assert.True(r.Region.Contains(new System.Drawing.Rectangle(150, 1180, 260, 68)));
        Assert.True(r.Region.Top < 1180 - 100, "leaves room above for more options");
        Assert.True(r.Region.Right <= 3440 && r.Region.Bottom <= 1440, "clipped to the screen");
    }
}
