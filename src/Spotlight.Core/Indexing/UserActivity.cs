using System.Runtime.InteropServices;

namespace Spotlight.Core.Indexing;

/// <summary>Whether someone is using the computer right now (keyboard or mouse in the last 2 minutes).
/// Reading document text slows down while they are (see Indexer.Throttle).</summary>
public static class UserActivity
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    public static TimeSpan IdleFor
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return TimeSpan.MaxValue;
            var info = new LASTINPUTINFO { cbSize = 8 };
            if (!GetLastInputInfo(ref info)) return TimeSpan.MaxValue;
            return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime));
        }
    }

    public static bool Active => IdleFor < TimeSpan.FromMinutes(2);
}
