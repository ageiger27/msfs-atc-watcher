using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AtcWatcher.Core;

/// <summary>
/// An option that is normally denied but should be pressed when the rest of the panel matches
/// <see cref="Context"/> (ATC message history and the other options). Beats the deny list.
/// </summary>
public sealed record ContextRule(string Option, string Context, string Note = "");

public sealed record RegionRect(int Left, int Top, int Width, int Height)
{
    public Rectangle ToRectangle() => new(Left, Top, Width, Height);
    public static RegionRect From(Rectangle r) => new(r.Left, r.Top, r.Width, r.Height);
    public override string ToString() => $"{Width}×{Height} at ({Left}, {Top})";
}

/// <summary>
/// User settings. Stored as snake_case JSON in %APPDATA%\AtcWatcher\settings.json so the file
/// has the same shape as the original Python script's config.json.
/// </summary>
public sealed class Settings
{
    /// <summary>%APPDATA%\AtcWatcher, or the ATCWATCHER_DIR environment variable when set (used for testing).</summary>
    public static readonly string Dir =
        Environment.GetEnvironmentVariable("ATCWATCHER_DIR") is { Length: > 0 } overrideDir
            ? overrideDir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AtcWatcher");
    public static readonly string FilePath = Path.Combine(Dir, "settings.json");
    public static readonly string CapturesDir = Path.Combine(Dir, "captures");
    public static readonly string LogPath = Path.Combine(Dir, "atc_watcher.log");

    /// <summary>Screen rectangle of the ATC panel, in physical pixels, virtual-screen coordinates.</summary>
    public RegionRect? Region { get; set; }

    /// <summary>Career callsign, e.g. "N172SP" or "Speedbird NAO9210".</summary>
    public string Callsign { get; set; } = "";

    public double ScanInterval { get; set; } = 10.0;
    public int ConfirmScans { get; set; } = 1;
    public double MinPressInterval { get; set; } = 4.0;
    public double PostPressDelay { get; set; } = 2.5;
    public int OcrScale { get; set; } = 2;
    public int HeartbeatInterval { get; set; } = 60;
    public string SimWindowTitleContains { get; set; } = "Flight Simulator";

    /// <summary>
    /// When another window is active, briefly bring the sim to the front to press the key, then
    /// give focus back. Off = wait until the sim is active on its own (calls may be missed).
    /// </summary>
    public bool BringSimToFront { get; set; } = true;

    public Dictionary<string, string> OptionKeys { get; set; } = new()
    {
        ["1"] = "1", ["2"] = "2", ["3"] = "3", ["4"] = "4", ["5"] = "5",
        ["6"] = "6", ["7"] = "7", ["8"] = "8", ["9"] = "9", ["0"] = "0",
    };

    public string ToggleHotkey { get; set; } = "ctrl+alt+a";
    public bool ArmedOnStart { get; set; } = true;
    public bool DryRun { get; set; }
    public bool SaveTriggerCaptures { get; set; } = true;
    public bool StartMinimized { get; set; }

    public List<string> AllowPatterns { get; set; } = DefaultAllowPatterns();
    public List<string> DenyPatterns { get; set; } = DefaultDenyPatterns();
    public List<ContextRule> ContextRules { get; set; } = DefaultContextRules();

    public static List<ContextRule> DefaultContextRules() => new()
    {
        new(@"^cancel ifr\b", @"cancel\W{0,3}(your |the )?ifr|ifr\W.{0,30}cancel|continue vfr", "ATC said we may cancel IFR"),
        // Start of an IFR flight: always take the clearance when it is offered. Empty context = always.
        new(@"^request ifr clearance\b", "", "IFR flight plan loaded; get the clearance"),
        // Only after IFR has just ended (the panel also offers "Retry With Last IFR Flight Plan").
        new(@"^request flight following\b", @"retry with last ifr", "IFR just ended; pick up flight following"),
    };

    public static List<string> DefaultAllowPatterns() => new()
    {
        @"^(roger|wilco|affirm|affirmative|acknowledge[d]?|cop(y|ied)|understood|read\s?back)\b",
        // Handoff readback or tuning to the new frequency.
        @"\b(contact|switch(ing)?( to)?|monitor|tune|set)\b.*\b1\d{2}[.,]\d{1,3}\b",
        // Handoff readback whose frequency wrapped onto the next line: "Contact Salt Lake Center".
        @"^(contact|switch(ing)?( to)?|monitor|tune)\b.*\b(center|centre|approach|departure|tower|ground|delivery|radio|control|unicom|clearance|director|radar)\b",
        // Check-in with the new controller: "Salt Lake Center, Speedbird NAO9210, 12,000 ft."
        @"^[a-z .'\-]+\b(center|centre|approach|departure|tower|ground|delivery|radio|control|unicom|clearance|director|radar)\b.*\b(\d{1,2},?\d{3}\s*(ft|feet)|fl\s?\d{2,3}|flight level|with you|level)\b",
        @"^(climb|descend|maintain|fly heading|turn (left|right)|proceed direct|cleared|expect|squawk|reduce speed|increase speed|resume own navigation|radar contact|altimeter|cross|continue|hold|taxi|line up|position and hold|frequency change approved)\b",
    };

    public static List<string> DefaultDenyPatterns() => new()
    {
        @"\b(request|cancel|nearest|declare|emergency|say again|unable|ask|file|close|flight following|abort|divert|stay with|check in|ready for|ready to|atis|awos|asos)\b",
        // "change" is only OK when a frequency follows (handled by the allow list).
        @"\bchange\b(?!.*\b1\d{2}[.,]\d{1,3}\b)",
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOpts) ?? new Settings();
        }
        catch (Exception ex)
        {
            AppLog.Write($"Could not read settings ({ex.Message}); using defaults.");
        }
        return new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
    }

    public Settings Clone() =>
        JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(this, JsonOpts), JsonOpts)!;
}
