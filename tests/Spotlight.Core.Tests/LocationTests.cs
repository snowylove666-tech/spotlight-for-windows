using System.Diagnostics;
using Spotlight.Core;
using Spotlight.Core.Indexing;
using Spotlight.Core.Search;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

/// <summary>Regressions for 0.4.0: cloud-sync folders, where a hit is inside a document, long result lists.</summary>
public sealed class LocationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spotlight-loc-" + Guid.NewGuid().ToString("N"));
    private string Db => Path.Combine(_dir, "index.db");
    private string Root => Path.Combine(_dir, "files");

    public LocationTests() => Directory.CreateDirectory(Root);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private SearchEngine Index(Settings? s = null)
    {
        s ??= new Settings();
        using (var store = new IndexStore(Db, s))
        using (var indexer = new Indexer(store, s, new[] { Root }))
            indexer.RunOnce();
        return new SearchEngine(Db);
    }

    [Fact]
    public void PdfHitKnowsItsPage()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in new[] { "Introduction to money", "Supply and demand", "Friedman and the quantity theory" })
            builder.AddPage(595, 842).AddText(text, 12, new PdfPoint(50, 700), font);
        File.WriteAllBytes(Path.Combine(Root, "course.pdf"), builder.Build());
        using var engine = Index();
        var hit = Assert.Single(engine.Search("friedman"));
        Assert.Equal(3, hit.Page);
        Assert.DoesNotContain("\n", hit.Snippet);
    }

    [Fact]
    public void NewlinesSurviveSoEmptyPdfPagesStillCount()
    {
        Assert.Equal("\n\nabc def\n\nxyz", IndexStore.NormalizeWhitespace("\n \n abc \t def \r\n\nxyz  \n"));
    }

    [Fact]
    public void LongDocumentsWithFewMentionsStillShowUp()
    {
        // 120 short documents dense with the term outrank one long book that mentions it once.
        for (int i = 0; i < 120; i++) File.WriteAllText(Path.Combine(Root, $"short{i:D3}.txt"), "货币 弗里德曼 弗里德曼 弗里德曼");
        File.WriteAllText(Path.Combine(Root, "课件金融保险.txt"), string.Concat(Enumerable.Repeat("经济学基础知识复习。", 5000)) + "代表人物：凯恩斯、弗里德曼。");
        using var engine = Index();
        var r = engine.Search("弗里德曼", 200);
        Assert.Contains(r, x => x.Name == "课件金融保险.txt");
        Assert.Equal(60, new Settings().MaxResults);
    }

    [Fact]
    public void JunctionsAreNotFollowedButOrdinaryFoldersAre()
    {
        if (!OperatingSystem.IsWindows()) return;
        var real = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "behindjunction.txt"), "x");
        Directory.CreateDirectory(Path.Combine(Root, "plain"));
        File.WriteAllText(Path.Combine(Root, "plain", "visible.txt"), "x");
        var link = Path.Combine(Root, "link");
        using (var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{real}\"") { CreateNoWindow = true, UseShellExecute = false })!)
            p.WaitForExit();
        try
        {
            Assert.True(Indexer.IsLink(new DirectoryInfo(link)));
            Assert.False(Indexer.IsLink(new DirectoryInfo(Path.Combine(Root, "plain"))));
            using var engine = Index();
            Assert.Single(engine.Search("visible"));
            Assert.Empty(engine.Search("behindjunction"));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public void CloudSyncFoldersAreNotTreatedAsLinks()
    {
        // A cloud sync root (Baidu Netdisk sync disk, OneDrive) is a reparse point without a link target.
        var cloud = new[] { @"D:\BaiduSyncdisk", Environment.GetEnvironmentVariable("OneDrive") ?? "" }
            .FirstOrDefault(p => p.Length > 0 && Directory.Exists(p) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0);
        if (cloud == null) return; // not available on this machine
        Assert.False(Indexer.IsLink(new DirectoryInfo(cloud)));
    }
}
