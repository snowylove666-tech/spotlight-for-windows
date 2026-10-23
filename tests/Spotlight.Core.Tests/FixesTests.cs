using System.Text;
using Spotlight.Core;
using Spotlight.Core.Extraction;
using Spotlight.Core.Indexing;
using Spotlight.Core.Search;

/// <summary>Regressions for 0.2.0: filters, location changes, PDF text budget, drive roots.</summary>
public sealed class FixesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spotlight-fix-" + Guid.NewGuid().ToString("N"));
    private string Db => Path.Combine(_dir, "index.db");

    public FixesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string Write(string rel, string text = "x")
    {
        var p = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
        return p;
    }

    private void Index(Settings s, params string[] roots)
    {
        using var store = new IndexStore(Db, s);
        using var indexer = new Indexer(store, s, roots.Select(r => Path.Combine(_dir, r)).ToList());
        indexer.RunOnce();
    }

    private List<SearchResult> Search(string q)
    {
        using var e = new SearchEngine(Db);
        return e.Search(q);
    }

    [Fact]
    public void ChineseGlyphWordsAreNotSpaced()
    {
        var sb = new StringBuilder();
        foreach (var w in new[] { "弗", "里", "德", "曼", "提出", "IS-LM", "模型", "Friedman" }) PdfExtractor.AppendWord(sb, w);
        Assert.Equal("弗里德曼提出 IS-LM 模型 Friedman", sb.ToString());
    }

    [Fact]
    public void ExcludedExtensionsAndKindsAreNotIndexedAndAreRemovedLater()
    {
        Write("a/notes.txt", "弗里德曼");
        Write("a/paper.md", "弗里德曼");
        Write("a/photo.png");
        var s = new Settings();
        Index(s, "a");
        Assert.Equal(2, Search("弗里德曼").Count);

        s.ExcludedExtensions = new() { "txt" };
        s.ExcludedKinds = new() { "Image" };
        Index(s, "a");
        var r = Search("弗里德曼");
        Assert.Equal("paper.md", Assert.Single(r).Name);
        Assert.Empty(Search("photo"));
    }

    [Fact]
    public void RemovedLocationsAreDroppedFromTheIndex()
    {
        Write("keep/alpha.txt");
        Write("drop/beta.txt");
        var s = new Settings();
        Index(s, "keep", "drop");
        Assert.Single(Search("beta"));
        Index(s, "keep");
        Assert.Empty(Search("beta"));
        Assert.Single(Search("alpha"));
    }

    [Fact]
    public void DiagnoseExplainsMissingFiles()
    {
        var inside = Write("root/doc.md", "hello");
        var outside = Write("elsewhere/other.md", "hello");
        var s = new Settings();
        using var store = new IndexStore(Db, s);
        using var indexer = new Indexer(store, s, new[] { Path.Combine(_dir, "root") });
        indexer.RunOnce();
        Assert.Contains("不在索引范围内", indexer.Diagnose(outside));
        Assert.Contains("正文已读取 5 个字符", indexer.Diagnose(inside));
    }

    [Fact]
    public void DriveRootPrefixRangeCoversEverythingOnTheDrive()
    {
        var sep = Path.DirectorySeparatorChar;
        var root = OperatingSystem.IsWindows() ? @"D:\" : "/";
        var (lo, hi) = IndexStore.PrefixRange(root);
        var child = root + "dir" + sep + "file.pdf";
        Assert.True(string.CompareOrdinal(child, lo) >= 0 && string.CompareOrdinal(child, hi) < 0);
        var (lo2, hi2) = IndexStore.PrefixRange(root + "a");
        Assert.Equal(root + "a" + sep, lo2);
        Assert.True(string.CompareOrdinal(root + "ab", lo2) < 0 || string.CompareOrdinal(root + "ab", hi2) >= 0);
    }

    [Fact]
    public void OldSettingsAreUpgraded()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """{ "Hotkey": "Ctrl+Space", "MaxPdfFileSizeMB": 50, "MaxContentChars": 1000000, "ExcludedDirNames": ["node_modules"] }""");
        var s = Settings.Load(path);
        Assert.Equal("Ctrl+Space", s.Hotkey);
        Assert.Equal(300, s.MaxPdfFileSizeMB);
        Assert.Equal(5_000_000, s.MaxContentChars);
        Assert.Contains("Program Files", s.ExcludedDirNames);
        Assert.Equal(60, s.MaxResults);
        Assert.Contains("\"SettingsVersion\": 7", File.ReadAllText(path));
    }
}
