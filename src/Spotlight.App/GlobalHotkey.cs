using System.Runtime.InteropServices;

namespace Spotlight.App;

/// <summary>
/// System-wide hotkey through RegisterHotKey. The hotkey is registered on a dedicated thread with
/// no window: WM_HOTKEY then arrives in that thread's message queue, which a small GetMessage loop
/// drains. This keeps the hotkey working while the search window is hidden.
/// </summary>
internal sealed partial class GlobalHotkey : IDisposable
{
    private const int WM_HOTKEY = 0x0312, WM_QUIT = 0x0012, HotkeyId = 0x5370;
    private const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public UIntPtr wParam; public IntPtr lParam; public uint time; public int x, y; }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    private static partial int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);

    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessage(uint threadId, uint msg, UIntPtr w, IntPtr l);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    private Thread? _thread;
    private uint _threadId;

    /// <summary>Registers <paramref name="hotkey"/> ("Alt+Space", "Ctrl+Shift+F" ...). Returns false if
    /// the text is invalid or another program already owns that combination.</summary>
    public bool Register(string hotkey, Action pressed)
    {
        if (!OperatingSystem.IsWindows() || !TryParse(hotkey, out uint mods, out uint vk)) return false;
        var ready = new ManualResetEventSlim();
        bool ok = false;
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            ok = RegisterHotKey(IntPtr.Zero, HotkeyId, mods | MOD_NOREPEAT, vk);
            ready.Set();
            if (!ok) return;
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                if (msg.message == WM_HOTKEY) pressed();
            UnregisterHotKey(IntPtr.Zero, HotkeyId);
        }) { IsBackground = true, Name = "Global hotkey" };
        _thread.Start();
        ready.Wait();
        return ok;
    }

    internal static bool TryParse(string text, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "alt": mods |= MOD_ALT; break;
                case "ctrl" or "control": mods |= MOD_CONTROL; break;
                case "shift": mods |= MOD_SHIFT; break;
                case "win" or "windows": mods |= MOD_WIN; break;
                default:
                    if (vk != 0) return false;
                    vk = KeyCode(raw);
                    if (vk == 0) return false;
                    break;
            }
        }
        return vk != 0;
    }

    private static uint KeyCode(string key)
    {
        key = key.ToUpperInvariant();
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0])) return key[0];
        if (key.StartsWith('F') && int.TryParse(key[1..], out int f) && f is >= 1 and <= 24) return (uint)(0x6F + f);
        return key switch
        {
            "SPACE" => 0x20, "ENTER" or "RETURN" => 0x0D, "TAB" => 0x09, "ESC" or "ESCAPE" => 0x1B,
            "`" or "OEM3" => 0xC0, ";" => 0xBA, "/" => 0xBF, "." => 0xBE, "," => 0xBC, "INSERT" => 0x2D,
            _ => 0,
        };
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
    }
}
