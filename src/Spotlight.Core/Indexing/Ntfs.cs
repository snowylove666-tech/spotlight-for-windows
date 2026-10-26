using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Spotlight.Core.Indexing;

/// <summary>One file or folder as the crawler and the change journal see it.</summary>
/// <param name="Fid">NTFS file reference number (the file's id on its volume); 0 when unknown.</param>
public readonly record struct FsEntry(string Path, string Name, bool IsDir, FileAttributes Attributes, long Size, long MTime, long Fid)
{
    public string Extension => IsDir ? "" : System.IO.Path.GetExtension(Name);
}

/// <summary>
/// Lists a folder with GetFileInformationByHandleEx(FileIdBothDirectoryInfo): the same data as
/// FindFirstFile (name, size, times, attributes) plus each entry's file id, in one pass. The id is
/// what the USN journal reports changes by (see <see cref="UsnJournal"/>).
/// </summary>
internal static class DirLister
{
    private const int FileIdBothDirectoryInfo = 10, FileIdBothDirectoryRestartInfo = 11;
    private const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003, IO_REPARSE_TAG_SYMLINK = 0xA000000C;

    /// <summary>The entries of <paramref name="dir"/>; an empty list when it can't be read.</summary>
    public static List<(FsEntry entry, bool isLink)> List(string dir)
    {
        var result = new List<(FsEntry, bool)>();
        using var h = Native.CreateFile(dir, Native.FILE_LIST_DIRECTORY, Native.ShareAll, IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (h.IsInvalid) return result;
        var buf = new byte[64 * 1024];
        string prefix = Settings.WithSeparator(dir);
        int cls = FileIdBothDirectoryRestartInfo;
        while (Native.GetFileInformationByHandleEx(h, cls, buf, buf.Length))
        {
            cls = FileIdBothDirectoryInfo;
            int off = 0;
            while (true)
            {
                int next = BitConverter.ToInt32(buf, off);
                long write = BitConverter.ToInt64(buf, off + 24);
                long size = BitConverter.ToInt64(buf, off + 40);
                var attrs = (FileAttributes)BitConverter.ToUInt32(buf, off + 56);
                int nameBytes = BitConverter.ToInt32(buf, off + 60);
                uint eaOrTag = BitConverter.ToUInt32(buf, off + 64); // the reparse tag for reparse points
                long fid = BitConverter.ToInt64(buf, off + 96);
                string name = Encoding.Unicode.GetString(buf, off + 104, nameBytes);
                if (name != "." && name != "..")
                {
                    bool isDir = (attrs & FileAttributes.Directory) != 0;
                    bool isLink = (attrs & FileAttributes.ReparsePoint) != 0 && eaOrTag is IO_REPARSE_TAG_MOUNT_POINT or IO_REPARSE_TAG_SYMLINK;
                    result.Add((new FsEntry(prefix + name, name, isDir, attrs, isDir ? 0 : size, FileTimeToTicks(write), fid), isLink));
                }
                if (next == 0) break;
                off += next;
            }
        }
        return result;
    }

    /// <summary>The folder or file itself (for roots), or null when it can't be opened.</summary>
    public static FsEntry? Stat(string path)
    {
        using var h = Native.CreateFile(path, Native.FILE_READ_ATTRIBUTES, Native.ShareAll, IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        return h.IsInvalid ? null : Describe(h, path);
    }

    /// <summary>Everything about an open file: used for the crawl's roots and for journal changes.</summary>
    internal static FsEntry? Describe(SafeFileHandle h, string path)
    {
        var basic = new byte[40];   // FILE_BASIC_INFO
        var standard = new byte[24]; // FILE_STANDARD_INFO
        var id = new byte[8];
        if (!Native.GetFileInformationByHandleEx(h, 0 /*FileBasicInfo*/, basic, basic.Length)) return null;
        if (!Native.GetFileInformationByHandleEx(h, 1 /*FileStandardInfo*/, standard, standard.Length)) return null;
        var attrs = (FileAttributes)BitConverter.ToUInt32(basic, 32);
        bool isDir = (attrs & FileAttributes.Directory) != 0;
        long fid = Native.GetFileInformationByHandle(h, out var info) ? ((long)info.FileIndexHigh << 32) | info.FileIndexLow : 0;
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        if (name.Length == 0) name = path;
        return new FsEntry(path, name, isDir, attrs, isDir ? 0 : BitConverter.ToInt64(standard, 8), FileTimeToTicks(BitConverter.ToInt64(basic, 16)), fid);
    }

    /// <summary>Same value as <see cref="FileSystemInfo.LastWriteTimeUtc"/>.Ticks, so rows written by
    /// either way compare equal and unchanged documents are not read again.</summary>
    internal static long FileTimeToTicks(long fileTime) => fileTime <= 0 ? 0 : DateTime.FromFileTimeUtc(fileTime).Ticks;
}

/// <summary>
/// The NTFS change journal of one volume, read without administrator rights
/// (FSCTL_READ_UNPRIVILEGED_USN_JOURNAL, Windows 10+). Like Everything, changes are read from where
/// the last read stopped, also across restarts and sleep, instead of crawling folders or depending
/// on FileSystemWatcher buffers. Unprivileged reads leave file names out; a record's file id is
/// turned into its current path with OpenFileById, and the index keeps every row's id so a deleted
/// file (which can no longer be opened) is found by id.
/// </summary>
public sealed class UsnJournal : IDisposable
{
    private const uint FSCTL_QUERY_USN_JOURNAL = 0x000900f4, FSCTL_READ_UNPRIVILEGED_USN_JOURNAL = 0x000903ab;
    private const int ERROR_JOURNAL_DELETE_IN_PROGRESS = 1178, ERROR_JOURNAL_NOT_ACTIVE = 1179, ERROR_JOURNAL_ENTRY_DELETED = 1181, ERROR_HANDLE_EOF = 38;

    private readonly SafeFileHandle _root;
    private readonly byte[] _buf = new byte[256 * 1024];

    /// <summary>"C:\"</summary>
    public string Volume { get; }
    public ulong JournalId { get; private set; }
    public long FirstUsn { get; private set; }
    public long NextUsn { get; private set; }

    private UsnJournal(string volume, SafeFileHandle root)
    {
        Volume = volume;
        _root = root;
    }

    /// <summary>The journal of the volume holding <paramref name="path"/>, or null (not NTFS, no
    /// journal, a network drive, or Windows before 10).</summary>
    public static UsnJournal? Open(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var volume = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(volume) || volume.StartsWith(@"\\")) return null;
        try
        {
            if (new DriveInfo(volume).DriveFormat != "NTFS") return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
        var h = Native.CreateFile(volume, Native.FILE_READ_ATTRIBUTES, Native.ShareAll, IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (h.IsInvalid) return null;
        var j = new UsnJournal(volume, h);
        if (!j.Query()) { j.Dispose(); return null; }
        // Make sure unprivileged reads work here (an old Windows, a filter driver) before relying on it.
        if (j.Read(j.NextUsn, new List<UsnChange>()) == ReadResult.Unsupported) { j.Dispose(); return null; }
        return j;
    }

    /// <summary>Reads the journal's id and range.</summary>
    public bool Query()
    {
        var q = new byte[80];
        if (!Native.DeviceIoControl(_root, FSCTL_QUERY_USN_JOURNAL, null, 0, q, q.Length, out _, IntPtr.Zero)) return false;
        JournalId = BitConverter.ToUInt64(q, 0);
        FirstUsn = BitConverter.ToInt64(q, 8);
        NextUsn = BitConverter.ToInt64(q, 16);
        return true;
    }

    public enum ReadResult { Ok, Reset, Unsupported }

    /// <summary>
    /// Appends the changes from <paramref name="from"/> to the end of the journal and returns where
    /// the next read starts in <see cref="NextUsn"/>. Reset: the journal was recreated or has already
    /// dropped records from that point (the app was off for long on a busy disk): crawl again.
    /// </summary>
    public ReadResult Read(long from, List<UsnChange> into, int maxRecords = 200_000)
    {
        var req = new byte[48]; // READ_USN_JOURNAL_DATA_V1
        long usn = from;
        int count = 0;
        while (count < maxRecords)
        {
            BitConverter.TryWriteBytes(req.AsSpan(0), usn);
            BitConverter.TryWriteBytes(req.AsSpan(8), 0xFFFFFFFFu);  // all reasons
            BitConverter.TryWriteBytes(req.AsSpan(12), 0);           // not only on close
            BitConverter.TryWriteBytes(req.AsSpan(16), 0L);          // don't wait
            BitConverter.TryWriteBytes(req.AsSpan(24), 0L);
            BitConverter.TryWriteBytes(req.AsSpan(32), JournalId);
            BitConverter.TryWriteBytes(req.AsSpan(40), (ushort)2);
            BitConverter.TryWriteBytes(req.AsSpan(42), (ushort)3);
            if (!Native.DeviceIoControl(_root, FSCTL_READ_UNPRIVILEGED_USN_JOURNAL, req, req.Length, _buf, _buf.Length, out int got, IntPtr.Zero))
            {
                int err = Marshal.GetLastWin32Error();
                if (err == ERROR_HANDLE_EOF) break;
                if (err is ERROR_JOURNAL_ENTRY_DELETED or ERROR_JOURNAL_NOT_ACTIVE or ERROR_JOURNAL_DELETE_IN_PROGRESS) return ReadResult.Reset;
                // A recreated journal (new id) or a start that is no longer a record boundary.
                if (err == 87) return ReadResult.Reset;
                return ReadResult.Unsupported;
            }
            long next = BitConverter.ToInt64(_buf, 0);
            int off = 8;
            while (off + 60 <= got)
            {
                int len = BitConverter.ToInt32(_buf, off);
                if (len <= 0) break;
                int major = BitConverter.ToUInt16(_buf, off + 4);
                if (major == 3)
                    into.Add(new UsnChange(BitConverter.ToInt64(_buf, off + 8), BitConverter.ToInt64(_buf, off + 24),
                        (UsnReason)BitConverter.ToUInt32(_buf, off + 56), (FileAttributes)BitConverter.ToUInt32(_buf, off + 68)));
                else if (major == 2)
                    into.Add(new UsnChange(BitConverter.ToInt64(_buf, off + 8), BitConverter.ToInt64(_buf, off + 16),
                        (UsnReason)BitConverter.ToUInt32(_buf, off + 40), (FileAttributes)BitConverter.ToUInt32(_buf, off + 52)));
                off += len;
                count++;
            }
            if (next == usn) break; // nothing more
            usn = next;
            if (got <= 8) break;
        }
        NextUsn = usn;
        return ReadResult.Ok;
    }

    /// <summary>The current path and details of the file with this id, or null if it is gone (or
    /// can't be opened, e.g. no access).</summary>
    public FsEntry? Resolve(long fid)
    {
        var desc = new Native.FILE_ID_DESCRIPTOR { dwSize = (uint)Marshal.SizeOf<Native.FILE_ID_DESCRIPTOR>(), Type = 0, FileId = fid };
        using var h = Native.OpenFileById(_root, ref desc, Native.FILE_READ_ATTRIBUTES, Native.ShareAll, IntPtr.Zero, Native.FILE_FLAG_BACKUP_SEMANTICS);
        if (h.IsInvalid) return null;
        var sb = new StringBuilder(1024);
        int n = Native.GetFinalPathNameByHandle(h, sb, sb.Capacity, 0 /*FILE_NAME_NORMALIZED | VOLUME_NAME_DOS*/);
        if (n <= 0 || n >= sb.Capacity) return null;
        var path = sb.ToString();
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return null;
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        return DirLister.Describe(h, path);
    }

    public void Dispose() => _root.Dispose();
}

[Flags]
public enum UsnReason : uint
{
    DataOverwrite = 0x1, DataExtend = 0x2, DataTruncation = 0x4, FileCreate = 0x100, FileDelete = 0x200,
    RenameOldName = 0x1000, RenameNewName = 0x2000, BasicInfoChange = 0x8000, HardLinkChange = 0x10000,
    ReparsePointChange = 0x100000, Close = 0x80000000,
}

/// <summary>One journal record: which file (by id), in which folder (by id), what happened.</summary>
public readonly record struct UsnChange(long Fid, long ParentFid, UsnReason Reason, FileAttributes Attributes);

internal static class Native
{
    public const uint FILE_LIST_DIRECTORY = 0x1, FILE_READ_ATTRIBUTES = 0x80, ShareAll = 7, OPEN_EXISTING = 3, FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    [StructLayout(LayoutKind.Sequential)]
    public struct FILE_ID_DESCRIPTOR
    {
        public uint dwSize;
        public int Type;
        public long FileId;
        public long Padding; // the union is 16 bytes (FILE_ID_128)
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)] // FILETIMEs are two DWORDs: no 8-byte alignment
    public struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int cls, byte[] buf, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetFileInformationByHandle(SafeFileHandle h, out BY_HANDLE_FILE_INFORMATION info);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[]? inBuf, int inLen, byte[] outBuf, int outLen, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern SafeFileHandle OpenFileById(SafeFileHandle volumeHint, ref FILE_ID_DESCRIPTOR id, uint access, uint share, IntPtr sa, uint flags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetFinalPathNameByHandleW")]
    public static extern int GetFinalPathNameByHandle(SafeFileHandle h, StringBuilder path, int size, uint flags);
}
