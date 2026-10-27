using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Spotlight.Core.Indexing;

/// <summary>
/// Reads every file and folder of an NTFS volume straight from its master file table ($MFT), the way
/// Everything builds its index: one sequential read of a few hundred MB instead of opening every
/// folder. Needs administrator rights (the volume is opened raw), so it runs in the SpotlightWin MFT
/// service, which streams the result to the app (see <see cref="MftProtocol"/>).
/// This file is compiled into both the service and Spotlight.Core: it only uses the base library.
/// </summary>
public static class MftReader
{
    /// <summary>One name of one file record. A file with hard links has one per link.</summary>
    /// <param name="Frn">File reference number: record index | sequence number &lt;&lt; 48 (the file id).</param>
    /// <param name="ParentFrn">The folder holding this name.</param>
    /// <param name="MTime">Last write time as a FILETIME.</param>
    public readonly record struct Entry(long Frn, long ParentFrn, uint Attributes, long Size, long MTime, uint ReparseTag, string Name);

    // The first 24 records are NTFS's own metadata ($MFT, $LogFile, $Extend…), never listed by Windows.
    private const int FirstUserRecord = 24;
    private const uint FSCTL_GET_NTFS_VOLUME_DATA = 0x00090064;
    private const uint AttrStandardInformation = 0x10, AttrFileName = 0x30, AttrData = 0x80, AttrReparsePoint = 0xC0, AttrEnd = 0xFFFFFFFF;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    // $STANDARD_INFORMATION carries two internal flags next to the usual attributes.
    private const uint InternalFlags = 0x10000000 | 0x20000000;

    /// <summary>Reads the whole table of <paramref name="volume"/> ("C:\") and calls
    /// <paramref name="emit"/> for each in-use name. Throws Win32Exception without admin rights.</summary>
    public static void Read(string volume, Action<Entry> emit)
    {
        string device = @"\\.\" + volume.TrimEnd('\\');
        using var h = CreateFile(device, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "open " + device);

        var vd = new byte[96];
        if (!DeviceIoControl(h, FSCTL_GET_NTFS_VOLUME_DATA, IntPtr.Zero, 0, vd, vd.Length, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "FSCTL_GET_NTFS_VOLUME_DATA");
        int bytesPerSector = BitConverter.ToInt32(vd, 40);
        int bytesPerCluster = BitConverter.ToInt32(vd, 44);
        int recordSize = BitConverter.ToInt32(vd, 48);
        long mftValidLength = BitConverter.ToInt64(vd, 56);
        long mftStartLcn = BitConverter.ToInt64(vd, 64);
        if (recordSize <= 0 || bytesPerCluster <= 0 || bytesPerSector <= 0) throw new InvalidDataException("not an NTFS volume");

        // Record 0 describes $MFT itself: its $DATA run list says where the rest of the table is.
        var first = new byte[Math.Max(recordSize, bytesPerSector)];
        ReadAt(h, mftStartLcn * bytesPerCluster, first, first.Length);
        if (!Fixup(first, 0, recordSize)) throw new InvalidDataException("$MFT record 0 is damaged");
        var runs = MftRuns(first, recordSize) ?? throw new InvalidDataException("$MFT has no data runs");

        long records = mftValidLength / recordSize;
        var acc = new Accumulator(records);
        const int chunkBytes = 4 << 20;
        var buf = new byte[chunkBytes];
        long index = 0;
        foreach (var (lcn, clusters) in runs)
        {
            if (lcn < 0) { index += clusters * bytesPerCluster / recordSize; continue; }
            long offset = lcn * bytesPerCluster, remaining = clusters * bytesPerCluster;
            while (remaining > 0 && index < records)
            {
                int n = (int)Math.Min(chunkBytes, remaining);
                n -= n % recordSize;
                if (n == 0) break;
                ReadAt(h, offset, buf, n);
                for (int at = 0; at + recordSize <= n && index < records; at += recordSize, index++)
                    if (index >= FirstUserRecord || index == 5) acc.Parse(buf, at, recordSize, index);
                offset += n;
                remaining -= n;
            }
        }
        acc.Emit(emit);
    }

    /// <summary>Per-record data. Attributes of a big record may sit in extension records, which name
    /// their base record: everything is gathered by base record before names are emitted.</summary>
    private sealed class Accumulator(long records)
    {
        private readonly ushort[] _seq = new ushort[records];
        private readonly byte[] _flags = new byte[records];      // 1 in use, 2 folder
        private readonly uint[] _attrs = new uint[records];
        private readonly long[] _size = new long[records];
        private readonly long[] _mtime = new long[records];
        private readonly uint[] _tag = new uint[records];
        private readonly List<(long index, long parent, string name, byte ns)> _names = new((int)Math.Min(records, int.MaxValue / 2));

        public void Parse(byte[] b, int at, int size, long index)
        {
            if (BitConverter.ToUInt32(b, at) != 0x454C4946) return; // "FILE"
            if (!Fixup(b, at, size)) return;
            ushort flags = BitConverter.ToUInt16(b, at + 22);
            if ((flags & 1) == 0) return;
            long baseRef = BitConverter.ToInt64(b, at + 32) & 0xFFFFFFFFFFFF;
            long owner = baseRef != 0 ? baseRef : index;
            if (owner >= _seq.Length) return;
            if (baseRef == 0)
            {
                _seq[owner] = BitConverter.ToUInt16(b, at + 16);
                _flags[owner] = (byte)(1 | ((flags & 2) != 0 ? 2 : 0));
            }
            int a = at + BitConverter.ToUInt16(b, at + 20);
            int end = at + size;
            while (a + 16 <= end)
            {
                uint type = BitConverter.ToUInt32(b, a);
                if (type == AttrEnd) break;
                int len = BitConverter.ToInt32(b, a + 4);
                if (len <= 0 || a + len > end) break;
                bool resident = b[a + 8] == 0;
                int nameLen = b[a + 9];
                int valueLen = resident ? BitConverter.ToInt32(b, a + 16) : 0;
                int value = resident ? a + BitConverter.ToUInt16(b, a + 20) : 0;
                if (resident && value + valueLen > a + len) break;
                switch (type)
                {
                    case AttrStandardInformation when resident && valueLen >= 36:
                        _mtime[owner] = BitConverter.ToInt64(b, value + 8);
                        _attrs[owner] = BitConverter.ToUInt32(b, value + 32) & ~InternalFlags;
                        break;
                    case AttrFileName when resident && valueLen >= 66:
                        byte ns = b[value + 65];
                        if (ns == 2) break; // the 8.3 short name: an alias of the Win32 name
                        int chars = b[value + 64];
                        if (66 + chars * 2 > valueLen) break;
                        long parent = BitConverter.ToInt64(b, value);
                        string name = System.Text.Encoding.Unicode.GetString(b, value + 66, chars * 2);
                        _names.Add((owner, parent, name, ns));
                        break;
                    case AttrData when nameLen == 0:
                        if (resident) _size[owner] = valueLen;
                        else if (BitConverter.ToInt64(b, a + 16) == 0) _size[owner] = BitConverter.ToInt64(b, a + 48);
                        break;
                    case AttrReparsePoint:
                        if (resident && valueLen >= 4) _tag[owner] = BitConverter.ToUInt32(b, value);
                        else if (!resident) _tag[owner] = 0xFFFFFFFF; // tag not at hand: treat as a link, never followed
                        break;
                }
                a += len;
            }
        }

        public void Emit(Action<Entry> emit)
        {
            foreach (var (i, parent, name, _) in _names)
            {
                if ((_flags[i] & 1) == 0) continue; // extension record of a deleted file
                if (i == 5) continue;               // the root folder itself
                bool dir = (_flags[i] & 2) != 0;
                uint attrs = _attrs[i] | (dir ? FILE_ATTRIBUTE_DIRECTORY : 0);
                emit(new Entry(i | ((long)_seq[i] << 48), parent, attrs, dir ? 0 : _size[i], _mtime[i], _tag[i], name));
            }
        }
    }

    /// <summary>Applies the update sequence array: the last two bytes of every 512-byte stride were
    /// swapped out on disk to detect torn writes. False when the record is torn.</summary>
    private static bool Fixup(byte[] b, int at, int size)
    {
        int usa = BitConverter.ToUInt16(b, at + 4), count = BitConverter.ToUInt16(b, at + 6);
        if (count == 0 || usa + count * 2 > size) return false;
        ushort check = BitConverter.ToUInt16(b, at + usa);
        for (int i = 1; i < count; i++)
        {
            int pos = at + i * 512 - 2;
            if (pos + 2 > at + size) return false;
            if (BitConverter.ToUInt16(b, pos) != check) return false;
            b[pos] = b[at + usa + i * 2];
            b[pos + 1] = b[at + usa + i * 2 + 1];
        }
        return true;
    }

    /// <summary>The (cluster, count) extents of $MFT's unnamed $DATA attribute in record 0.</summary>
    private static List<(long lcn, long clusters)>? MftRuns(byte[] b, int size)
    {
        int a = BitConverter.ToUInt16(b, 20);
        while (a + 16 <= size)
        {
            uint type = BitConverter.ToUInt32(b, a);
            if (type == AttrEnd) break;
            int len = BitConverter.ToInt32(b, a + 4);
            if (len <= 0 || a + len > size) break;
            if (type == AttrData && b[a + 8] != 0 && b[a + 9] == 0)
            {
                var runs = new List<(long, long)>();
                int p = a + BitConverter.ToUInt16(b, a + 32);
                long lcn = 0;
                while (p < a + len && b[p] != 0)
                {
                    int lenBytes = b[p] & 0xF, offBytes = b[p] >> 4;
                    p++;
                    long clusters = 0;
                    for (int i = 0; i < lenBytes; i++) clusters |= (long)b[p + i] << (8 * i);
                    p += lenBytes;
                    if (offBytes == 0) { runs.Add((-1, clusters)); continue; } // sparse: no records there
                    long delta = 0;
                    for (int i = 0; i < offBytes; i++) delta |= (long)b[p + i] << (8 * i);
                    if ((b[p + offBytes - 1] & 0x80) != 0) delta -= 1L << (8 * offBytes); // sign extend
                    p += offBytes;
                    lcn += delta;
                    runs.Add((lcn, clusters));
                }
                return runs;
            }
            a += len;
        }
        return null;
    }

    private static void ReadAt(SafeFileHandle h, long offset, byte[] buf, int count)
    {
        int done = 0;
        while (done < count)
        {
            int n = RandomAccess.Read(h, buf.AsSpan(done, count - done), offset + done);
            if (n <= 0) throw new EndOfStreamException("volume read past end");
            done += n;
        }
    }

    private const uint GENERIC_READ = 0x80000000, FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, FILE_SHARE_DELETE = 4, OPEN_EXISTING = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inSize, byte[] outBuf, int outSize, out int returned, IntPtr overlapped);
}

/// <summary>
/// The service's named pipe. The app writes one line, "SWMFT 1 SCAN C:\", and reads back a status
/// (0 = ok, else a Win32 error code followed by a message) and then the entries, ending with Frn -1.
/// </summary>
public static class MftProtocol
{
    public const string PipeName = "SpotlightWin.Mft";
    public const string ServiceName = "SpotlightWinMft";
    public const int Version = 1;

    public static void WriteEntry(BinaryWriter w, in MftReader.Entry e)
    {
        w.Write(e.Frn);
        w.Write(e.ParentFrn);
        w.Write(e.Attributes);
        w.Write(e.Size);
        w.Write(e.MTime);
        w.Write(e.ReparseTag);
        w.Write((ushort)e.Name.Length);
        foreach (char c in e.Name) w.Write((ushort)c);
    }

    public static void WriteEnd(BinaryWriter w) => w.Write(-1L);

    public static bool TryReadEntry(BinaryReader r, out MftReader.Entry e)
    {
        long frn = r.ReadInt64();
        if (frn == -1) { e = default; return false; }
        long parent = r.ReadInt64();
        uint attrs = r.ReadUInt32();
        long size = r.ReadInt64();
        long mtime = r.ReadInt64();
        uint tag = r.ReadUInt32();
        int n = r.ReadUInt16();
        var bytes = r.ReadBytes(n * 2);
        if (bytes.Length != n * 2) throw new EndOfStreamException();
        e = new MftReader.Entry(frn, parent, attrs, size, mtime, tag, System.Text.Encoding.Unicode.GetString(bytes));
        return true;
    }
}
