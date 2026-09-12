namespace AtcWatcher.Core;

/// <summary>Append-only log file plus an event the UI can subscribe to.</summary>
public static class AppLog
{
    private static readonly object Gate = new();
    public static event Action<string>? Line;

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Settings.Dir);
                File.AppendAllText(Settings.LogPath, line + Environment.NewLine);
            }
            catch
            {
                // Logging must never take the watcher down.
            }
        }
        Line?.Invoke(line);
    }
}
