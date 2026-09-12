using System.Drawing;
using System.Drawing.Imaging;

namespace AtcWatcher.Core;

public sealed record PressEvent(DateTime Time, AtcOption Option, string Key, string? CapturePath, bool DryRun);

public sealed record ScanResult(List<OcrLine> Lines, List<AtcOption> Options, List<Classified> Verdicts, Bitmap Image);

/// <summary>Snapshot of everything the watcher needs to be working, refreshed after every scan.</summary>
public sealed record Health(
    bool SimFound, string SimTitle,
    bool RegionSet, int LinesRead, int OptionsRead, DateTime? LastScan,
    bool CallsignSet, string Callsign, bool CallsignSeenNow, DateTime? CallsignLastSeen,
    bool Armed, bool DryRun, DateTime? LastPress);

/// <summary>The watch loop. Same behaviour as the Python script, exposed through events for the UI.</summary>
public sealed class Watcher : IDisposable
{
    private readonly WindowsOcr _ocr;
    private CancellationTokenSource? _cts;
    private Task? _task;
    private volatile Settings _settings;
    private volatile bool _armed;

    public Watcher(WindowsOcr ocr, Settings settings)
    {
        _ocr = ocr;
        _settings = settings;
        _armed = settings.ArmedOnStart;
    }

    public Settings Settings
    {
        get => _settings;
        set => _settings = value;
    }

    public bool Armed
    {
        get => _armed;
        set
        {
            if (_armed == value) return;
            _armed = value;
            AppLog.Write(value ? "ARMED" : "DISARMED");
            ArmedChanged?.Invoke(value);
        }
    }

    public bool IsRunning => _task is { IsCompleted: false };
    public int Scans { get; private set; }
    public DateTime? LastPress { get; private set; }

    public event Action<bool>? ArmedChanged;
    public event Action<IReadOnlyList<Classified>>? OptionsChanged;
    public event Action<PressEvent>? Pressed;
    public event Action<string>? Status;
    public event Action<int, int, DateTime?>? Heartbeat;
    public event Action<Health>? HealthChanged;

    private DateTime? _callsignLastSeen;

    /// <summary>Health without a fresh scan (used before the first scan and when no region is set).</summary>
    public Health CurrentHealth(Settings s, int linesRead = 0, int optionsRead = 0, DateTime? lastScan = null, bool callsignSeenNow = false)
    {
        var needle = s.SimWindowTitleContains ?? "";
        var sim = needle.Length == 0 ? IntPtr.Zero : InputSender.FindWindowByTitle(needle);
        return new Health(
            sim != IntPtr.Zero, sim == IntPtr.Zero ? "" : InputSender.TitleOf(sim),
            s.Region is not null, linesRead, optionsRead, lastScan,
            !string.IsNullOrWhiteSpace(s.Callsign), s.Callsign ?? "", callsignSeenNow, _callsignLastSeen,
            _armed, s.DryRun, LastPress);
    }

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _task = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _task?.Wait(2000); } catch { /* cancelled */ }
        _task = null;
    }

    /// <summary>One capture + OCR + classification. Used by the loop and by the "Test now" button.</summary>
    public async Task<ScanResult> ScanOnceAsync(Settings s)
    {
        if (s.Region is null) throw new InvalidOperationException("No ATC panel region is set.");
        var image = ScreenCapture.Capture(s.Region.ToRectangle());
        using var scaled = ScreenCapture.Upscale(image, s.OcrScale);
        var lines = await _ocr.ReadAsync(scaled).ConfigureAwait(false);
        var texts = lines.Select(l => l.Text).ToList();
        var options = OptionParser.Parse(texts);
        var (_, verdicts) = new Decider(s).Choose(options, texts);
        return new ScanResult(lines, options, verdicts, image);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        string? lastPressedKey = null;
        var lastPressTime = DateTime.MinValue;
        string? candidateKey = null;
        var candidateCount = 0;
        string? lastSignature = null;
        var lastHeartbeat = DateTime.Now;

        Status?.Invoke("Watching");
        while (!ct.IsCancellationRequested)
        {
            var s = _settings;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.2, s.ScanInterval)), ct);
                if (s.Region is null)
                {
                    Status?.Invoke("No panel region set");
                    HealthChanged?.Invoke(CurrentHealth(s));
                    continue;
                }

                // Scan even when disarmed so the live view and health check stay current; only press when armed.
                var scan = await ScanOnceAsync(s);
                Scans++;
                var opts = scan.Options;
                var verdicts = scan.Verdicts;
                var chosen = verdicts.FirstOrDefault(v => v.Verdict == Verdict.Press)?.Option;

                var seenNow = !string.IsNullOrWhiteSpace(s.Callsign) && CallsignDetector.IsSeen(scan.Lines.Select(l => l.Text), s.Callsign);
                if (seenNow) _callsignLastSeen = DateTime.Now;
                HealthChanged?.Invoke(CurrentHealth(s, scan.Lines.Count, opts.Count, DateTime.Now, seenNow));

                if (!_armed)
                {
                    if (chosen is not null) Status?.Invoke($"Disarmed. Would have answered [{chosen.Number}] {chosen.Text}");
                    else Status?.Invoke("Disarmed");
                    scan.Image.Dispose();
                    continue;
                }

                var signature = string.Join("\n", opts.Select(o => o.Number + "|" + o.Text));
                if (signature != lastSignature)
                {
                    lastSignature = signature;
                    if (opts.Count > 0)
                    {
                        AppLog.Write("Panel options changed:");
                        foreach (var v in verdicts)
                            AppLog.Write($"   [{v.Option.Number}] {v.Verdict.ToString().ToUpperInvariant(),-5} {v.Option.Text}  ({v.Reason})");
                        var optionRaws = opts.Select(o => o.Raw).ToHashSet();
                        var history = scan.Lines.Select(l => l.Text).Where(t => !optionRaws.Contains(t)).TakeLast(4).ToList();
                        if (history.Count > 0) AppLog.Write($"   panel text: {string.Join(" | ", history)}");
                    }
                    else AppLog.Write("Panel shows no numbered options");
                    OptionsChanged?.Invoke(verdicts);
                }

                if (s.HeartbeatInterval > 0 && (DateTime.Now - lastHeartbeat).TotalSeconds >= s.HeartbeatInterval)
                {
                    lastHeartbeat = DateTime.Now;
                    AppLog.Write($"Still watching: {Scans} scans, {opts.Count} options on screen, last press {(LastPress?.ToString("HH:mm:ss") ?? "none yet")}");
                    Heartbeat?.Invoke(Scans, opts.Count, LastPress);
                }

                var now = DateTime.Now;
                if (chosen is null)
                {
                    candidateKey = null; candidateCount = 0;
                    Status?.Invoke(opts.Count == 0 ? "Watching (no options visible)" : "Watching (nothing to answer)");
                    scan.Image.Dispose();
                    continue;
                }

                var key = Fuzzy.Normalize(chosen.Text);
                if (key == lastPressedKey && (now - lastPressTime).TotalSeconds < s.MinPressInterval * 3)
                {
                    Status?.Invoke("Answered; waiting for the panel to clear");
                    scan.Image.Dispose();
                    continue;
                }

                if (key != candidateKey) { candidateKey = key; candidateCount = 1; }
                else candidateCount++;
                if (candidateCount < Math.Max(1, s.ConfirmScans))
                {
                    Status?.Invoke($"Confirming [{chosen.Number}] {chosen.Text}");
                    scan.Image.Dispose();
                    continue;
                }

                if ((now - lastPressTime).TotalSeconds < s.MinPressInterval)
                {
                    scan.Image.Dispose();
                    continue;
                }

                if (!s.OptionKeys.TryGetValue(chosen.Number, out var keyName) || !InputSender.IsValidKeyName(keyName))
                {
                    AppLog.Write($"No valid key mapped for option {chosen.Number}; check the key bindings in settings");
                    scan.Image.Dispose();
                    continue;
                }

                var dry = s.DryRun;
                var title = InputSender.ForegroundWindowTitle();
                var needle = s.SimWindowTitleContains ?? "";
                var simInFront = needle.Length == 0 || title.Contains(needle, StringComparison.OrdinalIgnoreCase);
                var previous = IntPtr.Zero;
                if (!simInFront && !dry)
                {
                    if (s.BringSimToFront)
                    {
                        var sim = InputSender.FindWindowByTitle(needle);
                        if (sim != IntPtr.Zero)
                        {
                            previous = InputSender.ForegroundWindowHandle();
                            simInFront = InputSender.Activate(sim);
                            AppLog.Write(simInFront
                                ? $"Brought the sim to the front (you were in '{title}')"
                                : $"Could not bring the sim to the front (active window '{title}')");
                        }
                        else AppLog.Write($"No window with '{needle}' in its title; is the sim running?");
                    }
                    if (!simInFront)
                    {
                        AppLog.Write($"Would answer [{chosen.Number}] {chosen.Text} but the sim is not in the foreground ('{title}')");
                        Status?.Invoke("Sim is not the active window; waiting");
                        scan.Image.Dispose();
                        continue;
                    }
                }

                AppLog.Write(dry
                    ? $"DRY RUN: would press '{keyName}' for [{chosen.Number}] {chosen.Text}"
                    : $"Answering ATC: pressing '{keyName}' for [{chosen.Number}] {chosen.Text}");
                if (!dry) InputSender.Press(keyName);
                if (previous != IntPtr.Zero)
                {
                    Thread.Sleep(150);
                    InputSender.Activate(previous);   // hand focus back to what the user was doing
                }

                string? capturePath = null;
                if (s.SaveTriggerCaptures)
                {
                    try { capturePath = SaveCapture(scan.Image, $"press{chosen.Number}"); }
                    catch (Exception ex) { AppLog.Write($"Could not save capture: {ex.Message}"); }
                }
                scan.Image.Dispose();

                lastPressedKey = key; lastPressTime = now; LastPress = now;
                candidateKey = null; candidateCount = 0;
                Pressed?.Invoke(new PressEvent(now, chosen, keyName, capturePath, dry));
                Status?.Invoke($"Answered [{chosen.Number}] {chosen.Text}");
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, s.PostPressDelay)), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                AppLog.Write($"Scan failed; continuing. {ex.GetType().Name}: {ex.Message}");
                Status?.Invoke("Error during scan (see log)");
                try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { break; }
            }
        }
        Status?.Invoke("Stopped");
    }

    public static string SaveCapture(Bitmap image, string tag)
    {
        Directory.CreateDirectory(Settings.CapturesDir);
        var path = Path.Combine(Settings.CapturesDir, $"{DateTime.Now:yyyyMMdd-HHmmss}_{tag}.png");
        image.Save(path, ImageFormat.Png);
        // Keep the newest 50.
        foreach (var old in new DirectoryInfo(Settings.CapturesDir).GetFiles("*.png")
                     .OrderByDescending(f => f.LastWriteTimeUtc).Skip(50))
        {
            try { old.Delete(); } catch { /* ignore */ }
        }
        return path;
    }

    public void Dispose() => Stop();
}
