using Spotlight.Core;
using Spotlight.Core.Indexing;

/// <summary>0.7.5: "重新扫描" reports what it found, so the user can see it ran and what changed.</summary>
public sealed class ScanReportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spotlight-075-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_dir, "root");
    private string Db => Path.Combine(_dir, "index.db");

    public ScanReportTests() => Directory.CreateDirectory(Root);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private ScanReport Scan(Settings s)
    {
        using var store = new IndexStore(Db, s);
        using var indexer = new Indexer(store, s, new[] { Root, Path.Combine(_dir, "gone") });
        ScanReport? raised = null;
        indexer.ScanFinished += r => raised = r;
        indexer.RunOnce();
        Assert.Same(indexer.LastScan, raised);
        Assert.True(store.SizeBytes() > 0);
        return raised!;
    }

    [Fact]
    public void ReportsAddedRemovedAndMissingLocations()
    {
        var s = new Settings { PauseContentIndexingOnBattery = false };
        File.WriteAllText(Path.Combine(Root, "a.md"), "alpha");
        File.WriteAllText(Path.Combine(Root, "b.md"), "beta");

        var first = Scan(s);
        Assert.Equal(0, first.FilesBefore);
        Assert.True(first.Added >= 2);
        Assert.Contains(Path.Combine(_dir, "gone"), first.MissingRoots);

        File.Delete(Path.Combine(Root, "b.md"));
        File.WriteAllText(Path.Combine(Root, "c.md"), "gamma");
        var second = Scan(s);
        Assert.Equal(first.FilesAfter, second.FilesBefore);
        Assert.Equal(1, second.Added);
        Assert.Equal(1, second.Removed);
        Assert.Equal(second.FilesBefore, second.FilesAfter);
    }
}
