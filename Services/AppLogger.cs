namespace ClipboardManager.Services;

internal static class AppLogger
{
    private static readonly object Sync = new();
    private static string? _logPath;

    public static void Initialize()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClipboardManager");
        Directory.CreateDirectory(directory);
        _logPath = Path.Combine(directory, "clipboard-manager.log");
    }

    public static void Error(string message, Exception? exception = null)
    {
        try
        {
            if (_logPath is null)
                Initialize();

            var line = $"{DateTimeOffset.Now:O} ERROR {message}{Environment.NewLine}" +
                       (exception is null ? string.Empty : exception + Environment.NewLine);
            lock (Sync)
            {
                if (File.Exists(_logPath) && new FileInfo(_logPath).Length > 1_000_000)
                    File.Delete(_logPath);
                File.AppendAllText(_logPath!, line);
            }
        }
        catch
        {
            // Logging must never take down the clipboard listener.
        }
    }
}
