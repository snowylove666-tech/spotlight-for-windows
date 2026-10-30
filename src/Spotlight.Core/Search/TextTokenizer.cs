using System.Text;

namespace Spotlight.Core.Search;

/// <summary>
/// Turns text into a token stream that SQLite's unicode61 tokenizer can index, and turns
/// user queries into FTS5 MATCH expressions.
///
/// unicode61 treats a run of CJK characters as a single token, which makes Chinese text
/// unsearchable. We therefore rewrite every CJK run into overlapping bigrams plus a trailing
/// unigram ("合同条款" -> "合同 同条 条款 款"). A query of n CJK characters becomes the phrase of
/// its bigrams, so any substring of length >= 2 is found, and a single character becomes a
/// prefix query that matches every bigram starting with it.
/// </summary>
public static class TextTokenizer
{
    public static bool IsCjk(char c) =>
        (c >= 0x4E00 && c <= 0x9FFF) ||  // CJK Unified Ideographs
        (c >= 0x3400 && c <= 0x4DBF) ||  // Extension A
        (c >= 0xF900 && c <= 0xFAFF) ||  // Compatibility Ideographs
        (c >= 0x3040 && c <= 0x30FF) ||  // Hiragana + Katakana
        (c >= 0xAC00 && c <= 0xD7AF);    // Hangul syllables

    /// <summary>Rewrites text for indexing. When <paramref name="splitWords"/> is set, camelCase
    /// and letter/digit boundaries are also split so "MyReport2024" matches "report" and "2024".</summary>
    public static string ForIndex(string text, bool splitWords = false)
    {
        var sb = new StringBuilder(text.Length * 2);
        var cjk = new List<char>();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (IsCjk(c))
            {
                cjk.Clear();
                // Collect a CJK run. Whitespace between two CJK characters is ignored because PDFs
                // and hard-wrapped text routinely break Chinese sentences across lines or glyphs.
                while (i < text.Length)
                {
                    if (IsCjk(text[i])) { cjk.Add(text[i]); i++; continue; }
                    if (char.IsWhiteSpace(text[i]))
                    {
                        int j = i;
                        while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
                        if (j < text.Length && IsCjk(text[j])) { i = j; continue; }
                    }
                    break;
                }
                AppendCjkRun(sb, cjk);
                continue;
            }

            if (splitWords && char.IsLetterOrDigit(c))
            {
                int start = i;
                while (i < text.Length && char.IsLetterOrDigit(text[i]) && !IsCjk(text[i])) i++;
                AppendWordWithParts(sb, text.AsSpan(start, i - start));
                continue;
            }

            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    private static void AppendCjkRun(StringBuilder sb, List<char> run)
    {
        sb.Append(' ');
        if (run.Count == 1) { sb.Append(run[0]).Append(' '); return; }
        for (int k = 0; k + 1 < run.Count; k++)
            sb.Append(run[k]).Append(run[k + 1]).Append(' ');
        sb.Append(run[^1]).Append(' ');
    }

    private static void AppendWordWithParts(StringBuilder sb, ReadOnlySpan<char> word)
    {
        sb.Append(' ').Append(word).Append(' ');
        var parts = SplitWordParts(word);
        if (parts.Count > 1)
            foreach (var p in parts) sb.Append(p).Append(' ');
    }

    internal static List<string> SplitWordParts(ReadOnlySpan<char> word)
    {
        var parts = new List<string>();
        int start = 0;
        for (int k = 1; k < word.Length; k++)
        {
            char a = word[k - 1], b = word[k];
            bool boundary =
                (char.IsLower(a) && char.IsUpper(b)) ||
                (char.IsLetter(a) && char.IsDigit(b)) ||
                (char.IsDigit(a) && char.IsLetter(b)) ||
                // "PDFReader" -> "PDF", "Reader"
                (k + 1 < word.Length && char.IsUpper(a) && char.IsUpper(b) && char.IsLower(word[k + 1]));
            if (boundary) { parts.Add(word[start..k].ToString()); start = k; }
        }
        parts.Add(word[start..].ToString());
        return parts;
    }

    /// <summary>Splits a query into its search terms: whitespace separated, CJK runs and
    /// alphanumeric runs kept apart, punctuation dropped.</summary>
    public static List<string> QueryTerms(string query)
    {
        var terms = new List<string>();
        var cur = new StringBuilder();
        bool curCjk = false;
        void Flush() { if (cur.Length > 0) { terms.Add(cur.ToString()); cur.Clear(); } }
        foreach (char c in query)
        {
            if (IsCjk(c))
            {
                if (!curCjk) Flush();
                curCjk = true;
                cur.Append(c);
            }
            else if (char.IsLetterOrDigit(c))
            {
                if (curCjk) Flush();
                curCjk = false;
                cur.Append(char.ToLowerInvariant(c));
            }
            else Flush();
        }
        Flush();
        return terms;
    }

    /// <summary>Builds an FTS5 MATCH expression requiring every term. The last Latin term is a
    /// prefix match (the user is probably still typing it); earlier ones are too, which keeps
    /// "ann rep" matching "annual_report". Returns null when the query has nothing searchable.</summary>
    /// <param name="anyTerm">"模糊匹配": a document needs only one of the words, not all of them.</param>
    public static string? ToFtsQuery(string query, bool anyTerm = false)
    {
        var terms = QueryTerms(query);
        if (terms.Count == 0) return null;
        var parts = new List<string>();
        foreach (var t in terms)
        {
            if (IsCjk(t[0]))
            {
                if (t.Length == 1) parts.Add(Quote(t) + "*");
                else
                {
                    var bigrams = new List<string>();
                    for (int k = 0; k + 1 < t.Length; k++) bigrams.Add(t.Substring(k, 2));
                    parts.Add(Quote(string.Join(' ', bigrams)));
                }
            }
            else parts.Add(Quote(t) + "*");
        }
        return string.Join(anyTerm ? " OR " : " AND ", parts);
    }

    /// <summary>
    /// A query typed without spaces that still splits into several terms ("19级专家", "DGX-Spark")
    /// means exactly that sequence: "19" and "级专家" somewhere apart in a document is not a match.
    /// Spaces ("19级 专家") or "模糊匹配" ask for the words separately.
    /// </summary>
    public static bool IsPhrase(string query, bool anyTerm = false)
    {
        query = query.Trim();
        return !anyTerm && !query.Any(char.IsWhiteSpace) && QueryTerms(query).Count > 1;
    }

    /// <summary>The FTS5 phrase for <see cref="IsPhrase"/> queries: the tokens <see cref="ForIndex"/>
    /// writes for that text, in a row. A CJK run is followed by its trailing single character when
    /// more follows it in the query; a last Latin term is a prefix (the user may still be typing it).</summary>
    public static string ToPhraseFts(string query)
    {
        var terms = QueryTerms(query);
        var tokens = new List<string>();
        for (int i = 0; i < terms.Count; i++)
        {
            var t = terms[i];
            bool last = i == terms.Count - 1;
            if (!IsCjk(t[0])) tokens.Add(t);
            else if (t.Length == 1) tokens.Add(t);
            else
            {
                for (int k = 0; k + 1 < t.Length; k++) tokens.Add(t.Substring(k, 2));
                if (!last) tokens.Add(t[^1].ToString());
            }
        }
        bool prefix = !IsCjk(terms[^1][0]) || terms[^1].Length == 1;
        return Quote(string.Join(' ', tokens)) + (prefix ? "*" : "");
    }

    /// <summary>What to look for (and mark) inside a matching document: the whole phrase for
    /// <see cref="IsPhrase"/> queries, else each term.</summary>
    public static List<string> HitTerms(string query, bool anyTerm = false) =>
        IsPhrase(query, anyTerm) ? new List<string> { query.Trim().ToLowerInvariant() } : QueryTerms(query);

    private static string Quote(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
}
