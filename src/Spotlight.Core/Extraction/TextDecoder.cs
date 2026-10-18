using System.Text;

namespace Spotlight.Core.Extraction;

/// <summary>Reads text files whose encoding is unknown: BOM-marked UTF-8/UTF-16, plain UTF-8,
/// or legacy GB18030 (GBK), which is what most older Chinese Windows files use.</summary>
public static class TextDecoder
{
    static TextDecoder() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static Encoding? _gb18030;
    private static Encoding Gb18030 => _gb18030 ??= Encoding.GetEncoding("GB18030");

    public static string ReadFile(string path, int maxChars)
    {
        // Cap the bytes read: UTF-8 Chinese uses 3 bytes per char, UTF-16 uses 2.
        long maxBytes = Math.Min((long)maxChars * 3 + 4, 64L * 1024 * 1024);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        var len = (int)Math.Min(fs.Length, maxBytes);
        var bytes = new byte[len];
        int read = 0;
        while (read < len)
        {
            int n = fs.Read(bytes, read, len - read);
            if (n == 0) break;
            read += n;
        }
        return Decode(bytes.AsSpan(0, read), maxChars);
    }

    public static string Decode(ReadOnlySpan<byte> bytes, int maxChars = int.MaxValue)
    {
        string s;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) s = Encoding.UTF8.GetString(bytes[3..]);
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) s = Encoding.Unicode.GetString(bytes[2..]);
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) s = Encoding.BigEndianUnicode.GetString(bytes[2..]);
        else if (LooksLikeUtf8(bytes)) s = Encoding.UTF8.GetString(bytes);
        else s = Gb18030.GetString(bytes);
        return s.Length > maxChars ? s[..maxChars] : s;
    }

    private static bool LooksLikeUtf8(ReadOnlySpan<byte> b)
    {
        int i = 0;
        while (i < b.Length)
        {
            byte c = b[i];
            int n = c < 0x80 ? 0 : (c & 0xE0) == 0xC0 ? 1 : (c & 0xF0) == 0xE0 ? 2 : (c & 0xF8) == 0xF0 ? 3 : -1;
            if (n < 0) return false;
            if (i + n >= b.Length) return true; // truncated tail from our own read cap
            for (int k = 1; k <= n; k++)
                if ((b[i + k] & 0xC0) != 0x80) return false;
            i += n + 1;
        }
        return true;
    }

    /// <summary>True when the bytes contain NULs that are not part of UTF-16, i.e. the file is binary.</summary>
    public static bool LooksBinary(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 2 && ((head[0] == 0xFF && head[1] == 0xFE) || (head[0] == 0xFE && head[1] == 0xFF))) return false;
        return head.IndexOf((byte)0) >= 0;
    }
}
