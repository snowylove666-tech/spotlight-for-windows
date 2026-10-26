using Spotlight.Core;
using Spotlight.Core.Indexing;
using Spotlight.Core.Search;

/// <summary>
/// 0.8.1 "收录全部文件名": folders that are normally skipped (node_modules, AppData…) and hidden files are
/// indexed by name only, ranked after the user's own files, and their text is never read.
/// </summary>
public sealed class AllNamesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spotlight-081-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_dir, "root");
    private string Db => Path.Combine(_dir, "index.db");

    public AllNamesTests()
    {
        Directory.CreateDirectory(Path.Combine(Root, "node_modules", "pkg"));
        File.WriteAllText(Path.Combine(Root, "node_modules", "pkg", "wombat crash.log"), "wombat");
        File.WriteAllText(Path.Combine(Root, "wombat notes.md"), "wombat");
        var hidden = Path.Combine(Root, "wombat hidden.md");
        File.WriteAllText(hidden, "x");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        // Like a drive root, which Windows marks hidden + system: what is under it is still the user's own.
        File.SetAttributes(Root, FileAttributes.Directory | FileAttributes.Hidden);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private List<SearchResult> Index(bool allNames)
    {
        var s = new Settings { PauseContentIndexingOnBattery = false, IndexAllNames = allNames, FullContentIndex = true };
        using (var store = new IndexStore(Db, s))
        using (var indexer = new Indexer(store, s, new[] { Root })) indexer.RunOnce();
        using var e = new SearchEngine(Db);
        return e.Search("wombat", limit: 50).ToList();
    }

    [Fact]
    public void OffSkipsThoseFiles()
    {
        var names = Index(false).Select(r => r.Name).ToList();
        Assert.Contains("wombat notes.md", names);
        Assert.DoesNotContain("wombat crash.log", names);
        Assert.DoesNotContain("wombat hidden.md", names);
    }

    [Fact]
    public void OnFindsThemByNameAfterTheUsersOwnFiles()
    {
        var results = Index(true);
        var names = results.Select(r => r.Name).ToList();
        Assert.Contains("wombat crash.log", names);
        Assert.Contains("wombat hidden.md", names);
        Assert.Equal("wombat notes.md", names[0]);
        Assert.False(results[0].Buried);
        Assert.True(results.Single(r => r.Name == "wombat crash.log").Buried);
        using var store = new IndexStore(Db, new Settings());
        Assert.True(store.Buried(Path.Combine(Root, "node_modules")));
        Assert.False(store.Buried(Root));
    }

    [Fact]
    public void TurningItOffAgainRemovesThem()
    {
        Index(true);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var names = Index(false).Select(r => r.Name).ToList();
        Assert.DoesNotContain("wombat crash.log", names);
        Assert.Contains("wombat notes.md", names);
    }
}
