using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Spotlight.App;

/// <summary>Opening files, revealing them in Explorer, and the "start with Windows" switch.</summary>
internal static partial class Platform
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "SpotlightWin";

    public static void Open(string path)
    {
        LetNextWindowComeForward();
        if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        else Process.Start("xdg-open", path);
    }

    /// <summary>Opens a document at the place the query was found when its default program can be told
    /// where: SumatraPDF / Acrobat / Edge / Chrome jump to the page (Sumatra and Acrobat also search
    /// for the term); Notepad++ and VS Code jump to the line. Anything else opens normally.</summary>
    public static void OpenAt(string path, int? page, string? term)
    {
        if (!OperatingSystem.IsWindows()) { Open(path); return; }
        LetNextWindowComeForward();
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var exe = DefaultProgram(ext);
        var app = exe == null ? "" : Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
        string? args = null;
        term = string.IsNullOrWhiteSpace(term) ? null : term.Replace("\"", "");
        if (ext == ".pdf" && page is { } p)
        {
            args = app switch
            {
                "sumatrapdf" => $"-reuse-instance -page {p}{(term != null ? $" -search \"{term}\"" : "")} \"{path}\"",
                "acrobat" or "acrord32" => $"/A \"page={p}{(term != null ? "&search=" + term : "")}\" \"{path}\"",
                "msedge" or "chrome" => $"\"{new Uri(path).AbsoluteUri}#page={p}\"",
                _ => null,
            };
        }
        else if (term != null && app is "notepad++" or "code" && FirstLine(path, term) is { } line)
        {
            args = app == "code" ? $"-g \"{path}:{line}\"" : $"-n{line} \"{path}\"";
        }
        if (args == null || exe == null) { Open(path); return; }
        Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false });
    }

    /// <summary>1-based line of the first occurrence of <paramref name="term"/> in a text file.</summary>
    private static int? FirstLine(string path, string term)
    {
        try
        {
            if (new FileInfo(path).Length > 20 * 1024 * 1024) return null;
            int n = 0;
            foreach (var line in File.ReadLines(path))
            {
                n++;
                if (line.Contains(term, StringComparison.OrdinalIgnoreCase)) return n;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return null;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryString(int flags, int str, string assoc, string? extra, [Out] char[]? output, ref uint length);

    private const int ASSOCSTR_EXECUTABLE = 2;

    /// <summary>Full path of the program that opens files with this extension, or null.</summary>
    internal static string? DefaultProgram(string ext)
    {
        uint len = 1024;
        var buf = new char[len];
        if (AssocQueryString(0, ASSOCSTR_EXECUTABLE, ext, "open", buf, ref len) != 0) return null;
        var exe = new string(buf, 0, (int)Math.Max(0, len - 1));
        return File.Exists(exe) ? exe : null;
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2;

    /// <summary>Lets Windows 11 draw the rounded corners, border and shadow, so the window outline is
    /// drawn once (our own rounded border on top of Windows' produced a doubled edge at each corner).</summary>
    /// <summary>Asks Windows to start the app again if it crashes or stops responding (after it has
    /// run for a minute), and after a restart for updates.</summary>
    public static void RegisterForRestart(string args)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { RegisterApplicationRestart(args, 0); } catch (Exception) { }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterApplicationRestart(string commandLine, int flags);

    /// <summary>A plain Windows OK/Cancel question; true for OK.</summary>
    public static bool Confirm(string title, string text)
    {
        const uint MB_OKCANCEL = 0x1, MB_ICONQUESTION = 0x20, MB_SETFOREGROUND = 0x10000, MB_TOPMOST = 0x40000;
        return MessageBox(IntPtr.Zero, text, title, MB_OKCANCEL | MB_ICONQUESTION | MB_SETFOREGROUND | MB_TOPMOST) == 1;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(IntPtr hwnd, string text, string caption, uint type);

    public static bool RoundCorners(IntPtr hwnd)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) || hwnd == IntPtr.Zero) return false;
        int pref = DWMWCP_ROUND;
        return DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int)) == 0;
    }

    public static void Reveal(string path)
    {
        LetNextWindowComeForward();
        if (OperatingSystem.IsWindows()) Process.Start("explorer.exe", $"/select,\"{path}\"");
        else Process.Start("xdg-open", Path.GetDirectoryName(path) ?? path);
    }

    /// <summary>If start-up is on but points at another copy of the exe (an older version in another
    /// folder), point it at this one, so updating the app never leaves Windows starting the old one.</summary>
    public static void RefreshStartupEntry()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(RunValue) is string current && current != StartupCommand)
            key.SetValue(RunValue, StartupCommand);
    }

    /// <summary>Records where the index lives (HKCU\Software\SpotlightWin), so the uninstaller can
    /// delete it: reading that path back out of settings.json is beyond the installer's script.</summary>
    public static void RememberIndexFolder(string folder)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\SpotlightWin");
            key.SetValue("IndexFolder", folder);
        }
        catch (Exception) { }
    }

    /// <summary>Deletes "SpotlightWin.exe.old…" files next to the exe: setup renames the old exe
    /// when it is still running and cannot be deleted (see installer\SpotlightWin.iss).</summary>
    public static void DeleteOldCopies()
    {
        try
        {
            var exe = Environment.ProcessPath;
            var dir = exe == null ? null : Path.GetDirectoryName(exe);
            if (dir == null) return;
            foreach (var f in Directory.EnumerateFiles(dir, Path.GetFileName(exe) + ".old*"))
                try { File.Delete(f); } catch (Exception) { }
        }
        catch (Exception) { }
    }

    private static string StartupCommand => $"\"{Environment.ProcessPath}\" --background";

    public static bool StartsWithWindows
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }
        set
        {
            if (!OperatingSystem.IsWindows()) return;
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(RunValue, StartupCommand);
            else key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);

    /// <summary>
    /// Enter / Ctrl+Enter "did nothing" when the program was already running (Explorer, Word, a PDF
    /// reader reusing its window): the request went to that process, which Windows does not let
    /// take the foreground, so its window opened behind the others. While we are in front we may
    /// pass that right on to whoever opens the file.
    /// </summary>
    private static void LetNextWindowComeForward()
    {
        if (OperatingSystem.IsWindows()) AllowSetForegroundWindow(-1 /* ASFW_ANY */);
    }

    /// <summary>Brings our window to the front. Windows allows this right after our hotkey fired.</summary>
    public static void ForceForeground(IntPtr hwnd)
    {
        if (OperatingSystem.IsWindows() && hwnd != IntPtr.Zero) SetForegroundWindow(hwnd);
    }
}
