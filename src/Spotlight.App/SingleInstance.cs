namespace Spotlight.App;

/// <summary>One running copy per user session; later launches ask it to show its window.</summary>
internal static class SingleInstance
{
    private const string Name = "SpotlightWin.7c1f3c2e";
    private static Mutex? _mutex;
    private static EventWaitHandle? _show;

    public static bool TryAcquire()
    {
        _mutex = new Mutex(true, Name + ".mutex", out bool created);
        return created;
    }

    public static void SignalExisting()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (EventWaitHandle.TryOpenExisting(Name + ".show", out var h)) { h.Set(); h.Dispose(); }
        }
        catch (Exception e) when (e is PlatformNotSupportedException or IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Calls <paramref name="onShow"/> (on a background thread) whenever another launch signals us.</summary>
    public static void ListenForShow(Action onShow)
    {
        try { _show = new EventWaitHandle(false, EventResetMode.AutoReset, Name + ".show"); }
        catch (PlatformNotSupportedException) { return; } // named events are Windows-only
        var t = new Thread(() => { while (_show.WaitOne()) onShow(); }) { IsBackground = true, Name = "SingleInstance" };
        t.Start();
    }
}
