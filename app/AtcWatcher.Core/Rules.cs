using System.Text;
using System.Text.RegularExpressions;

namespace AtcWatcher.Core;

public sealed record AtcOption(string Number, string Text, string Raw);

public enum Verdict { Press, Deny, Skip }

public sealed record Classified(AtcOption Option, Verdict Verdict, string Reason);

public static class OptionParser
{
    // "1 - Acknowledge Handoff", "1. Roger", "1) ...". The digit must be followed by a separator
    // or whitespace so history lines like "10,000 ft" never parse as option 1.
    private static readonly Regex OptionRe =
        new(@"^\s*(\d)(?:\s*[.,:;)\]\-]\s*|\s+)(\S.*)$", RegexOptions.Compiled);

    public static bool TryParse(string line, out AtcOption option)
    {
        var m = OptionRe.Match(line);
        if (m.Success)
        {
            option = new AtcOption(m.Groups[1].Value, m.Groups[2].Value.Trim(), line);
            return true;
        }
        option = null!;
        return false;
    }

    public static List<AtcOption> Parse(IEnumerable<string> lines)
    {
        var result = new List<AtcOption>();
        foreach (var line in lines)
            if (TryParse(line, out var opt)) result.Add(opt);
        return result;
    }
}

public static class Fuzzy
{
    /// <summary>
    /// Upper-cases, strips everything but letters and digits, and folds the OCR confusions
    /// (O/0, I/L/1, S/5, B/8, Z/2) so "Speedbird NAO9210" and "Speedbird NA09210" compare equal.
    /// </summary>
    public static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.ToUpperInvariant())
        {
            if (!char.IsLetterOrDigit(ch)) continue;
            sb.Append(ch switch
            {
                'O' => '0', 'I' => '1', 'L' => '1', 'S' => '5', 'B' => '8', 'Z' => '2',
                _ => ch,
            });
        }
        return sb.ToString();
    }
}

public sealed class Decider
{
    private readonly List<Regex> _allow;
    private readonly List<Regex> _deny;
    private readonly string _callsign;

    public Decider(Settings s)
    {
        _allow = s.AllowPatterns.Select(Compile).ToList();
        _deny = s.DenyPatterns.Select(Compile).ToList();
        _callsign = Fuzzy.Normalize(s.Callsign ?? "");
    }

    private static Regex Compile(string p) => new(p, RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string Short(Regex r)
    {
        var p = r.ToString();
        return p.Length > 40 ? p[..40] + "…" : p;
    }

    public Classified Classify(AtcOption opt)
    {
        foreach (var d in _deny)
            if (d.IsMatch(opt.Text)) return new(opt, Verdict.Deny, $"deny rule /{Short(d)}/");
        foreach (var a in _allow)
            if (a.IsMatch(opt.Text)) return new(opt, Verdict.Press, $"allow rule /{Short(a)}/");
        if (_callsign.Length >= 4 && Fuzzy.Normalize(opt.Text).Contains(_callsign))
            return new(opt, Verdict.Press, "contains your callsign");
        return new(opt, Verdict.Skip, "no rule matched");
    }

    public (AtcOption? Chosen, List<Classified> Verdicts) Choose(IEnumerable<AtcOption> options)
    {
        var verdicts = options.Select(Classify).ToList();
        var chosen = verdicts.FirstOrDefault(v => v.Verdict == Verdict.Press)?.Option;
        return (chosen, verdicts);
    }
}
