namespace AtcWatcher.Core;

/// <summary>Append-only log file plus an event the UI can subscribe to.</summary>
public static class AppLog
{
    private static readonly object Gate = new();
    public static event Action<string>? Line;

    /// <summary>Set when the log file cannot be written, so the UI can say so instead of failing silently.</summary>
    public static string? LastError { get; private set; }

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Settings.Dir);
                File.AppendAllText(Settings.LogPath, line + Environment.NewLine);
                LastError = null;
            }
            catch (Exception ex)
            {
                // Logging must never take the watcher down, but it must not fail invisibly either.
                LastError = ex.Message;
                try { File.WriteAllText(Path.Combine(Settings.Dir, "atc_watcher.err"), ex.ToString()); }
                catch { /* nothing more we can do */ }
            }
        }
        Line?.Invoke(line);
    }
}
