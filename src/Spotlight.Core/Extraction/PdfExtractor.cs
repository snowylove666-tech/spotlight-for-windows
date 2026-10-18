using System.Text;
using UglyToad.PdfPig;

namespace Spotlight.Core.Extraction;

/// <summary>Extracts the text layer of PDFs with PdfPig (pure managed code, no IFilter needed).
/// Scanned PDFs without a text layer yield nothing; OCR is on the roadmap.</summary>
public sealed class PdfExtractor : IContentExtractor
{
    public bool CanHandle(string extension) => extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    public string Extract(string path, int maxChars, CancellationToken ct) => ExtractWithNote(path, maxChars, ct, out _);

    /// <summary>Reads page by page. A broken page is skipped, and when time runs out the pages read
    /// so far are kept, so one bad page or a very long book never loses the whole document.</summary>
    public string ExtractWithNote(string path, int maxChars, CancellationToken ct, out string? note)
    {
        note = null;
        var sb = new StringBuilder();
        using var doc = PdfDocument.Open(path, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
        int pages = doc.NumberOfPages, failed = 0;
        for (int i = 1; i <= pages; i++)
        {
            if (ct.IsCancellationRequested)
            {
                note = $"读取超时，只读取了前 {i - 1}/{pages} 页";
                break;
            }
            try
            {
                var page = doc.GetPage(i);
                foreach (var word in page.GetWords())
                    AppendWord(sb, word.Text);
            }
            catch (Exception e) when (e is not OperationCanceledException) { failed++; }
            sb.Append('\n');
            if (sb.Length >= maxChars)
            {
                note = $"文字过多，只读取了前 {i}/{pages} 页";
                return sb.ToString(0, maxChars);
            }
        }
        if (note == null && failed > 0) note = $"{failed} 页无法解析，已跳过";
        return sb.ToString();
    }

    // PdfPig often returns Chinese text one glyph per "word". Joining those with spaces would double
    // the text size (eating the character budget) for no benefit, so CJK neighbours are glued.
    internal static void AppendWord(StringBuilder sb, string word)
    {
        if (word.Length == 0) return;
        // A newline marks the end of a page (search results use it to find the page of a hit).
        if (word.AsSpan().IndexOfAny('\n', '\r') >= 0) word = word.Replace('\n', ' ').Replace('\r', ' ');
        if (sb.Length > 0)
        {
            char prev = sb[^1];
            if (!(prev == '\n' || (TextTokenizerShim.IsCjk(prev) && TextTokenizerShim.IsCjk(word[0]))))
                sb.Append(' ');
        }
        sb.Append(word);
    }
}

file static class TextTokenizerShim
{
    public static bool IsCjk(char c) => Search.TextTokenizer.IsCjk(c) || (c >= 0x3000 && c <= 0x303F) || (c >= 0xFF00 && c <= 0xFFEF);
}
