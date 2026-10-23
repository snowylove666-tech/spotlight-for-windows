namespace Spotlight.Core;

/// <summary>Appends unexpected errors to %AppData%\SpotlightWin\error.log (kept under 1 MB), so a
/// problem on someone's PC can be diagnosed from the file instead of guessed.</summary>
public static class ErrorLog
{
    private static readonly object Gate = new();

    public static string PathName => Path.Combine(Settings.AppDataDir, "error.log");

    public static void Write(string where, Exception e) => Append($"{where}: {e}\n");

    /// <summary>A one-line event (started, quit, resumed…): when the app is found not running, the
    /// last lines show whether it was quit, crashed or never started.</summary>
    public static void Note(string text) => Append(text);

    private static void Append(string text)
    {
        try
        {
            lock (Gate)
            {
                var info = new FileInfo(PathName);
                if (info.Exists && info.Length > 1024 * 1024) info.Delete();
                File.AppendAllText(PathName, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}\n");
            }
        }
        catch (Exception) { } // logging must never cause a second failure
    }
}
