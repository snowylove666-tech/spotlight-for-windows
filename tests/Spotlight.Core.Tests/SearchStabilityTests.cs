using System.Diagnostics;
using Spotlight.Core;
using Spotlight.Core.Indexing;
using Spotlight.Core.Search;

/// <summary>
/// 0.8.3: searching stays responsive. A search cancelled by the next keystroke stops its query at
/// once instead of making the next search wait; the "收录全部文件名" extras and the snippets of lower
/// rows can be fetched in a second step without changing what is found.
/// </summary>
public sealed class SearchStabilityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spotlight-083-" + Guid.NewGuid().ToString("N"));
    private string Db => Path.Combine(_dir, "index.db");

    public SearchStabilityTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>An index with many names (written directly: a crawl of this many files would be slow).</summary>
    private void ManyNames(int count, int buried)
    {
        using (new IndexStore(Db, new Settings())) { }
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Db);
        conn.Open();
        using var c = conn.CreateCommand();
        c.CommandText = $"""
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < {count})
            INSERT INTO files(path, name, name_lower, ext, kind, size, mtime, buried)
            SELECT 'C:\x\file number ' || i || '.bin', 'file number ' || i || '.bin', 'file number ' || i || '.bin', '.bin', 0, 1, 1, i <= {buried} FROM n;
            """;
        c.ExecuteNonQuery();
    }

    [Fact]
    public void ACancelledSearchStopsAtOnceAndTheNextOneWorks()
    {
        ManyNames(600_000, buried: 0);
        using var engine = new SearchEngine(Db);
        // How long the full scan takes here, to know the cancelled one stopped early.
        var sw = Stopwatch.StartNew();
        engine.Search("no such name anywhere", limit: 60);
        var full = sw.Elapsed;

        using var cts = new CancellationTokenSource();
        sw.Restart();
        cts.CancelAfter(TimeSpan.FromTicks(full.Ticks / 10));
        Assert.ThrowsAny<OperationCanceledException>(() => engine.Search("no such name anywhere either", SearchFilter.None, 60, cts.Token));
        Assert.True(sw.Elapsed < full * 0.7, $"cancelled after {sw.Elapsed.TotalMilliseconds:F0} ms, a full search takes {full.TotalMilliseconds:F0} ms");

        // The interrupt doesn't stick to the connection.
        Assert.Contains(engine.Search("number 123456", limit: 60), r => r.Name == "file number 123456.bin");
    }

    [Fact]
    public void AnAlreadyCancelledSearchThrowsAndLeavesTheEngineUsable()
    {
        ManyNames(1000, buried: 0);
        using var engine = new SearchEngine(Db);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => engine.Search("number 5", SearchFilter.None, 60, cts.Token));
        Assert.NotEmpty(engine.Search("number 5", limit: 60));
    }

    [Fact]
    public void ExtrasCanBeSearchedInASecondStep()
    {
        ManyNames(2000, buried: 1000); // 1..1000 are "all names" extras
        using var engine = new SearchEngine(Db);
        // "mber 150" is only a substring (no word of a name starts with it).
        var first = engine.Search("mber 150", SearchFilter.None with { ExtraNames = false }, 60);
        Assert.True(first.ExtraNamesSkipped);
        Assert.DoesNotContain(first, r => r.Name == "file number 150.bin");
        Assert.Contains(first, r => r.Name == "file number 1500.bin");

        var full = engine.Search("mber 150", SearchFilter.None, 60);
        Assert.False(full.ExtraNamesSkipped);
        Assert.Contains(full, r => r.Name == "file number 150.bin");
        Assert.Contains(full, r => r.Name == "file number 1500.bin");
    }

    [Fact]
    public void SnippetsFilledLaterMatchSnippetsFilledAtOnce()
    {
        var root = Path.Combine(_dir, "docs");
        Directory.CreateDirectory(root);
        for (int i = 0; i < 30; i++) File.WriteAllText(Path.Combine(root, $"note {i}.md"), $"line {i}\nthe capybara report number {i}\ncapybara again");
        var s = new Settings { PauseContentIndexingOnBattery = false };
        using (var store = new IndexStore(Db, s))
        using (var indexer = new Indexer(store, s, new[] { root })) indexer.RunOnce();

        using var engine = new SearchEngine(Db);
        var results = engine.Search("capybara", limit: 60);
        Assert.Equal(30, results.Count);
        Assert.NotNull(results.HitsPending);
        Assert.All(results.Take(12), r => Assert.NotNull(r.Snippet));
        Assert.All(results.HitsPending!, r => Assert.Null(r.Snippet));
        engine.FillHits(results);
        Assert.Null(results.HitsPending);
        Assert.All(results, r => { Assert.Contains("capybara", r.Snippet); Assert.Equal(2, r.HitCount); });
    }

    [Fact]
    public void NamesFirstThenText()
    {
        var root = Path.Combine(_dir, "docs");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "okapi plan.md"), "nothing here");
        File.WriteAllText(Path.Combine(root, "other.md"), "the okapi is mentioned only in this text");
        var s = new Settings { PauseContentIndexingOnBattery = false };
        using (var store = new IndexStore(Db, s))
        using (var indexer = new Indexer(store, s, new[] { root })) indexer.RunOnce();

        using var engine = new SearchEngine(Db);
        var names = engine.Search("okapi", SearchFilter.None with { Content = false }, 60);
        Assert.True(names.ContentSkipped);
        Assert.Equal(new[] { "okapi plan.md" }, names.Select(r => r.Name));

        var all = engine.Search("okapi", SearchFilter.None, 60);
        Assert.False(all.ContentSkipped);
        Assert.Contains(all, r => r.Name == "other.md" && r.ContentMatch);
        Assert.Equal("okapi plan.md", all[0].Name);
    }

    /// <summary>Every query a search runs on the files table uses an index: a plain scan reads all
    /// 1.7 million rows (seconds when not cached). The corpus statistic for ranking text hits did,
    /// and made the first text search every five minutes take up to nine seconds.</summary>
    [Fact]
    public void SearchQueriesNeverScanTheWholeFilesTable()
    {
        using (new IndexStore(Db, new Settings())) { }
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Db);
        conn.Open();
        var queries = new[]
        {
            "SELECT COUNT(*), AVG(content_chars) FROM files WHERE has_content = 1;",
            "SELECT id FROM files WHERE buried = 0 AND instr(name_lower, 'x') > 0 LIMIT 2000;",
            "SELECT id FROM files WHERE buried = 1 AND instr(name_lower, 'x') > 0 LIMIT 2000;",
            "SELECT id FROM files WHERE buried = 0 AND name_lower >= 'a' AND name_lower < 'b' LIMIT 300;",
            "SELECT id FROM files WHERE buried = 1 AND name_lower >= 'a' AND name_lower < 'b' LIMIT 300;",
            "SELECT max(length(name_lower)) FROM files WHERE buried = 0;",
        };
        foreach (var sql in queries)
        {
            using var c = conn.CreateCommand();
            c.CommandText = "EXPLAIN QUERY PLAN " + sql;
            using var r = c.ExecuteReader();
            var plan = new List<string>();
            while (r.Read()) plan.Add(r.GetString(3));
            Assert.DoesNotContain(plan, p => p == "SCAN files");
        }
    }

    /// <summary>The window's steps reuse the name candidates: the result must equal one full search,
    /// also when 模糊匹配 is switched between searches of the same words.</summary>
    [Fact]
    public void StepsGiveTheSameResultAsOneSearch()
    {
        var root = Path.Combine(_dir, "docs");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "tapir budget.md"), "x");
        File.WriteAllText(Path.Combine(root, "tapir.md"), "the budget is here");
        File.WriteAllText(Path.Combine(root, "budget.md"), "x");
        var s = new Settings { PauseContentIndexingOnBattery = false };
        using (var store = new IndexStore(Db, s))
        using (var indexer = new Indexer(store, s, new[] { root })) indexer.RunOnce();

        List<string> Names(SearchResults r) => r.Select(x => x.Name).ToList();
        using (var fresh = new SearchEngine(Db))
        using (var stepped = new SearchEngine(Db))
        {
            foreach (bool any in new[] { false, true, false })
            {
                var f = SearchFilter.None with { AnyTerm = any };
                stepped.Search("tapir budget", f with { Content = false, ExtraNames = false }, 60);
                stepped.Search("tapir budget", f with { ExtraNames = false }, 60);
                Assert.Equal(Names(fresh.Search("tapir budget", f, 60)), Names(stepped.Search("tapir budget", f, 60)));
            }
        }
    }
}
