using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Spotlight.App;

/// <summary>
/// Shows a file with the preview handler Windows uses in Explorer's preview pane (Word, Excel,
/// PowerPoint and whatever else is installed), so documents look like the original rather than a
/// thumbnail. The handler draws into a child window of ours.
///
/// Each preview runs on its own thread: Office's previewers live inside Word/Excel/PowerPoint, and
/// an Office program that is busy (a dialog open, a big recalculation) can keep the call waiting.
/// The search window never waits for it; after a few seconds it shows a thumbnail instead.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class PreviewHandlerHost : NativeControlHost
{
    private IntPtr _hwnd;
    private PreviewSession? _session;

    /// <summary>Handlers that did not answer in time; not tried again until the app restarts.</summary>
    private static readonly ConcurrentDictionary<Guid, bool> Unresponsive = new();

    public PreviewHandlerHost() => SizeChanged += (_, _) => _session?.Resize();

    /// <summary>The handler registered for this file type, if any (and it has been answering).</summary>
    public static Guid? HandlerFor(string path)
    {
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return null;
        uint size = 64;
        var sb = new System.Text.StringBuilder((int)size);
        // ASSOCF_INIT_DEFAULTTOSTAR | ASSOCF_NOTRUNCATE; ASSOCSTR_SHELLEXTENSION = 16
        if (AssocQueryString(0x4 | 0x20, 16, ext, PreviewHandlerIid, sb, ref size) != 0) return null;
        return Guid.TryParse(sb.ToString(), out var g) && !Unresponsive.ContainsKey(g) ? g : null;
    }

    public bool HasWindow => _hwnd != IntPtr.Zero;

    /// <summary>Shows the file. Completes with false when its handler is missing, fails or takes too
    /// long (the caller falls back to a thumbnail).</summary>
    public async Task<bool> LoadAsync(string path)
    {
        Unload();
        if (_hwnd == IntPtr.Zero || HandlerFor(path) is not { } clsid) return false;
        var session = _session = new PreviewSession(clsid, path, _hwnd);
        var finished = await Task.WhenAny(session.Shown, Task.Delay(TimeSpan.FromSeconds(6)));
        if (finished == session.Shown && session.Shown.Result) return _session == session;
        if (finished != session.Shown) Unresponsive[clsid] = true;
        if (_session == session) Unload();
        return false;
    }

    public void Unload()
    {
        _session?.Close();
        _session = null;
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPCHILDREN = 0x02000000;
        _hwnd = CreateWindowEx(0, "STATIC", "", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN, 0, 0, 1, 1, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return new PlatformHandle(_hwnd, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        Unload();
        DestroyWindow(control.Handle);
        _hwnd = IntPtr.Zero;
    }

    /// <summary>True when the foreground window is <paramref name="topLevel"/> (a handler that took
    /// the keyboard focus does not make the search window inactive in that sense).</summary>
    public static bool IsForeground(IntPtr topLevel) =>
        topLevel != IntPtr.Zero && GetAncestor(GetForegroundWindow(), 2 /* GA_ROOT */) == topLevel;

    /// <summary>Gives the keyboard back to our window: handlers often focus themselves in DoPreview.</summary>
    public static void TakeFocusBack(IntPtr topLevel)
    {
        if (topLevel != IntPtr.Zero) SetFocus(topLevel);
    }

    /// <summary>
    /// While a document is on show, Office's previewer takes the keyboard focus again whenever it
    /// likes (after drawing, after a click into it). Then ↑/↓ and Ctrl+arrows went to Word instead
    /// of the result list. Called every few hundred ms: if the focus is on a window inside ours that
    /// is not ours, takes it back. Returns true when it did.
    /// </summary>
    public static bool KeepKeyboard(IntPtr topLevel)
    {
        if (topLevel == IntPtr.Zero || GetForegroundWindow() != topLevel) return false;
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (!GetGUIThreadInfo(GetWindowThreadProcessId(topLevel, out _), ref info)) return false;
        var focus = info.hwndFocus;
        if (focus == IntPtr.Zero || focus == topLevel || GetAncestor(focus, 2 /* GA_ROOT */) != topLevel) return false;
        uint me = GetCurrentThreadId(), owner = GetWindowThreadProcessId(focus, out _);
        bool attached = owner != me && AttachThreadInput(me, owner, true);
        SetFocus(topLevel);
        if (attached) AttachThreadInput(me, owner, false);
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint to, bool on);

    /// <summary>One file on show: a single-threaded-apartment thread that owns the handler and runs a
    /// message loop for it until closed.</summary>
    private sealed class PreviewSession
    {
        private const uint WM_QUIT = 0x0012, WM_APP_RESIZE = 0x8001;
        private readonly TaskCompletionSource<bool> _shown = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly IntPtr _hwnd;
        private uint _threadId;
        private volatile bool _closed;

        public Task<bool> Shown => _shown.Task;

        public PreviewSession(Guid clsid, string path, IntPtr hwnd)
        {
            _hwnd = hwnd;
            var t = new Thread(() => Run(clsid, path)) { IsBackground = true, Name = "Preview handler" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        public void Resize() { if (_threadId != 0) PostThreadMessage(_threadId, WM_APP_RESIZE, IntPtr.Zero, IntPtr.Zero); }

        public void Close()
        {
            _closed = true;
            if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        private void Run(Guid clsid, string path)
        {
            _threadId = GetCurrentThreadId();
            IPreviewHandler? handler = null;
            try
            {
                handler = Create(clsid, path);
                if (handler == null || _closed) { _shown.TrySetResult(false); return; }
                GetClientRect(_hwnd, out var rect);
                handler.SetWindow(_hwnd, ref rect);
                handler.DoPreview();
                _shown.TrySetResult(!_closed);
                while (!_closed && GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    if (msg.message == WM_APP_RESIZE && msg.hwnd == IntPtr.Zero)
                    {
                        GetClientRect(_hwnd, out rect);
                        handler.SetRect(ref rect);
                        continue;
                    }
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            }
            catch (Exception) { _shown.TrySetResult(false); }
            finally
            {
                if (handler != null)
                {
                    try { handler.Unload(); } catch (Exception) { }
                    try { Marshal.ReleaseComObject(handler); } catch (Exception) { }
                }
            }
        }

        private static IPreviewHandler? Create(Guid clsid, string path)
        {
            // Out of process (Explorer's way; Office's previewers live in the Office programs), in
            // process for handlers that only come as a DLL.
            var iid = typeof(IPreviewHandler).GUID;
            if (CoCreateInstance(clsid, IntPtr.Zero, CLSCTX_LOCAL_SERVER, iid, out var obj) != 0 &&
                CoCreateInstance(clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, iid, out obj) != 0)
                return null;
            const uint STGM_READ = 0;
            if (obj is IInitializeWithFile withFile) withFile.Initialize(path, STGM_READ);
            else if (obj is IInitializeWithStream withStream)
            {
                // STGM_SHARE_DENY_NONE: the document may be open in Word at the same time.
                SHCreateStreamOnFileEx(path, STGM_READ | 0x40, 0, false, IntPtr.Zero, out var stream);
                withStream.Initialize(stream, STGM_READ);
            }
            else { Marshal.ReleaseComObject(obj!); return null; }
            return (IPreviewHandler)obj;
        }
    }

    // ---------------------------------------------------------------- interop

    private const string PreviewHandlerIid = "{8895b1c6-b41f-4c1c-a562-0d564250836f}";
    private const uint CLSCTX_INPROC_SERVER = 0x1, CLSCTX_LOCAL_SERVER = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [ComImport, Guid("8895b1c6-b41f-4c1c-a562-0d564250836f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPreviewHandler
    {
        void SetWindow(IntPtr hwnd, ref RECT rect);
        void SetRect(ref RECT rect);
        void DoPreview();
        void Unload();
        void SetFocus();
        void QueryFocus(out IntPtr hwnd);
        [PreserveSig] int TranslateAccelerator(IntPtr msg);
    }

    [ComImport, Guid("b7d14566-0509-4cce-a71f-0a554233bd9b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithFile
    {
        void Initialize([MarshalAs(UnmanagedType.LPWStr)] string path, uint mode);
    }

    [ComImport, Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithStream
    {
        void Initialize(IStream stream, uint mode);
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance([MarshalAs(UnmanagedType.LPStruct)] Guid clsid, IntPtr outer, uint context,
        [MarshalAs(UnmanagedType.LPStruct)] Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object? obj);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryString(uint flags, int str, string assoc, string extra, System.Text.StringBuilder? output, ref uint size);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateStreamOnFileEx(string file, uint mode, uint attributes, bool create, IntPtr template, out IStream stream);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
