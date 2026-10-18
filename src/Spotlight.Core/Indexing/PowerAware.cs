using System.Runtime.InteropServices;

namespace Spotlight.Core.Indexing;

/// <summary>Windows power helpers that keep the indexer from costing battery or competing with
/// the foreground app. Every method is a no-op on other platforms.</summary>
internal static partial class PowerAware
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct THREAD_POWER_THROTTLING_STATE
    {
        public uint Version, ControlMask, StateMask;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentThread();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetThreadPriority(IntPtr thread, int priority);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetThreadInformation(IntPtr thread, int infoClass, ref THREAD_POWER_THROTTLING_STATE info, int size);

    private const int THREAD_MODE_BACKGROUND_BEGIN = 0x00010000;
    private const int ThreadPowerThrottling = 3;
    private const uint THREAD_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    /// <summary>True when a laptop is running on battery.</summary>
    public static bool OnBattery()
    {
        if (!OperatingSystem.IsWindows()) return false;
        return GetSystemPowerStatus(out var s) && s.ACLineStatus == 0;
    }

    /// <summary>Puts the calling thread into background mode (lowest CPU, I/O and memory priority)
    /// and opts it into EcoQoS so Windows 11 schedules it on efficiency cores at low clock speeds.</summary>
    public static void MakeCurrentThreadBackground()
    {
        Thread.CurrentThread.Priority = ThreadPriority.Lowest;
        if (!OperatingSystem.IsWindows()) return;
        var t = GetCurrentThread();
        SetThreadPriority(t, THREAD_MODE_BACKGROUND_BEGIN);
        var state = new THREAD_POWER_THROTTLING_STATE
        {
            Version = 1,
            ControlMask = THREAD_POWER_THROTTLING_EXECUTION_SPEED,
            StateMask = THREAD_POWER_THROTTLING_EXECUTION_SPEED,
        };
        SetThreadInformation(t, ThreadPowerThrottling, ref state, Marshal.SizeOf<THREAD_POWER_THROTTLING_STATE>());
    }
}
