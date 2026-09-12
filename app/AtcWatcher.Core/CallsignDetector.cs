using System.Text.RegularExpressions;

namespace AtcWatcher.Core;

/// <summary>
/// Guesses the player's callsign from the ATC message history. Controller lines start with the
/// callsign ("Speedbird NAO9210, Salt Lake Center, altimeter..." or "N172SP contact ..."), so we
/// take the leading token(s) up to the first comma or instruction verb and pick the most common.
/// </summary>
public static class CallsignDetector
{
    private static readonly Regex Lead = new(
        @"^\s*((?:[A-Za-z]{3,}\s+)?[A-Za-z]*\d[A-Za-z0-9]*)\s*(?:,|\b(contact|climb|descend|maintain|turn|fly|squawk|radar|altimeter|cleared|expect|proceed|reduce|increase|continue|hold|taxi|report|cross|say)\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string? Detect(IEnumerable<string> lines)
    {
        var counts = new Dictionary<string, (int Count, string Display)>();
        foreach (var line in lines)
        {
            if (OptionParser.TryParse(line, out _)) continue;
            var m = Lead.Match(line);
            if (!m.Success) continue;
            var display = Regex.Replace(m.Groups[1].Value.Trim(), @"\s+", " ");
            var key = Fuzzy.Normalize(display);
            if (key.Length < 3) continue;
            counts[key] = counts.TryGetValue(key, out var c) ? (c.Count + 1, c.Display) : (1, display);
        }
        return counts.Count == 0 ? null : counts.Values.OrderByDescending(v => v.Count).First().Display;
    }
}
