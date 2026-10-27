using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace Spotlight.Core.Indexing;

/// <summary>
/// All names of one NTFS drive as read from its master file table by the SpotlightWin MFT service,
/// held in memory for one crawl. Listing a folder is then a lookup instead of a disk read, so a crawl
/// of a whole drive takes seconds. Built for one scan and dropped right after.
/// </summary>
public sealed class MftVolume
{
    private readonly List<MftReader.Entry> _entries;
    private readonly Dictionary<long, int> _first = new();
    private readonly HashSet<long> _folders = new();
    private readonly int[] _next;

    public string Volume { get; }
    public int Count => _entries.Count;
    public TimeSpan Took { get; }

    private MftVolume(string volume, List<MftReader.Entry> entries, TimeSpan took)
    {
        Volume = volume;
        _entries = entries;
        Took = took;
        _next = new int[entries.Count];
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            long parent = entries[i].ParentFrn;
            _next[i] = _first.TryGetValue(parent, out int head) ? head : -1;
            _first[parent] = i;
            if ((entries[i].Attributes & (uint)FileAttributes.Directory) != 0) _folders.Add(entries[i].Frn);
        }
    }

    private const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003, IO_REPARSE_TAG_SYMLINK = 0xA000000C;

    /// <summary>The folder's entries, as <see cref="DirLister.List"/> returns them; null when the
    /// folder isn't in the table (created after it was read): then list it from disk.</summary>
    public List<(FsEntry entry, bool isLink)>? List(long dirFid, string dir)
    {
        if (dirFid == 0) return null;
        var result = new List<(FsEntry, bool)>();
        // A folder of the table without children is empty; one not in it at all was made after it was read.
        if (!_first.TryGetValue(dirFid, out int i)) return _folders.Contains(dirFid) || (dirFid & 0xFFFFFFFFFFFF) == 5 ? result : null;
        string prefix = Settings.WithSeparator(dir);
        for (; i >= 0; i = _next[i])
        {
            var e = _entries[i];
            // 0x40000 in the table marks extended attributes (Windows keeps some on executables);
            // Windows itself never reports it, and it is FILE_ATTRIBUTE_RECALL_ON_OPEN to everyone else.
            var attrs = (FileAttributes)(e.Attributes & ~0x40000u);
            bool isDir = (attrs & FileAttributes.Directory) != 0;
            // A reparse tag that couldn't be read (0xFFFFFFFF) counts as a link too: never followed.
            bool isLink = (attrs & FileAttributes.ReparsePoint) != 0 && e.ReparseTag is IO_REPARSE_TAG_MOUNT_POINT or IO_REPARSE_TAG_SYMLINK or 0xFFFFFFFF;
            result.Add((new FsEntry(prefix + e.Name, e.Name, isDir, attrs, isDir ? 0 : e.Size, DirLister.FileTimeToTicks(e.MTime), e.Frn), isLink));
        }
        return result;
    }

    /// <summary>Diagnostics (spotlight-cli mftcheck): lists <paramref name="folder"/>'s tree both from
    /// the table and from disk and reports the differences.</summary>
    public string CompareWithDisk(string folder, int maxReport = 400)
    {
        var report = new StringBuilder();
        long same = 0, folders = 0, differ = 0, onlyDisk = 0, onlyMft = 0;
        var stack = new Stack<(string dir, long fid)>();
        stack.Push((folder, DirLister.Stat(folder)?.Fid ?? 0));
        void Note(string line) { if (differ + onlyDisk + onlyMft <= maxReport) report.AppendLine(line); }
        while (stack.Count > 0)
        {
            var (dir, fid) = stack.Pop();
            folders++;
            var disk = DirLister.List(dir).ToDictionary(x => x.entry.Name, StringComparer.OrdinalIgnoreCase);
            var mft = (List(fid, dir) ?? new()).ToDictionary(x => x.entry.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var (name, (d, dLink)) in disk)
            {
                if (!mft.TryGetValue(name, out var m)) { onlyDisk++; Note("只在磁盘: " + d.Path); continue; }
                var (me, mLink) = m;
                bool attrsDiffer = (d.Attributes & ~FileAttributes.Normal) != (me.Attributes & ~FileAttributes.Normal);
                if (d.IsDir != me.IsDir || d.Size != me.Size || d.MTime != me.MTime || d.Fid != me.Fid || attrsDiffer || dLink != mLink)
                {
                    differ++;
                    Note($"不同: {d.Path}  磁盘 size={d.Size} mtime={d.MTime} attr={(uint)d.Attributes:X} fid={d.Fid:X} link={dLink} | MFT size={me.Size} mtime={me.MTime} attr={(uint)me.Attributes:X} fid={me.Fid:X} link={mLink}");
                }
                else same++;
                if (d.IsDir && !dLink) stack.Push((d.Path, d.Fid));
            }
            foreach (var name in mft.Keys.Where(n => !disk.ContainsKey(n))) { onlyMft++; Note("只在 MFT: " + mft[name].entry.Path); }
        }
        report.Insert(0, $"{folder}: {folders:N0} 个文件夹，一致 {same:N0}，不同 {differ:N0}，只在磁盘 {onlyDisk:N0}，只在 MFT {onlyMft:N0}{Environment.NewLine}");
        return report.ToString();
    }

    /// <summary>Asks the service for <paramref name="volume"/>'s table ("C:\"). Null, with the
    /// reason, when the service isn't installed or can't read it: then the drive is crawled folder by folder.</summary>
    public static MftVolume? Load(string volume, out string? why)
    {
        why = null;
        if (!OperatingSystem.IsWindows()) { why = "not Windows"; return null; }
        if (!MftService.Installed) { why = "服务未安装"; return null; }
        var sw = Stopwatch.StartNew();
        try
        {
            using var pipe = MftService.Connect();
            if (pipe == null) { why = "服务没有响应"; return null; }
            var request = Encoding.ASCII.GetBytes($"SWMFT {MftProtocol.Version} SCAN {volume}\n");
            pipe.Write(request);
            pipe.Flush();
            using var r = new BinaryReader(new BufferedStream(pipe, 1 << 20), Encoding.UTF8);
            int status = r.ReadInt32();
            if (status != 0) { why = $"服务读取失败（{status}）：{r.ReadString()}"; return null; }
            var list = new List<MftReader.Entry>(1 << 20);
            while (MftProtocol.TryReadEntry(r, out var e)) list.Add(e);
            return new MftVolume(volume, list, sw.Elapsed);
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException)
        {
            why = "服务连接中断：" + e.Message;
            return null;
        }
    }
}

/// <summary>The SpotlightWin MFT service as seen from the app: installed or not, start it, connect.</summary>
public static class MftService
{
    /// <summary>The service exe that ships next to the app, run elevated to install it.</summary>
    public static string BundledExe => Path.Combine(AppContext.BaseDirectory, "SpotlightWin.MftService.exe");

    public static bool Installed
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            IntPtr scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
            if (scm == IntPtr.Zero) return false;
            try
            {
                IntPtr svc = OpenService(scm, MftProtocol.ServiceName, SERVICE_QUERY_STATUS);
                if (svc == IntPtr.Zero) return false;
                CloseServiceHandle(svc);
                return true;
            }
            finally { CloseServiceHandle(scm); }
        }
    }

    /// <summary>A connection to the service, starting it first if it is stopped (it stops itself when idle).</summary>
    internal static NamedPipeClientStream? Connect()
    {
        var pipe = new NamedPipeClientStream(".", MftProtocol.PipeName, PipeDirection.InOut);
        try { pipe.Connect(200); return pipe; }
        catch (TimeoutException) { }
        Start();
        try { pipe.Connect(10_000); return pipe; }
        catch (TimeoutException) { pipe.Dispose(); return null; }
    }

    private static void Start()
    {
        IntPtr scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) return;
        try
        {
            IntPtr svc = OpenService(scm, MftProtocol.ServiceName, SERVICE_START);
            if (svc == IntPtr.Zero) return;
            StartService(svc, 0, IntPtr.Zero); // fails harmlessly when it is already running
            CloseServiceHandle(svc);
        }
        finally { CloseServiceHandle(scm); }
    }

    /// <summary>Runs the bundled service exe as administrator (Windows asks the user) with
    /// "install" or "uninstall". True when it finished successfully.</summary>
    public static bool RunElevated(string verb, out string? error)
    {
        error = null;
        if (!File.Exists(BundledExe)) { error = "找不到 " + BundledExe; return false; }
        try
        {
            using var p = Process.Start(new ProcessStartInfo(BundledExe, verb) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            if (p == null) { error = "无法启动"; return false; }
            p.WaitForExit();
            if (p.ExitCode != 0) error = $"退出代码 {p.ExitCode}";
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            error = "已取消（没有在 Windows 的提示里选择「是」）";
            return false;
        }
    }

    private const uint SC_MANAGER_CONNECT = 1, SERVICE_QUERY_STATUS = 4, SERVICE_START = 0x10;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr scm, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool StartService(IntPtr svc, int argc, IntPtr argv);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr h);
}
