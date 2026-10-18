using System.Runtime;
using System.Runtime.InteropServices;

namespace Spotlight.Core;

/// <summary>
/// The launcher sits idle most of the day, so whenever it is hidden and the indexer has caught up we
/// give memory back: compact the managed heap, release unused heap segments, and empty the working
/// set. Pages come back on demand the next time the window opens (a few milliseconds).
/// </summary>
public static partial class MemoryTrimmer
{
    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    [LibraryImport("kernel32.dll", EntryPoint = "K32EmptyWorkingSet")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyWorkingSet(IntPtr process);

    private static readonly object Gate = new();

    public static void Trim()
    {
        lock (Gate)
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            if (OperatingSystem.IsWindows()) EmptyWorkingSet(GetCurrentProcess());
        }
    }
}
