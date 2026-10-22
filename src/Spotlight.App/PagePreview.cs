using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Media.Imaging;
using Spotlight.Core;
using Spotlight.Core.Search;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Spotlight.App;

/// <summary>A picture of the document for the preview pane, plus where the query sits on it.</summary>
public sealed class PreviewImage(Bitmap bitmap, IReadOnlyList<Rect> highlights, int? page, int? pageCount)
{
    public Bitmap Bitmap { get; } = bitmap;
    /// <summary>Hit rectangles in 0..1 coordinates of the image.</summary>
    public IReadOnlyList<Rect> Highlights { get; } = highlights;
    public int? Page { get; } = page;
    public int? PageCount { get; } = pageCount;
}

/// <summary>
/// Renders what the original looks like: the PDF page that contains the hit (Windows' built-in PDF
/// renderer, with the matched words located by PdfPig), the picture itself for images, and the
/// Explorer thumbnail for Office documents and videos. Everything runs off the UI thread.
/// </summary>
internal static class PagePreview
{
    private static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".ico" };

    public static bool CanPreview(SearchResult r) =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) && !IsCloudOnly(r.Path) &&
        (r.Kind is FileKind.Pdf or FileKind.Document or FileKind.Spreadsheet or FileKind.Presentation or FileKind.Video
         || ImageExts.Contains(Path.GetExtension(r.Path).ToLowerInvariant()));

    /// <summary>Returns null when there is nothing better to show than the icon.</summary>
    public static async Task<PreviewImage?> RenderAsync(SearchResult r, List<string> terms, int widthPx, CancellationToken ct, int? page = null)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return null;
        try
        {
            if (r.Kind == FileKind.Pdf) return await RenderPdfAsync(r.Path, page ?? r.Page, terms, widthPx, ct);
            if (ImageExts.Contains(Path.GetExtension(r.Path).ToLowerInvariant()))
                return await Task.Run(() => LoadImage(r.Path, widthPx), ct);
            return await ShellThumbnail(r.Path, Math.Min(1024, widthPx), ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return null; } // damaged, encrypted or locked file: the icon will do
    }

    private static PreviewImage? LoadImage(string path, int widthPx)
    {
        using var s = File.OpenRead(path);
        var bmp = Bitmap.DecodeToWidth(s, widthPx, BitmapInterpolationMode.HighQuality);
        return new PreviewImage(bmp, Array.Empty<Rect>(), null, null);
    }

    // ---------------------------------------------------------------- PDF

    [SupportedOSPlatform("windows10.0.17763.0")]
    private static async Task<PreviewImage?> RenderPdfAsync(string path, int? knownPage, List<string> terms, int widthPx, CancellationToken ct)
    {
        // Find the page (old indexes don't know it) and the hit boxes with PdfPig.
        var (page, pageCount, boxes) = await Task.Run(() => Locate(path, knownPage, terms, ct), ct);
        ct.ThrowIfCancellationRequested();

        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        var doc = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
        if (doc.PageCount == 0) return null;
        page = Math.Clamp(page, 1, (int)doc.PageCount);
        using var pdfPage = doc.GetPage((uint)(page - 1));
        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var options = new Windows.Data.Pdf.PdfPageRenderOptions { DestinationWidth = (uint)widthPx };
        await pdfPage.RenderToStreamAsync(stream, options);
        ct.ThrowIfCancellationRequested();
        stream.Seek(0);
        using var managed = stream.AsStreamForRead();
        var bmp = new Bitmap(managed);
        return new PreviewImage(bmp, boxes, page, (int)doc.PageCount);
    }

    /// <summary>The page to show (the known one, else the first page containing a term, else 1) and
    /// the rectangles of the terms on it.</summary>
    internal static (int page, int pageCount, List<Rect> boxes) Locate(string path, int? knownPage, List<string> terms, CancellationToken ct)
    {
        using var doc = PdfDocument.Open(path, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
        int count = doc.NumberOfPages;
        if (knownPage is { } k && k >= 1 && k <= count)
            return (k, count, SafeBoxes(doc, k, terms));
        if (terms.Count > 0)
        {
            // Scan from the start, but give up after a few seconds on huge books.
            var until = DateTime.UtcNow.AddSeconds(4);
            for (int i = 1; i <= count && DateTime.UtcNow < until; i++)
            {
                ct.ThrowIfCancellationRequested();
                var boxes = SafeBoxes(doc, i, terms);
                if (boxes.Count > 0) return (i, count, boxes);
            }
        }
        return (1, count, new List<Rect>());
    }

    /// <summary>The nearest page after (<paramref name="step"/> = 1) or before (-1) <paramref name="from"/>
    /// that contains one of the terms, or null. Gives up after a few seconds on huge books.</summary>
    internal static int? FindHitPage(string path, int from, int step, List<string> terms, CancellationToken ct)
    {
        if (terms.Count == 0) return null;
        using var doc = PdfDocument.Open(path, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
        var until = DateTime.UtcNow.AddSeconds(6);
        for (int i = from + step; i >= 1 && i <= doc.NumberOfPages && DateTime.UtcNow < until; i += step)
        {
            ct.ThrowIfCancellationRequested();
            if (SafeBoxes(doc, i, terms).Count > 0) return i;
        }
        return null;
    }

    // ---------------------------------------------------------------- text

    /// <summary>Text and code files are shown as text: up to a few thousand characters starting a
    /// little before the first hit (or at the top).</summary>
    internal static string? TextAround(string path, List<string> terms, CancellationToken ct)
    {
        var extractor = Spotlight.Core.Extraction.ContentExtractors.Default.For(Path.GetExtension(path));
        if (extractor == null || new FileInfo(path).Length > 64L * 1024 * 1024) return null;
        var text = extractor.Extract(path, 4_000_000, ct);
        int hit = -1;
        foreach (var t in terms.Where(t => t.Length > 0))
        {
            int k = text.IndexOf(t, StringComparison.OrdinalIgnoreCase);
            if (k >= 0 && (hit < 0 || k < hit)) hit = k;
        }
        int start = hit <= 300 ? 0 : text.LastIndexOf('\n', hit - 300) + 1;
        int length = Math.Min(text.Length - start, 8000);
        return (start > 0 ? "…\n" : "") + text.Substring(start, length) + (start + length < text.Length ? "\n…" : "");
    }

    private static List<Rect> SafeBoxes(PdfDocument doc, int pageNumber, List<string> terms)
    {
        try { return Boxes(doc.GetPage(pageNumber), terms); }
        catch (Exception e) when (e is not OperationCanceledException) { return new List<Rect>(); }
    }

    /// <summary>Finds every occurrence of the terms among the page's letters (ignoring spaces, which
    /// PDFs place arbitrarily inside Chinese text) and returns one rectangle per line of each hit.</summary>
    internal static List<Rect> Boxes(Page page, List<string> terms)
    {
        var result = new List<Rect>();
        if (terms.Count == 0 || page.Rotation.Value != 0) return result;
        var letters = page.Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToList();
        var text = new System.Text.StringBuilder();
        var owner = new List<int>(); // index into letters for each char of text
        for (int i = 0; i < letters.Count; i++)
            foreach (var ch in letters[i].Value) { text.Append(char.ToLowerInvariant(ch)); owner.Add(i); }
        var s = text.ToString();
        var crop = page.CropBox.Bounds;
        if (crop.Width <= 0 || crop.Height <= 0) return result;

        foreach (var term in terms.Where(t => t.Length > 0))
        {
            var t = term.ToLowerInvariant();
            for (int at = s.IndexOf(t, StringComparison.Ordinal); at >= 0 && result.Count < 200; at = s.IndexOf(t, at + t.Length, StringComparison.Ordinal))
            {
                Rect? line = null;
                double baseline = double.NaN;
                for (int j = owner[at]; j <= owner[at + t.Length - 1]; j++)
                {
                    var g = letters[j].BoundingBox;
                    // Normalised to the crop box, origin top-left like the rendered image.
                    var r = new Rect((g.Left - crop.Left) / crop.Width, (crop.Top - g.Top) / crop.Height,
                                     g.Width / crop.Width, g.Height / crop.Height);
                    double b = letters[j].StartBaseLine.Y;
                    if (line is { } cur && Math.Abs(b - baseline) < g.Height * 0.5) line = cur.Union(r);
                    else
                    {
                        if (line is { } done) result.Add(done);
                        line = r;
                        baseline = b;
                    }
                }
                if (line is { } last) result.Add(last);
            }
        }
        return result;
    }

    // ---------------------------------------------------------------- shell thumbnails

    private static Task<PreviewImage?> ShellThumbnail(string path, int size, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<PreviewImage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        // The shell's thumbnail providers expect a single-threaded apartment.
        var t = new Thread(() =>
        {
            try { tcs.TrySetResult(ct.IsCancellationRequested ? null : LoadThumbnail(path, size)); }
            catch (Exception e) { tcs.TrySetException(e); }
        }) { IsBackground = true, Name = "Thumbnail" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr hbitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr bindCtx, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr h);

    private const int SIIGBF_THUMBNAILONLY = 0x08, SIIGBF_BIGGERSIZEOK = 0x01;

    [SupportedOSPlatform("windows")]
    private static PreviewImage? LoadThumbnail(string path, int size)
    {
        SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var factory);
        try
        {
            // Thumbnail only: when a file type has none (e.g. a Word file saved without one) the icon stays.
            if (factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_THUMBNAILONLY | SIIGBF_BIGGERSIZEOK, out var hbmp) != 0 || hbmp == IntPtr.Zero)
                return null;
            try
            {
                using var gdi = System.Drawing.Image.FromHbitmap(hbmp);
                using var ms = new MemoryStream();
                gdi.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                ms.Position = 0;
                return new PreviewImage(new Bitmap(ms), Array.Empty<Rect>(), null, null);
            }
            finally { DeleteObject(hbmp); }
        }
        finally { Marshal.ReleaseComObject(factory); }
    }

    /// <summary>Online-only cloud files would be downloaded just to draw a preview.</summary>
    internal static bool IsCloudOnly(string path)
    {
        try
        {
            const FileAttributes RecallOnOpen = (FileAttributes)0x00040000, RecallOnDataAccess = (FileAttributes)0x00400000;
            return (File.GetAttributes(path) & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0;
        }
        catch (Exception) { return true; }
    }
}
