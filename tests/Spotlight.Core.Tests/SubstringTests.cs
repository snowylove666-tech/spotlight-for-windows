using Spotlight.Core;
using Spotlight.Core.Indexing;
using Spotlight.Core.Search;

/// <summary>0.7.9: a name holding the query inside a word is found even among many whole-word matches (like Everything).</summary>
public sealed class SubstringTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spotlight-079-" + Guid.NewGuid().ToString("N"));
    private string Db => Path.Combine(_dir, "index.db");

    public SubstringTests()
    {
        var root = Path.Combine(_dir, "root");
        Directory.CreateDirectory(root);
        for (int i = 0; i < 80; i++) File.WriteAllText(Path.Combine(root, $"report {i}.md"), "x");
        File.WriteAllText(Path.Combine(root, "annualreport2026.pdf"), "x");
        var s = new Settings { PauseContentIndexingOnBattery = false, IndexContents = false };
        using var store = new IndexStore(Db, s);
        using var indexer = new Indexer(store, s, new[] { root });
        indexer.RunOnce();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void FindsTheQueryInsideAWordAmongManyWordMatches()
    {
        using var e = new SearchEngine(Db);
        var results = e.Search("report", limit: 200);
        Assert.Contains(results, r => r.Name == "annualreport2026.pdf");
        Assert.Contains(e.Search("port2026"), r => r.Name == "annualreport2026.pdf");
    }

    [Fact]
    public void CountsFollowChangesBetweenRealCounts()
    {
        var s = new Settings { PauseContentIndexingOnBattery = false };
        var root = Path.Combine(_dir, "root");
        File.WriteAllText(Path.Combine(root, "note.md"), "some words");
        using var store = new IndexStore(Db, s);
        using (var indexer = new Indexer(store, s, new[] { root })) indexer.RunOnce();
        store.Counts(exact: true); // the cached starting point
        store.DeletePath(Path.Combine(root, "note.md"));      // a file with text goes
        store.DeletePath(Path.Combine(root, "report 1.md"));  // and one without
        var rec = store.Find(Path.Combine(root, "report 2.md"))!;
        store.SetContents(new[] { (rec.Id, 1L, (string?)"now with text", (string?)null) });
        Assert.Equal(store.Counts(exact: true), store.Counts());
    }
}
