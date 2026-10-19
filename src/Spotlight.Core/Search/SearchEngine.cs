using Microsoft.Data.Sqlite;
using Spotlight.Core.Indexing;

namespace Spotlight.Core.Search;

public sealed class SearchResult
{
    public long Id { get; init; }
    public required string Path { get; init; }
    public required string Name { get; init; }
    public FileKind Kind { get; init; }
    public long Size { get; init; }
    public DateTime Modified { get; init; }
    public double Score { get; set; }
    /// <summary>True when the query was found in the document text (not only in the name).</summary>
    public bool ContentMatch { get; set; }
    public bool NameMatch { get; set; }
    /// <summary>Text around the first content hit, for display.</summary>
    public string? Snippet { get; set; }
    /// <summary>PDFs: 1-based page of the first content hit (the extractor ends every page with a newline).</summary>
    public int? Page { get; set; }
    /// <summary>How many times the query terms occur in the document text (content hits only).</summary>
    public int HitCount { get; set; }
    /// <summary>A document whose text has not been read yet (so a content search can't find it yet).</summary>
    public bool ContentPending { get; init; }
    internal int ContentChars { get; init; }
    /// <summary>For calculator results: the computed value.</summary>
    public string? Value { get; init; }
    internal int LaunchCount { get; init; }
    internal long LastLaunch { get; init; }

    public override string ToString() => $"{Score,7:F1}  {FileKinds.DisplayName(Kind),-4} {Name}  ({Path})";
}

public enum SortBy { Relevance, Modified, Name, Size, Kind }

/// <summary>Optional narrowing and ordering chosen in the search window.</summary>
public sealed record SearchFilter(IReadOnlyCollection<FileKind>? Kinds = null, SortBy Sort = SortBy.Relevance, bool Descending = true)
{
    public static SearchFilter None { get; } = new();

    /// <summary>The usual direction for each order: newest and largest first, names A→Z.</summary>
    public static bool DefaultDescending(SortBy s) => s is SortBy.Modified or SortBy.Size or SortBy.Relevance;
}

/// <summary>
/// Answers queries against the index. Ranking combines, in order of weight:
///  how well the file name matches (exact > prefix > word prefix > substring > initials),
///  what the user opened before for the same query, how often and how recently a file was opened,
///  the kind of item (apps first), full-text relevance (BM25), modification recency and path depth.
/// </summary>
public sealed class SearchEngine : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _lock = new();

    public SearchEngine(string dbPath)
    {
        _conn = IndexStore.Open(dbPath, readOnly: true);
    }

    public List<SearchResult> Search(string query, int limit = 60, CancellationToken ct = default) =>
        Search(query, SearchFilter.None, limit, ct);

    /// <summary>Like <see cref="Search(string,int,CancellationToken)"/>, keeping only the given kinds.
    /// With an order other than relevance, the most relevant matches (a few hundred) are re-ordered,
    /// so "by date" means "the newest of the files that really match".</summary>
    public List<SearchResult> Search(string query, SearchFilter filter, int limit = 60, CancellationToken ct = default)
    {
        query = query.Trim();
        var results = new List<SearchResult>();
        if (query.Length == 0) return results;

        if (filter.Kinds == null && Calculator.TryEvaluate(query, out var value))
        {
            // An arithmetic expression is not a file search; its digits would only match noise.
            results.Add(new SearchResult { Id = -1, Path = "", Name = $"{query} = {value}", Kind = FileKind.Calculator, Score = 5000, Value = value });
            return results;
        }

        var fts = TextTokenizer.ToFtsQuery(query);
        if (fts == null) return results;
        var terms = TextTokenizer.QueryTerms(query);
        string qLower = query.ToLowerInvariant();

        lock (_lock)
        {
            var byId = new Dictionary<long, SearchResult>();

            // 1. Candidates by name: FTS token match, plus whole-name prefix via the B-tree index
            //    (so "a" still finds "a.txt" even when thousands of names contain an "a…" word).
            var ids = new HashSet<long>();
            ids.UnionWith(QueryIds("SELECT rowid FROM names_fts WHERE names_fts MATCH $q LIMIT 3000;", ("$q", fts)));
            ids.UnionWith(QueryIds("SELECT id FROM files WHERE name_lower >= $lo AND name_lower < $hi LIMIT 300;", ("$lo", qLower), ("$hi", qLower + '￿')));
            // Few hits: also try plain substring ("port" inside "report") which token matching can't do.
            if (ids.Count < 50 && qLower.Length >= 2)
                ids.UnionWith(QueryIds("SELECT id FROM files WHERE instr(name_lower, $q) > 0 LIMIT 300;", ("$q", qLower)));
            ct.ThrowIfCancellationRequested();

            foreach (var r in LoadRows(ids)) byId[r.Id] = r;
            foreach (var r in byId.Values)
            {
                r.Score = NameScore(r, qLower, terms);
                r.NameMatch = r.Score > 0;
            }

            // 2. Full-text candidates in BM25 order. A single character would match most documents and
            //    only cost time, so content search starts at two characters.
            if (terms.Sum(t => t.Length) >= 2)
            {
                var hits = QueryHits("SELECT rowid, bm25(content_fts) FROM content_fts WHERE content_fts MATCH $q ORDER BY rank LIMIT 1000;", fts);
                ct.ThrowIfCancellationRequested();
                foreach (var r in LoadRows(hits.Select(h => h.id).Where(id => !byId.ContainsKey(id)))) byId[r.Id] = r;
                var scores = ContentScores(hits, byId, terms.Count == 1 ? fts : null);
                for (int i = 0; i < hits.Count; i++)
                {
                    if (!byId.TryGetValue(hits[i].id, out var r)) continue;
                    r.ContentMatch = true;
                    // Content relevance is worth less than a good name match.
                    r.Score = r.NameMatch ? r.Score + scores[i] * 0.35 : scores[i];
                }

                // 2b. Some words in the name, the rest in the text ("课件 弗里德曼").
                if (terms.Count > 1) AddNamePlusContent(terms, byId, ct);
            }

            // 3. Usage: picks for this query (and longer queries starting with it) plus frecency.
            var picks = LoadPicks(IndexStore.NormalizeQuery(query));
            long now = DateTime.UtcNow.Ticks;
            foreach (var r in byId.Values)
            {
                if (r.Score <= 0) continue;
                if (picks.TryGetValue(r.Id, out var pc)) r.Score += Math.Min(450, 220 * Math.Log2(1 + pc));
                r.Score += Bonus(r, now);
            }

            var ranked = byId.Values.Where(r => r.Score > 0 && (filter.Kinds == null || filter.Kinds.Contains(r.Kind)))
                .OrderByDescending(r => r.Score).ThenBy(r => r.Name.Length).ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase);
            var top = filter.Sort == SortBy.Relevance && filter.Descending
                ? ranked.Take(limit).ToList()
                : Order(ranked.Take(Math.Max(limit, 300)), filter).Take(limit).ToList();

            foreach (var r in top.Where(r => r.ContentMatch)) FillHit(r, terms);
            results.AddRange(top);
        }
        return results;
    }

    // ---------------------------------------------------------------- scoring

    private static readonly StringComparer NameOrder = StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), true);

    private static IEnumerable<SearchResult> Order(IEnumerable<SearchResult> items, SearchFilter f)
    {
        var list = items.ToList();
        // Stable sorts: ties keep their relevance order.
        IOrderedEnumerable<SearchResult> sorted = f.Sort switch
        {
            SortBy.Modified => f.Descending ? list.OrderByDescending(r => r.Modified) : list.OrderBy(r => r.Modified),
            SortBy.Size => f.Descending ? list.OrderByDescending(r => r.Size) : list.OrderBy(r => r.Size),
            SortBy.Name => f.Descending ? list.OrderByDescending(r => r.Name, NameOrder) : list.OrderBy(r => r.Name, NameOrder),
            SortBy.Kind => f.Descending
                ? list.OrderByDescending(r => FileKinds.DisplayName(r.Kind), NameOrder).ThenByDescending(r => System.IO.Path.GetExtension(r.Name), StringComparer.OrdinalIgnoreCase)
                : list.OrderBy(r => FileKinds.DisplayName(r.Kind), NameOrder).ThenBy(r => System.IO.Path.GetExtension(r.Name), StringComparer.OrdinalIgnoreCase),
            _ => list.OrderBy(r => -r.Score), // relevance, reversed below when ascending
        };
        return f.Sort == SortBy.Relevance && !f.Descending ? list.OrderBy(r => r.Score) : sorted;
    }

    private (long n, double avgChars, DateTime at) _corpus;

    /// <summary>
    /// Content score (120..340) for each BM25 hit. BM25 divides by document length, so a 1000-page
    /// course book that mentions a name eleven times ranks below hundreds of one-page notes that
    /// mention it once. For single-term queries the term frequency is recovered from the BM25 score
    /// (its formula is known; only the document length is approximated by its character count) and the
    /// score grows with the number of mentions instead. Other queries keep BM25 order.
    /// </summary>
    private double[] ContentScores(List<(long id, double bm25)> hits, Dictionary<long, SearchResult> byId, string? singleTermFts)
    {
        var scores = new double[hits.Count];
        for (int i = 0; i < hits.Count; i++) scores[i] = 120 + 200 * Math.Pow(0.97, i);
        if (singleTermFts == null || hits.Count == 0) return scores;

        if (_corpus.n == 0 || DateTime.UtcNow - _corpus.at > TimeSpan.FromMinutes(5))
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT COUNT(*), AVG(content_chars) FROM files WHERE has_content = 1;";
            using var r = c.ExecuteReader();
            if (r.Read()) _corpus = (r.GetInt64(0), r.IsDBNull(1) ? 0 : r.GetDouble(1), DateTime.UtcNow);
        }
        long matching = hits.Count < 1000 ? hits.Count : Count("SELECT COUNT(*) FROM content_fts WHERE content_fts MATCH $q;", singleTermFts);
        // Same IDF as SQLite's bm25(), including its floor for terms found in most documents.
        double idf = Math.Max(1e-6, Math.Log((_corpus.n - matching + 0.5) / (matching + 0.5)));
        if (_corpus.avgChars <= 0) return scores;
        double best = hits.Max(h => -h.bm25);
        for (int i = 0; i < hits.Count; i++)
        {
            if (!byId.TryGetValue(hits[i].id, out var row)) continue;
            double tf = EstimateTermFrequency(-hits[i].bm25 / idf, row.ContentChars / _corpus.avgChars);
            double mentions = Math.Min(1, Math.Log2(1 + tf) / Math.Log2(17)); // 16+ mentions: full marks
            scores[i] = 120 + 200 * mentions + 20 * (-hits[i].bm25 / best);
        }
        return scores;
    }

    /// <summary>Inverts BM25 (k1 = 1.2, b = 0.75) for one term: <paramref name="r"/> is score / idf,
    /// <paramref name="relativeLength"/> the document length over the average.</summary>
    internal static double EstimateTermFrequency(double r, double relativeLength)
    {
        const double k1 = 1.2, b = 0.75;
        double k = k1 * (1 - b + b * Math.Max(0, relativeLength));
        r = Math.Clamp(r, 0, (k1 + 1) * 0.98);
        return Math.Max(1, r * k / (k1 + 1 - r));
    }

    /// <summary>Documents whose name holds some query terms and whose text holds the others.</summary>
    private void AddNamePlusContent(List<string> terms, Dictionary<long, SearchResult> byId, CancellationToken ct)
    {
        var byRest = new Dictionary<string, List<long>>();
        var rows = new Dictionary<long, SearchResult>();
        foreach (var t in terms)
        {
            var nameFts = TextTokenizer.ToFtsQuery(t);
            if (nameFts == null) continue;
            foreach (var r in LoadRows(QueryIds("SELECT rowid FROM names_fts WHERE names_fts MATCH $q LIMIT 2000;", ("$q", nameFts)).Where(id => !rows.ContainsKey(id))))
                rows[r.Id] = r;
        }
        foreach (var r in rows.Values)
        {
            if (byId.TryGetValue(r.Id, out var known) && (known.ContentMatch || known.Score >= 420)) continue;
            var name = r.Name.ToLowerInvariant();
            var rest = terms.Where(t => !name.Contains(t, StringComparison.Ordinal)).ToList();
            if (rest.Count == 0 || rest.Count == terms.Count) continue;
            var key = string.Join(' ', rest);
            if (!byRest.TryGetValue(key, out var list)) byRest[key] = list = new List<long>();
            list.Add(r.Id);
        }
        foreach (var (rest, ids) in byRest)
        {
            ct.ThrowIfCancellationRequested();
            var restFts = TextTokenizer.ToFtsQuery(rest);
            if (restFts == null) continue;
            foreach (var chunk in ids.Chunk(500))
                foreach (var id in QueryIds($"SELECT rowid FROM content_fts WHERE content_fts MATCH $q AND rowid IN ({string.Join(',', chunk)});", ("$q", restFts)))
                {
                    var r = byId.TryGetValue(id, out var known) ? known : byId[id] = rows[id];
                    r.ContentMatch = true;
                    r.NameMatch = true;
                    // Below a name holding every term, above most text-only hits.
                    r.Score = Math.Max(r.Score, 380);
                }
        }
    }

    internal static double NameScore(SearchResult r, string q, List<string> terms)
    {
        string name = r.Name.ToLowerInvariant();
        string stem = System.IO.Path.GetFileNameWithoutExtension(name);
        if (r.Kind == FileKind.Folder || r.Kind == FileKind.App) stem = name;
        double ratio = Math.Min(1.0, (double)q.Length / Math.Max(1, stem.Length));

        if (stem == q) return 1000;
        if (name == q) return 980;
        if (stem.StartsWith(q, StringComparison.Ordinal)) return 800 + 100 * ratio;
        if (WordPrefix(stem, q)) return 650 + 100 * ratio;
        if (stem.Contains(q, StringComparison.Ordinal)) return 520 + 80 * ratio;
        if (terms.Count > 1 && terms.All(t => name.Contains(t, StringComparison.Ordinal))) return 470 + 80 * ratio;
        if (Initials(r.Name).StartsWith(q, StringComparison.Ordinal) && q.Length >= 2) return 600 + 50 * ratio;
        if (terms.All(t => name.Contains(t, StringComparison.Ordinal))) return 420;
        // Matched through tokens only (e.g. the extension "pdf"): weak.
        return terms.Any(t => name.Contains(t, StringComparison.Ordinal)) ? 200 : 0;
    }

    private static bool WordPrefix(string name, string q)
    {
        for (int i = 1; i < name.Length; i++)
        {
            char prev = name[i - 1];
            bool boundary = !char.IsLetterOrDigit(prev) || TextTokenizer.IsCjk(prev) != TextTokenizer.IsCjk(name[i]);
            if (boundary && string.CompareOrdinal(name, i, q, 0, q.Length) == 0) return true;
        }
        return false;
    }

    /// <summary>First letters of each word, so "vsc" finds "Visual Studio Code".</summary>
    internal static string Initials(string name)
    {
        var chars = new List<char>();
        foreach (var word in name.Split(new[] { ' ', '_', '-', '.', '(', ')', '[', ']' }, StringSplitOptions.RemoveEmptyEntries))
            foreach (var part in TextTokenizer.SplitWordParts(word))
                if (part.Length > 0 && char.IsLetterOrDigit(part[0])) chars.Add(char.ToLowerInvariant(part[0]));
        return new string(chars.ToArray());
    }

    private static double Bonus(SearchResult r, long nowTicks)
    {
        double b = r.Kind switch
        {
            FileKind.App => 260,
            FileKind.Folder => 25,
            FileKind.Pdf or FileKind.Document or FileKind.Spreadsheet or FileKind.Presentation => 40,
            _ => 0,
        };
        // Uninstaller shortcuts are almost never what you want.
        if (r.Kind == FileKind.App && (r.Name.StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase) || r.Name.StartsWith("卸载")))
            b -= 400;

        double ageDays = TimeSpan.FromTicks(Math.Max(0, nowTicks - r.Modified.Ticks)).TotalDays;
        if (ageDays < 7) b += 40;
        else if (ageDays < 30) b += 20;

        if (r.LaunchCount > 0)
        {
            double sinceLaunch = TimeSpan.FromTicks(Math.Max(0, nowTicks - r.LastLaunch)).TotalDays;
            double decay = Math.Pow(0.5, sinceLaunch / 14); // half-life of two weeks
            b += Math.Min(300, 120 * Math.Log2(1 + r.LaunchCount) * (0.3 + 0.7 * decay));
        }

        int depth = r.Path.Count(c => c == System.IO.Path.DirectorySeparatorChar);
        b -= Math.Min(60, Math.Max(0, depth - 4) * 6);
        return b;
    }

    // ---------------------------------------------------------------- data access

    private List<long> QueryIds(string sql, params (string, object)[] args)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = sql;
        foreach (var (k, v) in args) c.Parameters.AddWithValue(k, v);
        var list = new List<long>();
        try
        {
            using var r = c.ExecuteReader();
            while (r.Read()) list.Add(r.GetInt64(0));
        }
        catch (SqliteException) { } // malformed FTS expression from unusual input: treat as no match
        return list;
    }

    private List<(long id, double bm25)> QueryHits(string sql, string fts)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = sql;
        c.Parameters.AddWithValue("$q", fts);
        var list = new List<(long, double)>();
        try
        {
            using var r = c.ExecuteReader();
            while (r.Read()) list.Add((r.GetInt64(0), r.GetDouble(1)));
        }
        catch (SqliteException) { } // malformed FTS expression from unusual input: treat as no match
        return list;
    }

    private long Count(string sql, string fts)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = sql;
        c.Parameters.AddWithValue("$q", fts);
        try { return Convert.ToInt64(c.ExecuteScalar()); }
        catch (SqliteException) { return 0; }
    }

    private List<SearchResult> LoadRows(IEnumerable<long> ids)
    {
        var list = new List<SearchResult>();
        foreach (var chunk in ids.Chunk(500))
        {
            using var c = _conn.CreateCommand();
            c.CommandText = $"SELECT id, path, name, kind, size, mtime, launch_count, last_launch, content_chars, wants_content = 1 AND content_mtime <> mtime FROM files WHERE id IN ({string.Join(',', chunk)});";
            using var r = c.ExecuteReader();
            while (r.Read())
                list.Add(new SearchResult
                {
                    Id = r.GetInt64(0),
                    Path = r.GetString(1),
                    Name = r.GetString(2),
                    Kind = (FileKind)r.GetInt32(3),
                    Size = r.GetInt64(4),
                    Modified = new DateTime(r.GetInt64(5), DateTimeKind.Utc),
                    LaunchCount = r.GetInt32(6),
                    LastLaunch = r.GetInt64(7),
                    ContentChars = r.GetInt32(8),
                    ContentPending = r.GetInt64(9) == 1,
                });
        }
        return list;
    }

    private Dictionary<long, long> LoadPicks(string normalizedQuery)
    {
        var d = new Dictionary<long, long>();
        if (normalizedQuery.Length == 0) return d;
        using var c = _conn.CreateCommand();
        c.CommandText = "SELECT file_id, SUM(count) FROM picks WHERE query >= $lo AND query < $hi GROUP BY file_id;";
        c.Parameters.AddWithValue("$lo", normalizedQuery);
        c.Parameters.AddWithValue("$hi", normalizedQuery + '￿');
        using var r = c.ExecuteReader();
        while (r.Read()) d[r.GetInt64(0)] = r.GetInt64(1);
        return d;
    }

    private void FillHit(SearchResult r, List<string> terms)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = "SELECT data FROM texts WHERE id = $id;";
        c.Parameters.AddWithValue("$id", r.Id);
        if (c.ExecuteScalar() is not byte[] data) return;
        var text = IndexStore.Decompress(data);
        r.Snippet = Snippet(text, terms);
        r.HitCount = CountHits(text, terms);
        // Indexes built before 0.4.0 kept no newlines; the app then looks the page up in the PDF itself.
        if (r.Kind != FileKind.Pdf || text.IndexOf('\n') < 0) return;
        int at = FirstHit(text, terms, out _);
        if (at >= 0) r.Page = 1 + text.AsSpan(0, at).Count('\n');
    }

    /// <summary>Occurrences of the terms in the text, for "11 处" in the result list.</summary>
    internal static int CountHits(string text, List<string> terms)
    {
        int n = 0;
        foreach (var t in terms.Where(t => t.Length > 0))
            for (int i = text.IndexOf(t, StringComparison.OrdinalIgnoreCase); i >= 0; i = text.IndexOf(t, i + t.Length, StringComparison.OrdinalIgnoreCase))
                n++;
        return n;
    }

    internal static int FirstHit(string text, List<string> terms, out int length)
    {
        int best = -1;
        length = 0;
        foreach (var t in terms)
        {
            int i = text.IndexOf(t, StringComparison.OrdinalIgnoreCase);
            if (i >= 0 && (best < 0 || i < best)) { best = i; length = t.Length; }
        }
        return best;
    }

    internal static string? Snippet(string text, List<string> terms, int radius = 60)
    {
        int best = FirstHit(text, terms, out int len);
        string s;
        if (best < 0) s = text.Length > radius * 2 ? text[..(radius * 2)] + "…" : text;
        else
        {
            int start = Math.Max(0, best - radius);
            int end = Math.Min(text.Length, best + len + radius);
            s = (start > 0 ? "…" : "") + text[start..end] + (end < text.Length ? "…" : "");
        }
        return s.Replace('\n', ' ');
    }

    public void ReleaseMemory()
    {
        lock (_lock)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "PRAGMA shrink_memory;";
            c.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        lock (_lock) _conn.Dispose();
    }
}
