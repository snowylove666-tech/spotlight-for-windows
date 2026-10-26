using Spotlight.Core;
using Spotlight.Core.Indexing;
using Spotlight.Core.Search;

/// <summary>
/// 0.8.0: NTFS drives are followed through the change journal (read without administrator rights).
/// These run against the real journal of the drive holding the temp folder; they are skipped where
/// that drive has no journal (not NTFS, not Windows).
/// </summary>
public sealed class JournalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spotlight-080-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_dir, "root");
    private string Db => Path.Combine(_dir, "index.db");

    public JournalTests() => Directory.CreateDirectory(Root);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static Settings Quiet() => new() { PauseContentIndexingOnBattery = false };

    private static bool Journaled(string path)
    {
        using var j = UsnJournal.Open(path);
        return j != null;
    }

    private static void WaitFor(Func<bool> cond, int seconds = 15)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until) { if (cond()) return; Thread.Sleep(100); }
        Assert.True(cond(), "timed out");
    }

    private bool Found(string query, string name)
    {
        using var e = new SearchEngine(Db);
        return e.Search(query, limit: 100).Any(r => r.Name == name);
    }

    [Fact]
    public void TheCrawlRecordsFileIds()
    {
        if (!Journaled(Root)) return;
        File.WriteAllText(Path.Combine(Root, "a.md"), "x");
        var s = Quiet();
        using var store = new IndexStore(Db, s);
        using (var indexer = new Indexer(store, s, new[] { Root })) indexer.RunOnce();
        var fid = DirLister.Stat(Path.Combine(Root, "a.md"))!.Value.Fid;
        Assert.NotEqual(0, fid);
        Assert.Equal(Path.Combine(Root, "a.md"), store.FindByFid(Path.GetPathRoot(Root)!, fid)?.path, ignoreCase: true);
    }

    [Fact]
    public void CreatesRenamesMovesAndDeletesAreFollowed()
    {
        if (!Journaled(Root)) return;
        var s = Quiet();
        using var store = new IndexStore(Db, s);
        using var indexer = new Indexer(store, s, new[] { Root });
        indexer.Start();
        WaitFor(() => indexer.Status.Phase == IndexPhase.Idle);

        var a = Path.Combine(Root, "zebra notes.md");
        File.WriteAllText(a, "hello");
        WaitFor(() => Found("zebra", "zebra notes.md"));

        var b = Path.Combine(Root, "giraffe notes.md");
        File.Move(a, b);
        WaitFor(() => Found("giraffe", "giraffe notes.md") && !Found("zebra", "zebra notes.md"));

        // A folder moved in from outside the search locations brings its contents along.
        var outside = Path.Combine(_dir, "outside");
        Directory.CreateDirectory(Path.Combine(outside, "sub"));
        File.WriteAllText(Path.Combine(outside, "sub", "okapi.md"), "x");
        Directory.Move(outside, Path.Combine(Root, "moved in"));
        WaitFor(() => Found("okapi", "okapi.md"));

        File.Delete(b);
        WaitFor(() => !Found("giraffe", "giraffe notes.md"));
        Directory.Delete(Path.Combine(Root, "moved in"), true);
        WaitFor(() => !Found("okapi", "okapi.md"));
    }

    [Fact]
    public void ChangesWhileTheAppWasOffAreCaughtUpWithoutACrawl()
    {
        if (!Journaled(Root)) return;
        var s = Quiet();
        File.WriteAllText(Path.Combine(Root, "before.md"), "x");
        using (var store = new IndexStore(Db, s))
        using (var indexer = new Indexer(store, s, new[] { Root }))
        {
            indexer.Start();
            WaitFor(() => indexer.Status.Phase == IndexPhase.Idle && Found("before", "before.md"));
        }

        // The app is off: a file is added and one removed.
        File.WriteAllText(Path.Combine(Root, "while off.md"), "x");
        File.Delete(Path.Combine(Root, "before.md"));

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        using (var store = new IndexStore(Db, s))
        using (var indexer = new Indexer(store, s, new[] { Root }))
        {
            indexer.Start();
            WaitFor(() => Found("while", "while off.md") && !Found("before", "before.md"));
            Assert.Null(indexer.LastScan); // replayed from the journal: no full crawl ran
        }
    }

    [Fact]
    public void FilesInPlacesThatArentIndexedAreIgnored()
    {
        if (!Journaled(Root)) return;
        var s = Quiet();
        Directory.CreateDirectory(Path.Combine(Root, "node_modules"));
        using var store = new IndexStore(Db, s);
        using var indexer = new Indexer(store, s, new[] { Root });
        indexer.Start();
        WaitFor(() => indexer.Status.Phase == IndexPhase.Idle);
        File.WriteAllText(Path.Combine(Root, "node_modules", "kudu.md"), "x");
        File.WriteAllText(Path.Combine(_dir, "kudu outside.md"), "x");
        File.WriteAllText(Path.Combine(Root, "kudu inside.md"), "x");
        WaitFor(() => Found("kudu", "kudu inside.md"));
        Assert.False(Found("kudu", "kudu.md"));
        Assert.False(Found("kudu", "kudu outside.md"));
    }
}
