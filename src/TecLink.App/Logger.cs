namespace TecLink.App;

internal static class Logger
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TecLink", "error.log");

    public static void LogError(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:O}] {ex}\n\n");
        }
        catch
        {
            // best-effort logging; nothing else to do if even this fails
        }
    }
}
