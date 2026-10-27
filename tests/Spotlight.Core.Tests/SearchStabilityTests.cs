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
}
