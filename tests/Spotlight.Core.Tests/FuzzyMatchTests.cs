using Spotlight.Core;
using Spotlight.Core.Indexing;
using Spotlight.Core.Search;

/// <summary>0.7.6 "模糊匹配": several words find files holding any of them, the fullest matches first.</summary>
public sealed class FuzzyMatchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spotlight-076-" + Guid.NewGuid().ToString("N"));
    private string Db => Path.Combine(_dir, "index.db");

    public FuzzyMatchTests()
    {
        var root = Path.Combine(_dir, "root");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "both.md"), "季度报告 苹果 香蕉 销量");
        File.WriteAllText(Path.Combine(root, "one.md"), "季度报告 苹果 销量");
        File.WriteAllText(Path.Combine(root, "none.md"), "季度报告 橘子");
        var s = new Settings { PauseContentIndexingOnBattery = false };
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
    public void AllWordsAreNeededByDefault()
    {
        using var e = new SearchEngine(Db);
        Assert.Equal(new[] { "both.md" }, e.Search("苹果 香蕉").Select(r => r.Name));
    }

    [Fact]
    public void FuzzyFindsAnyWordAndRanksFullerMatchesFirst()
    {
        using var e = new SearchEngine(Db);
        var results = e.Search("苹果 香蕉", new SearchFilter(AnyTerm: true));
        Assert.Equal(new[] { "both.md", "one.md" }, results.Select(r => r.Name));
        Assert.Equal((2, 2), (results[0].TermsMatched, results[0].TermsTotal));
        Assert.Equal((1, 2), (results[1].TermsMatched, results[1].TermsTotal));
    }
}
