using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using AtcWatcher.Core;
using Microsoft.Win32;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;

namespace AtcWatcher;

public sealed class OptionRow
{
    public string Number { get; init; } = "";
    public string Verdict { get; init; } = "";
    public string Text { get; init; } = "";
    public string Reason { get; init; } = "";
}

public sealed class HistoryRow
{
    public string Time { get; init; } = "";
    public string Text { get; init; } = "";
    public string Key { get; init; } = "";
    public string? Path { get; init; }
    public Visibility HasPath => Path is null ? Visibility.Collapsed : Visibility.Visible;
}

public sealed class VerdictBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        "PRESS" => new SolidColorBrush(Color.FromRgb(0x37, 0xC8, 0x71)),
        "DENY" => new SolidColorBrush(Color.FromRgb(0xE5, 0x53, 0x3D)),
        _ => new SolidColorBrush(Color.FromRgb(0x8B, 0x95, 0xA5)),
    };
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public partial class MainWindow : Window
{
    private const int HotkeyId = 0xA7C;
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "AtcWatcher";

    private Settings _settings = Settings.Load();
    private WindowsOcr? _ocr;
    private Watcher? _watcher;
    private TrayIcon? _tray;
    private HwndSource? _hwnd;
    private bool _reallyExit;
    private readonly ObservableCollection<OptionRow> _options = new();
    private readonly ObservableCollection<HistoryRow> _history = new();
    private readonly List<string> _logLines = new();

    public MainWindow()
    {
        InitializeComponent();
        OptionsList.ItemsSource = _options;
        HistoryList.ItemsSource = _history;
        AppLog.Line += line => Dispatcher.BeginInvoke(() => AppendLog(line));
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _ocr = new WindowsOcr();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "ATC Watcher", MessageBoxButton.OK, MessageBoxImage.Error);
            System.Windows.Application.Current.Shutdown();
            return;
        }

        _watcher = new Watcher(_ocr, _settings);
        _watcher.ArmedChanged += armed => Dispatcher.BeginInvoke(() => UpdateArmedUi(armed));
        _watcher.OptionsChanged += v => Dispatcher.BeginInvoke(() => ShowVerdicts(v));
        _watcher.Pressed += p => Dispatcher.BeginInvoke(() => OnPressed(p));
        _watcher.Status += s => Dispatcher.BeginInvoke(() => StatusText.Text = s);

        _tray = new TrayIcon();
        _tray.ShowRequested += () => { Show(); WindowState = WindowState.Normal; Activate(); };
        _tray.ToggleRequested += () => { if (_watcher is not null) _watcher.Armed = !_watcher.Armed; };
        _tray.ExitRequested += () => { _reallyExit = true; Close(); };

        PopulateFromSettings();
        UpdateArmedUi(_watcher.Armed);
        AppLog.Write($"ATC Watcher started (OCR language {_ocr.LanguageTag})");
        _watcher.Start();

        if (_settings.StartMinimized) Hide();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = (HwndSource)PresentationSource.FromVisual(this)!;
        _hwnd.AddHook(WndProc);
        RegisterHotkey();
    }

    private void RegisterHotkey()
    {
        if (_hwnd is null) return;
        Hotkeys.UnregisterHotKey(_hwnd.Handle, HotkeyId);
        if (string.IsNullOrWhiteSpace(_settings.ToggleHotkey)) return;
        try
        {
            var (mods, vk) = Hotkeys.Parse(_settings.ToggleHotkey);
            if (!Hotkeys.RegisterHotKey(_hwnd.Handle, HotkeyId, mods, vk))
                AppLog.Write($"Hotkey {_settings.ToggleHotkey} is already in use by another program; arm/disarm from the window or tray instead.");
        }
        catch (FormatException ex)
        {
            AppLog.Write(ex.Message);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Hotkeys.WM_HOTKEY && wParam.ToInt32() == HotkeyId && _watcher is not null)
        {
            _watcher.Armed = !_watcher.Armed;
            handled = true;
        }
        return IntPtr.Zero;
    }

    // ---------------------------------------------------------------- UI state

    private void PopulateFromSettings()
    {
        RegionText.Text = _settings.Region is null
            ? "Not set yet. Click “Find it for me” while the ATC panel is open."
            : $"Watching a {_settings.Region} area of the screen.";
        CallsignBox.Text = _settings.Callsign;
        IntervalBox.Text = _settings.ScanInterval.ToString(CultureInfo.InvariantCulture);
        MinGapBox.Text = _settings.MinPressInterval.ToString(CultureInfo.InvariantCulture);
        HotkeyBox.Text = _settings.ToggleHotkey;
        TitleBox.Text = _settings.SimWindowTitleContains;
        DryRunBox.IsChecked = _settings.DryRun;
        StartMinBox.IsChecked = _settings.StartMinimized;
        AutostartBox.IsChecked = IsAutostartEnabled();
    }

    private void UpdateArmedUi(bool armed)
    {
        ArmButton.Content = armed ? "ARMED" : "DISARMED";
        var brush = (Brush)FindResource(armed ? "Green" : "FgDim");
        ArmButton.Background = brush;
        ArmButton.BorderBrush = brush;
        _tray?.SetArmed(armed);
        if (!armed) StatusText.Text = "Disarmed. Nothing will be pressed.";
    }

    private void ShowVerdicts(IReadOnlyList<Classified> verdicts)
    {
        _options.Clear();
        foreach (var v in verdicts)
            _options.Add(new OptionRow
            {
                Number = v.Option.Number,
                Verdict = v.Verdict.ToString().ToUpperInvariant(),
                Text = v.Option.Text,
                Reason = v.Reason,
            });
    }

    private void OnPressed(PressEvent p)
    {
        _history.Insert(0, new HistoryRow
        {
            Time = p.Time.ToString("HH:mm:ss"),
            Text = (p.DryRun ? "(practice) " : "") + p.Option.Text,
            Key = p.Key,
            Path = p.CapturePath,
        });
        while (_history.Count > 100) _history.RemoveAt(_history.Count - 1);
    }

    private void AppendLog(string line)
    {
        _logLines.Add(line);
        if (_logLines.Count > 300) _logLines.RemoveAt(0);
        LogBox.Text = string.Join(Environment.NewLine, _logLines);
        LogBox.ScrollToEnd();
    }

    private void ApplySettings()
    {
        _settings.Save();
        if (_watcher is not null) _watcher.Settings = _settings.Clone();
    }

    // ---------------------------------------------------------------- Handlers

    private void ArmButton_Click(object sender, RoutedEventArgs e)
    {
        if (_watcher is not null) _watcher.Armed = !_watcher.Armed;
    }

    private async void FindButton_Click(object sender, RoutedEventArgs e)
    {
        if (_ocr is null) return;
        FindButton.IsEnabled = false;
        try
        {
            StatusText.Text = "Looking for the ATC panel on all monitors…";
            var result = await PanelFinder.FindAsync(_ocr, msg => Dispatcher.BeginInvoke(() => StatusText.Text = msg));
            if (result is null)
            {
                RegionText.Text = "Couldn't find the numbered ATC replies on any screen. Make sure the ATC panel is open and pinned in the sim, then try again, or use “Select manually”.";
                StatusText.Text = "Panel not found";
                return;
            }
            _settings.Region = RegionRect.From(result.Region);
            ApplySettings();
            RegionText.Text = $"Found it on {result.ScreenName} ({result.OptionCount} replies visible). Watching a {_settings.Region} area.";
            AppLog.Write($"Panel found on {result.ScreenName}: {_settings.Region}");
            await RunTestAsync();
        }
        catch (Exception ex)
        {
            AppLog.Write($"Find failed: {ex.Message}");
            StatusText.Text = "Find failed (see log)";
        }
        finally
        {
            FindButton.IsEnabled = true;
        }
    }

    private async void SelectButton_Click(object sender, RoutedEventArgs e)
    {
        var picked = RegionSelectForm.Pick();
        if (picked is null) return;
        _settings.Region = RegionRect.From(picked.Value);
        ApplySettings();
        RegionText.Text = $"Watching a {_settings.Region} area of the screen.";
        AppLog.Write($"Panel region set manually: {_settings.Region}");
        await RunTestAsync();
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e) => await RunTestAsync();

    private async Task RunTestAsync()
    {
        if (_watcher is null) return;
        if (_settings.Region is null)
        {
            StatusText.Text = "Set the panel location first.";
            return;
        }
        TestButton.IsEnabled = false;
        try
        {
            var scan = await _watcher.ScanOnceAsync(_settings.Clone());
            ShowVerdicts(scan.Verdicts);
            var chosen = scan.Verdicts.FirstOrDefault(v => v.Verdict == Verdict.Press);
            string path;
            try { path = Watcher.SaveCapture(scan.Image, "test"); }
            finally { scan.Image.Dispose(); }
            if (scan.Options.Count == 0)
            {
                StatusText.Text = scan.Lines.Count == 0
                    ? "Test: nothing readable in that area. Is the panel visible there?"
                    : "Test: text found but no numbered replies. Open the ATC menu so replies are showing.";
            }
            else
            {
                StatusText.Text = chosen is null
                    ? $"Test: {scan.Options.Count} replies read, none need answering. Looks good."
                    : $"Test: would press {_settings.OptionKeys.GetValueOrDefault(chosen.Option.Number, "?")} for “{chosen.Option.Text}”.";
            }
            AppLog.Write($"Test scan: {scan.Lines.Count} lines, {scan.Options.Count} replies, capture {path}");
            if (string.IsNullOrWhiteSpace(_settings.Callsign))
            {
                var guess = CallsignDetector.Detect(scan.Lines.Select(l => l.Text));
                if (guess is not null)
                {
                    CallsignBox.Text = guess;
                    _settings.Callsign = guess;
                    ApplySettings();
                    AppLog.Write($"Callsign detected: {guess}");
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"Test failed: {ex.Message}");
            StatusText.Text = "Test failed (see log)";
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void CallsignBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var v = CallsignBox.Text.Trim();
        if (v == _settings.Callsign) return;
        _settings.Callsign = v;
        ApplySettings();
        AppLog.Write($"Callsign set to '{v}'");
    }

    private async void DetectCallsign_Click(object sender, RoutedEventArgs e)
    {
        if (_watcher is null || _settings.Region is null)
        {
            StatusText.Text = "Set the panel location first.";
            return;
        }
        try
        {
            var scan = await _watcher.ScanOnceAsync(_settings.Clone());
            scan.Image.Dispose();
            var guess = CallsignDetector.Detect(scan.Lines.Select(l => l.Text));
            if (guess is null)
            {
                StatusText.Text = "Couldn't spot a callsign. Wait for ATC to say something, then try again, or type it in.";
                return;
            }
            CallsignBox.Text = guess;
            _settings.Callsign = guess;
            ApplySettings();
            StatusText.Text = $"Callsign set to {guess}. Edit it if that's not quite right.";
        }
        catch (Exception ex)
        {
            AppLog.Write($"Detect failed: {ex.Message}");
        }
    }

    private void Advanced_LostFocus(object sender, RoutedEventArgs e)
    {
        if (double.TryParse(IntervalBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var iv) && iv >= 0.5)
            _settings.ScanInterval = iv;
        if (double.TryParse(MinGapBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var gap) && gap >= 0)
            _settings.MinPressInterval = gap;
        var hk = HotkeyBox.Text.Trim();
        if (hk != _settings.ToggleHotkey)
        {
            _settings.ToggleHotkey = hk;
            RegisterHotkey();
        }
        _settings.SimWindowTitleContains = TitleBox.Text.Trim();
        _settings.DryRun = DryRunBox.IsChecked == true;
        _settings.StartMinimized = StartMinBox.IsChecked == true;
        ApplySettings();
        PopulateFromSettings();
    }

    private void Autostart_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (AutostartBox.IsChecked == true)
                key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue(RunValue, false);
        }
        catch (Exception ex)
        {
            AppLog.Write($"Could not change launch-with-Windows: {ex.Message}");
        }
    }

    private static bool IsAutostartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string;
    }

    private void EditRules_Click(object sender, RoutedEventArgs e)
    {
        _settings.Save();
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Settings.FilePath}\"") { UseShellExecute = true });
    }

    private void ReloadRules_Click(object sender, RoutedEventArgs e)
    {
        var fresh = Settings.Load();
        fresh.Region ??= _settings.Region;
        _settings = fresh;
        ApplySettings();
        PopulateFromSettings();
        RegisterHotkey();
        AppLog.Write("Settings reloaded from file");
    }

    private void ResetRules_Click(object sender, RoutedEventArgs e)
    {
        _settings.AllowPatterns = Settings.DefaultAllowPatterns();
        _settings.DenyPatterns = Settings.DefaultDenyPatterns();
        ApplySettings();
        AppLog.Write("Reply rules reset to defaults");
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Settings.CapturesDir);
        Process.Start(new ProcessStartInfo("explorer.exe", Settings.Dir) { UseShellExecute = true });
    }

    private void ViewCapture_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path } && File.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_reallyExit)
        {
            e.Cancel = true;
            Hide();
            _tray?.Balloon("ATC Watcher is still running", "Double-click the tray icon to open it again.");
            return;
        }
        _watcher?.Stop();
        _tray?.Dispose();
        if (_hwnd is not null) Hotkeys.UnregisterHotKey(_hwnd.Handle, HotkeyId);
        AppLog.Write("ATC Watcher exited");
        System.Windows.Application.Current.Shutdown();
    }
}
