namespace TecLink.App;

internal static class Logger
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetEnvironmentVariable("TECLINK_LOGS") ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TecLink", "error.log");

    private static readonly string InfoPath = Path.Combine(
        Environment.GetEnvironmentVariable("TECLINK_LOGS") ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TecLink", "connection.log");
    private static readonly object InfoLock = new();

    /// <summary>One line per connection event (who joined, why a link dropped…), kept small.</summary>
    public static void Info(string text)
    {
        try
        {
            lock (InfoLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(InfoPath)!);
                if (File.Exists(InfoPath) && new FileInfo(InfoPath).Length > 200_000) File.Delete(InfoPath);
                File.AppendAllText(InfoPath, $"[{DateTime.Now:HH:mm:ss}] {text}\n");
            }
        }
        catch { /* best effort */ }
    }

    public static void LogError(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 300_000) File.Delete(LogPath); // never grows without limit
            File.AppendAllText(LogPath, $"[{DateTime.Now:O}] {ex}\n\n");
        }
        catch
        {
            // best-effort logging; nothing else to do if even this fails
        }
    }
}
