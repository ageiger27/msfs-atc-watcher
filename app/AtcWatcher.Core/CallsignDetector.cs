using System.Text.RegularExpressions;

namespace AtcWatcher.Core;

/// <summary>
/// Guesses the player's callsign from the ATC message history. Controller lines address the player
/// by callsign ("Speedbird NAO5410 , Salt Lake Center, altimeter...", "N172SP contact ...") and the
/// player's own read-backs end with it ("Climb and maintain 13,000 ft, Speedbird NAO5410 ."). The
/// panel wraps long lines, so the history is joined into one string before matching, and the most
/// frequent candidate wins.
/// </summary>
public static class CallsignDetector
{
    // Optional airline word + a token containing a digit, followed by a comma, a full stop, or an
    // instruction verb. Examples: "Speedbird NAO5410 ,", "Cessna 770.", "N172SP contact".
    private static readonly Regex Candidate = new(
        @"\b(?:(?<word>[A-Za-z]{3,})\s+)?(?<token>[A-Za-z]*\d[A-Za-z0-9]*)\s*(?:,|\.(?!\d)|\b(?:contact|climb|descend|maintain|turn|fly|squawk|radar|altimeter|cleared|expect|proceed|reduce|increase|continue|hold|taxi|report|cross|say|request|acknowledge|is|you)\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Words that precede numbers in ATC phraseology but are never part of a callsign.
    private static readonly HashSet<string> NotAirline = new(StringComparer.OrdinalIgnoreCase)
    {
        "altimeter", "squawk", "heading", "runway", "wind", "winds", "maintain", "climb", "descend",
        "level", "expect", "flight", "contact", "tower", "ground", "approach", "center", "centre",
        "departure", "frequency", "direct", "turn", "fly", "cross", "hold", "taxi", "via", "type",
        "miles", "mile", "feet", "the", "and", "for", "with", "from", "north", "south", "east", "west",
        "northwest", "northeast", "southwest", "southeast", "at", "to", "on", "of", "is", "you",
    };

    public static string? Detect(IEnumerable<string> lines)
    {
        var history = lines.Where(l => !OptionParser.TryParse(l, out _));
        var text = string.Join(" ", history);
        var counts = new Dictionary<string, (int Count, string Display)>();

        foreach (Match m in Candidate.Matches(text))
        {
            var word = m.Groups["word"].Success ? m.Groups["word"].Value : null;
            var token = m.Groups["token"].Value;
            if (word is not null && NotAirline.Contains(word)) word = null;

            // A bare token must look like a tail number (letters and digits, 3+ chars), not "25".
            if (word is null && (token.Length < 3 || !token.Any(char.IsLetter))) continue;
            if (word is not null && token.Length < 2) continue;

            var display = word is null ? token.ToUpperInvariant() : $"{Capitalise(word)} {token.ToUpperInvariant()}";
            var key = Fuzzy.Normalize(display);
            counts[key] = counts.TryGetValue(key, out var c) ? (c.Count + 1, c.Display) : (1, display);
        }

        return counts.Count == 0 ? null : counts.Values.OrderByDescending(v => v.Count).First().Display;
    }

    /// <summary>True when any history line on the panel contains the callsign (OCR-tolerant).</summary>
    public static bool IsSeen(IEnumerable<string> lines, string callsign)
    {
        var key = Fuzzy.Normalize(callsign ?? "");
        if (key.Length < 4) return false;
        var text = Fuzzy.Normalize(string.Join(" ", lines.Where(l => !OptionParser.TryParse(l, out _))));
        return text.Contains(key);
    }

    private static string Capitalise(string w) => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant();
}
