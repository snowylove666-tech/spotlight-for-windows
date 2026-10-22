namespace Spotlight.Core;

/// <summary>Appends unexpected errors to %AppData%\SpotlightWin\error.log (kept under 1 MB), so a
/// problem on someone's PC can be diagnosed from the file instead of guessed.</summary>
public static class ErrorLog
{
    private static readonly object Gate = new();

    public static string PathName => Path.Combine(Settings.AppDataDir, "error.log");

    public static void Write(string where, Exception e)
    {
        try
        {
            lock (Gate)
            {
                var info = new FileInfo(PathName);
                if (info.Exists && info.Length > 1024 * 1024) info.Delete();
                File.AppendAllText(PathName, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {where}: {e}\n\n");
            }
        }
        catch (Exception) { } // logging must never cause a second failure
    }
}
