using Spotlight.Core;
using Spotlight.Core.Indexing;
using Spotlight.Core.Search;

/// <summary>
/// 0.8.2: a full crawl compares with what is indexed and writes only the differences (with the MFT
/// service, a rescan of a whole drive is then a few seconds).
/// </summary>
public sealed class RecrawlTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spotlight-082-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_dir, "root");
    private string Db => Path.Combine(_dir, "index.db");

    public RecrawlTests()
    {
        Directory.CreateDirectory(Path.Combine(Root, "sub"));
        for (int i = 0; i < 20; i++) File.WriteAllText(Path.Combine(Root, "sub", $"quokka {i}.md"), "x");
        File.WriteAllText(Path.Combine(Root, "keep.md"), "old text");
        File.WriteAllText(Path.Combine(Root, "gone.md"), "x");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private ScanReport Crawl(Settings s)
    {
        using var store = new IndexStore(Db, s);
        using var indexer = new Indexer(store, s, new[] { Root });
        indexer.RunOnce();
        return indexer.LastScan!;
    }

    private List<string> Find(string q)
    {
        using var e = new SearchEngine(Db);
        return e.Search(q, limit: 100).Select(r => r.Name).ToList();
    }

    [Fact]
    public void ASecondCrawlWritesOnlyWhatChanged()
    {
        var s = new Settings { PauseContentIndexingOnBattery = false };
        var first = Crawl(s);
        Assert.Equal(0, first.Unchanged);

        File.Delete(Path.Combine(Root, "gone.md"));
        File.WriteAllText(Path.Combine(Root, "new.md"), "x");
        File.WriteAllText(Path.Combine(Root, "keep.md"), "fresh pangolin text");
        File.SetLastWriteTimeUtc(Path.Combine(Root, "keep.md"), DateTime.UtcNow.AddMinutes(1));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var second = Crawl(s);
        // The 20 files in sub. (sub itself may count as changed: its time in the root's listing is
        // refreshed lazily by NTFS after its files were created.)
        Assert.True(second.Unchanged >= 20, $"unchanged {second.Unchanged}");
        Assert.DoesNotContain("gone.md", Find("gone"));
        Assert.Contains("new.md", Find("new"));
        Assert.Contains("keep.md", Find("pangolin")); // the changed file's text was read again
        Assert.Equal(20, Find("quokka").Count(n => n.StartsWith("quokka")));
    }

    [Fact]
    public void FilesNowFilteredOutAreRemovedWithoutBeingRewritten()
    {
        Crawl(new Settings { PauseContentIndexingOnBattery = false });
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Crawl(new Settings { PauseContentIndexingOnBattery = false, ExcludedPaths = { Path.Combine(Root, "sub") } });
        Assert.DoesNotContain(Find("quokka"), n => n.StartsWith("quokka"));
        Assert.Contains("keep.md", Find("keep"));
    }

    [Fact]
    public void ARootNestedInAnotherIsNotInsertedTwice()
    {
        // Like the Start Menu under C:\ — a new index, then a crawl that finds everything unchanged.
        var s = new Settings { PauseContentIndexingOnBattery = false, IndexContents = false };
        for (int run = 0; run < 2; run++)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            using var store = new IndexStore(Db, s);
            using var indexer = new Indexer(store, s, new[] { Path.Combine(Root, "sub"), Root });
            indexer.RunOnce();
            Assert.Equal(20, Find("quokka").Count(n => n.StartsWith("quokka")));
        }
    }
}
