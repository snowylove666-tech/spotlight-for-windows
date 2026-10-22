using System.IO.Compression;
using Spotlight.Core;
using Spotlight.Core.Indexing;
using Spotlight.Core.Search;

/// <summary>0.7.0: the index ignoring itself, spreadsheets, full content mode, portable apps.</summary>
public sealed class Release070Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spotlight-070-" + Guid.NewGuid().ToString("N"));
    private string Db => Path.Combine(_dir, "data", "index.db");

    public Release070Tests() => Directory.CreateDirectory(_dir);

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

    private void Index(Settings s)
    {
        using var store = new IndexStore(Db, s);
        using var indexer = new Indexer(store, s, new List<string> { _dir });
        indexer.RunOnce();
    }

    private List<SearchResult> Search(string q)
    {
        using var e = new SearchEngine(Db);
        return e.Search(q);
    }

    private static Settings Quiet() => new() { PauseContentIndexingOnBattery = false };

    [Fact]
    public void TheIndexDoesNotIndexItsOwnFiles()
    {
        Write("data/other.txt", "hello");
        var s = Quiet();
        Index(s);
        Index(s); // second run: index.db, -wal now exist on disk inside the root
        Assert.Empty(Search("index.db"));
        Assert.Single(Search("other"));
    }

    [Fact]
    public void SpreadsheetTextIsSearchableByDefault()
    {
        WriteXlsx("docs/预算.xlsx", "课件金融保险预算");
        Write("docs/表.csv", "名称,金额\n差旅报销,300");
        Index(Quiet());
        Assert.Contains(Search("金融保险"), r => r.Name == "预算.xlsx" && r.ContentMatch);
        Assert.Contains(Search("差旅报销"), r => r.Name == "表.csv" && r.ContentMatch);
    }

    [Fact]
    public void FullContentIndexReadsEveryKindAndFolder()
    {
        Write("code/main.py", "def 量化回测(): pass");
        Write("tools/readme.txt", "工具箱说明");
        var s = Quiet();
        s.ExcludedContentPaths = new() { Path.Combine(_dir, "tools") };
        Index(s);
        Assert.DoesNotContain(Search("量化回测"), r => r.ContentMatch);
        Assert.DoesNotContain(Search("工具箱说明"), r => r.ContentMatch);

        s.FullContentIndex = true;
        Index(s);
        Assert.Contains(Search("量化回测"), r => r.ContentMatch);
        Assert.Contains(Search("工具箱说明"), r => r.ContentMatch);

        // Off again: the narrower choices apply and the extra text is dropped.
        s.FullContentIndex = false;
        Index(s);
        Assert.DoesNotContain(Search("量化回测"), r => r.ContentMatch);
    }

    [Fact]
    public void SettingsFrom06AddSpreadsheets()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """{ "SettingsVersion": 4, "ContentCategories": ["Pdf", "Word"], "IndexFolder": "X:\\idx" }""");
        var s = Settings.Load(path);
        Assert.Equal(new[] { "Pdf", "Word", "Spreadsheet" }, s.ContentCategories);
        Assert.False(s.FullContentIndex);
        Assert.Contains("\"SettingsVersion\": 5", File.ReadAllText(path));
    }

    [Theory]
    [InlineData(@"D:\3.AI\codex\投研工作流程\lightodo-v0.4\dist-v0.4\Lightodo.exe", true)]
    [InlineData(@"D:\tools\Everything\Everything.exe", true)]
    [InlineData(@"D:\3.AI\lightodo-v0.4\updates\pending\Lightodo.exe", false)]
    [InlineData(@"D:\tools\Everything\Uninstall.exe", false)]
    [InlineData(@"D:\tools\Everything\CrashReporter.exe", false)]
    [InlineData(@"C:\Users\a\Downloads\WeChatSetup.exe", false)]
    [InlineData(@"D:\games\bin\x64\launcher.exe", false)]
    public void PortableProgramsCountAsApps(string path, bool app)
    {
        if (!OperatingSystem.IsWindows()) return; // backslash paths
        Assert.Equal(app, IndexStore.LooksLikeAppExe(path));
    }

    private void WriteXlsx(string rel, string cellText)
    {
        var p = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        using var zip = ZipFile.Open(p, ZipArchiveMode.Create);
        void Add(string name, string xml)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open());
            w.Write(xml);
        }
        Add("xl/sharedStrings.xml", $"""<?xml version="1.0" encoding="UTF-8"?><sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><si><t>{cellText}</t></si></sst>""");
        Add("xl/worksheets/sheet1.xml", """<?xml version="1.0" encoding="UTF-8"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1"><v>42</v></c></row></sheetData></worksheet>""");
    }
}
