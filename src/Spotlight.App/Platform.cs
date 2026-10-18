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
        if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        else Process.Start("xdg-open", path);
    }

    public static void Reveal(string path)
    {
        if (OperatingSystem.IsWindows()) Process.Start("explorer.exe", $"/select,\"{path}\"");
        else Process.Start("xdg-open", Path.GetDirectoryName(path) ?? path);
    }

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
            if (value) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\" --background");
            else key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>Brings our window to the front. Windows allows this right after our hotkey fired.</summary>
    public static void ForceForeground(IntPtr hwnd)
    {
        if (OperatingSystem.IsWindows() && hwnd != IntPtr.Zero) SetForegroundWindow(hwnd);
    }
}
