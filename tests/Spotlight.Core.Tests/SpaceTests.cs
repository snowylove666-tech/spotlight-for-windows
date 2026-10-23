using Microsoft.Data.Sqlite;
using Spotlight.Core;
using Spotlight.Core.Indexing;
using Spotlight.Core.Search;

/// <summary>Regressions for 0.5.0: text only for documents, name-only folders, moving the index.</summary>
public sealed class SpaceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spotlight-space-" + Guid.NewGuid().ToString("N"));
    private string Db => Path.Combine(_dir, "index.db");
    private string Root => Path.Combine(_dir, "files");

    public SpaceTests() => Directory.CreateDirectory(Root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private void Write(string rel, string text)
    {
        var p = Path.Combine(Root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
    }

    private void Index(Settings s, string? db = null)
    {
        using var store = new IndexStore(db ?? Db, s);
        using var indexer = new Indexer(store, s, new[] { Root });
        indexer.RunOnce();
    }

    private static Settings Everything() => new()
    {
        PauseContentIndexingOnBattery = false,
        ContentCategories = Enum.GetNames<ContentCategory>().Where(n => n != "None").ToList(),
    };

    // Documents only, as before 0.7.0 made spreadsheets a default too.
    private static Settings DocumentsOnly() => new()
    {
        PauseContentIndexingOnBattery = false,
        ContentCategories = new() { "Pdf", "Word", "Presentation", "Text" },
    };

    [Fact]
    public void OnlyDocumentsHaveTheirTextReadByDefault()
    {
        var s = new Settings();
        Assert.True(s.WantsContentFor(".pdf", @"D:\a\b.pdf"));
        Assert.True(s.WantsContentFor(".docx", @"D:\a\b.docx"));
        Assert.True(s.WantsContentFor(".pptx", @"D:\a\b.pptx"));
        Assert.True(s.WantsContentFor(".md", @"D:\a\b.md"));
        Assert.True(s.WantsContentFor(".xlsx", @"D:\a\b.xlsx"));
        Assert.True(s.WantsContentFor(".xls", @"D:\a\b.xls"));
        Assert.True(s.WantsContentFor(".csv", @"D:\a\b.csv"));
        Assert.False(s.WantsContentFor(".log", @"D:\a\b.log"));
        Assert.False(s.WantsContentFor(".json", @"D:\a\b.json"));
        Assert.False(s.WantsContentFor(".py", @"D:\a\b.py"));
        Assert.Equal(ContentCategory.Code, Settings.CategoryOf(".py"));
        Assert.Equal(ContentCategory.None, Settings.CategoryOf(".exe"));
    }

    [Fact]
    public void NameOnlyFoldersKeepNamesButNotText()
    {
        Write("apps/tool/readme.txt", "zebra manual");
        Write("notes/plan.txt", "zebra plan");
        var s = new Settings { PauseContentIndexingOnBattery = false, ExcludedContentPaths = { Path.Combine(Root, "apps") } };
        Assert.False(s.WantsContentFor(".txt", Path.Combine(Root, "apps", "tool", "readme.txt")));
        Assert.True(s.WantsContentFor(".txt", Path.Combine(Root, "appsx", "readme.txt")));
        Index(s);
        using var e = new SearchEngine(Db);
        Assert.Equal("plan.txt", Assert.Single(e.Search("zebra")).Name);
        Assert.Single(e.Search("readme"));
    }

    [Fact]
    public void SwitchingTypesToNameOnlyDropsTheirText()
    {
        Write("data.csv", "zebra,1,2");
        Write("app.log", "zebra started");
        Write("notes.txt", "zebra notes");
        Index(Everything());
        using (var e = new SearchEngine(Db)) Assert.Equal(3, e.Search("zebra").Count(r => r.ContentMatch));

        Index(DocumentsOnly()); // the indexer purges on start
        using var e2 = new SearchEngine(Db);
        var hit = Assert.Single(e2.Search("zebra"), r => r.ContentMatch);
        Assert.Equal("notes.txt", hit.Name);
        Assert.Single(e2.Search("data.csv"));
        using var c = new SqliteConnection($"Data Source={Db}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM texts;";
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void RelocateMovesCompactsAndKeepsSearching()
    {
        for (int i = 0; i < 50; i++) Write($"sheet{i}.csv", string.Concat(Enumerable.Repeat($"row {i} value zebra ", 2000)));
        Write("keep.txt", "the zebra stays");
        Index(Everything());
        long before = new FileInfo(Db).Length;

        var target = Path.Combine(_dir, "moved", "index.db");
        IndexStore.Relocate(Db, target, DocumentsOnly());
        Assert.False(File.Exists(Db));
        Assert.True(new FileInfo(target).Length < before / 2);
        using (var e = new SearchEngine(target))
        {
            Assert.Equal("keep.txt", Assert.Single(e.Search("zebra"), r => r.ContentMatch).Name);
            Assert.Equal(50, e.Search("csv", 100).Count(r => r.Name.EndsWith(".csv")));
        }
        using var c = new SqliteConnection($"Data Source={target};Mode=ReadOnly");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA auto_vacuum;";
        Assert.Equal(2L, (long)cmd.ExecuteScalar()!); // incremental: later purges shrink the file
    }

    [Fact]
    public void IndexFolderIsCreatedOnFirstStart()
    {
        // A new PC: the chosen folder (e.g. D:\SpotlightWinData) doesn't exist yet.
        var db = Path.Combine(_dir, "new", "nested", "index.db");
        using (var store = new IndexStore(db, new Settings())) { }
        Assert.True(File.Exists(db));
        using var e = new SearchEngine(db);
        Assert.Empty(e.Search("anything"));
    }

    [Fact]
    public void UpgradeTo050PicksAnIndexFolderAndKeepsChoices()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """{ "SettingsVersion": 3, "Hotkey": "Ctrl+Space", "MaxResults": 60 }""");
        var s = Settings.Load(path);
        Assert.Equal(Settings.DefaultContentCategories(), s.ContentCategories);
        Assert.NotNull(s.IndexFolder);
        Assert.Contains("\"SettingsVersion\": 7", File.ReadAllText(path));
        Assert.Equal(s.IndexFolder, Settings.Load(path).IndexFolder);
    }
}
