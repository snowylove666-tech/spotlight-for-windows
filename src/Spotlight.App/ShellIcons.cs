using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Media.Imaging;
using Spotlight.Core;

namespace Spotlight.App;

/// <summary>Fetches the real Windows shell icon for files (per extension, or per file for
/// programs and shortcuts) on a dedicated STA thread, with a cache.</summary>
internal static partial class ShellIcons
{
    private static readonly ConcurrentDictionary<string, Bitmap?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly BlockingCollection<(string key, string path, bool isDir, bool perFile, Action<Bitmap?> done)> Queue = new();
    private static Thread? _worker;

    public static void Request(string path, FileKind kind, Action<Bitmap?> done)
    {
        if (!OperatingSystem.IsWindows() || kind == FileKind.Calculator) { done(null); return; }
        string ext = Path.GetExtension(path).ToLowerInvariant();
        bool perFile = ext is ".exe" or ".lnk" or ".url" or ".appref-ms" or ".ico" or "";
        bool isDir = kind == FileKind.Folder;
        string key = isDir ? "<dir>" : perFile ? path : ext;
        if (Cache.TryGetValue(key, out var cached)) { done(cached); return; }
        EnsureWorker();
        Queue.Add((key, path, isDir, perFile && !isDir, done));
    }

    [SupportedOSPlatform("windows")]
    private static void EnsureWorker()
    {
        if (_worker != null) return;
        lock (Queue)
        {
            if (_worker != null) return;
            _worker = new Thread(Work) { IsBackground = true, Name = "Shell icons" };
            _worker.SetApartmentState(ApartmentState.STA);
            _worker.Start();
        }
    }

    [SupportedOSPlatform("windows")]
    private static void Work()
    {
        foreach (var (key, path, isDir, perFile, done) in Queue.GetConsumingEnumerable())
        {
            if (!Cache.TryGetValue(key, out var bmp))
            {
                bmp = Load(path, isDir, perFile);
                Cache[key] = bmp;
            }
            done(bmp);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attrs, ref SHFILEINFO info, uint size, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr h);

    private const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0, SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10, FILE_ATTRIBUTE_NORMAL = 0x80;

    [SupportedOSPlatform("windows")]
    private static Bitmap? Load(string path, bool isDir, bool perFile)
    {
        var info = new SHFILEINFO();
        uint flags = SHGFI_ICON | SHGFI_LARGEICON;
        string target = path;
        uint attrs = 0;
        if (!perFile)
        {
            // Ask by type only: no disk access, works for files that are offline (OneDrive).
            flags |= SHGFI_USEFILEATTRIBUTES;
            attrs = isDir ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
            target = isDir ? "folder" : "file" + Path.GetExtension(path);
        }
        if (SHGetFileInfo(target, attrs, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags) == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            return null;
        try
        {
            using var icon = System.Drawing.Icon.FromHandle(info.hIcon);
            using var gdi = icon.ToBitmap();
            using var ms = new MemoryStream();
            gdi.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;
            return new Bitmap(ms);
        }
        catch (Exception) { return null; }
        finally { DestroyIcon(info.hIcon); }
    }
}
