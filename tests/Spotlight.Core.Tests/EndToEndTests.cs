using System.IO.Compression;
using System.Text;
using Spotlight.Core;
using Spotlight.Core.Indexing;
using Spotlight.Core.Search;
using UglyToad.PdfPig.Writer;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Core;

public sealed class EndToEndTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spotlight-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string _db;
    private readonly Settings _settings = new();

    public EndToEndTests()
    {
        _root = Path.Combine(_dir, "files");
        _db = Path.Combine(_dir, "index.db");
        Directory.CreateDirectory(_root);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string Write(string rel, string text, Encoding? enc = null)
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text, enc ?? new UTF8Encoding(false));
        return p;
    }

    private void WritePdf(string rel, string text)
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(595, 842);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        page.AddText(text, 12, new PdfPoint(50, 700), font);
        File.WriteAllBytes(Path.Combine(_root, rel), builder.Build());
    }

    private void WriteDocx(string rel, params string[] paragraphs)
    {
        var p = Path.Combine(_root, rel);
        using var zip = ZipFile.Open(p, ZipArchiveMode.Create);
        var e = zip.CreateEntry("word/document.xml");
        using var w = new StreamWriter(e.Open());
        w.Write("<?xml version=\"1.0\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>");
        foreach (var para in paragraphs) w.Write($"<w:p><w:r><w:t>{para}</w:t></w:r></w:p>");
        w.Write("</w:body></w:document>");
    }

    private SearchEngine Index()
    {
        using (var store = new IndexStore(_db, _settings))
        using (var indexer = new Indexer(store, _settings, new[] { _root }))
            indexer.RunOnce();
        return new SearchEngine(_db);
    }

    [Fact]
    public void FindsFilesByNameWithExactMatchFirst()
    {
        Write("report.txt", "x");
        Write("annual_report_2024.txt", "x");
        Write("deep/er/reporting tools.md", "x");
        Write("unrelated.txt", "x");
        using var engine = Index();
        var r = engine.Search("report");
        Assert.Equal("report.txt", r[0].Name);
        Assert.Contains(r, x => x.Name == "annual_report_2024.txt");
        Assert.Contains(r, x => x.Name == "reporting tools.md");
        Assert.DoesNotContain(r, x => x.Name == "unrelated.txt");
    }

    [Fact]
    public void FindsSubstringInsideWord()
    {
        Write("myreport.txt", "x");
        using var engine = Index();
        Assert.Contains(engine.Search("port"), x => x.Name == "myreport.txt");
    }

    [Fact]
    public void FindsChineseNamesAndContent()
    {
        Write("2024年度采购合同.txt", "甲方与乙方签订本合同。");
        Write("笔记/会议纪要.txt", "下周讨论 付款条款 和交付时间。");
        Write("gbk.txt", "这是用国标编码保存的报销说明", Encoding.GetEncoding("GB18030"));
        using var engine = Index();

        Assert.Equal("2024年度采购合同.txt", engine.Search("采购合同")[0].Name);
        Assert.Contains(engine.Search("合"), x => x.Name == "2024年度采购合同.txt");

        var content = engine.Search("付款条款");
        var hit = Assert.Single(content);
        Assert.Equal("会议纪要.txt", hit.Name);
        Assert.True(hit.ContentMatch);
        Assert.Contains("付款条款", hit.Snippet);

        Assert.Contains(engine.Search("报销"), x => x.Name == "gbk.txt");
    }

    [Fact]
    public void FindsTextInsidePdfAndDocx()
    {
        WritePdf("paper.pdf", "Quantum entanglement experiment results");
        WriteDocx("plan.docx", "Kickoff meeting agenda", "预算审批流程");
        using var engine = Index();

        var pdf = engine.Search("entanglement");
        Assert.Equal("paper.pdf", Assert.Single(pdf).Name);
        Assert.Contains("entanglement", pdf[0].Snippet, StringComparison.OrdinalIgnoreCase);

        Assert.Equal("plan.docx", Assert.Single(engine.Search("agenda")).Name);
        Assert.Equal("plan.docx", Assert.Single(engine.Search("审批")).Name);
    }

    [Fact]
    public void NameMatchOutranksContentMatch()
    {
        Write("budget.txt", "nothing here");
        Write("notes.txt", "the budget for next year");
        using var engine = Index();
        var r = engine.Search("budget");
        Assert.Equal("budget.txt", r[0].Name);
        Assert.Equal("notes.txt", r[1].Name);
    }

    [Fact]
    public void LearnsFromPicks()
    {
        Write("project-alpha.txt", "x");
        Write("project-beta.txt", "x");
        using (var store = new IndexStore(_db, _settings))
        using (var indexer = new Indexer(store, _settings, new[] { _root }))
        {
            indexer.RunOnce();
            using var e = new SearchEngine(_db);
            var beta = e.Search("project").Single(x => x.Name == "project-beta.txt");
            store.RecordLaunch(beta.Id, "proj");
            store.RecordLaunch(beta.Id, "proj");
        }
        using var engine = new SearchEngine(_db);
        Assert.Equal("project-beta.txt", engine.Search("pro")[0].Name);
    }

    [Fact]
    public void RescanRemovesDeletedAndPicksUpChanges()
    {
        var gone = Write("temporary.txt", "x");
        var changed = Write("changing.txt", "old words");
        Index().Dispose();
        File.Delete(gone);
        File.WriteAllText(changed, "fresh vocabulary");
        File.SetLastWriteTimeUtc(changed, DateTime.UtcNow.AddMinutes(1));
        using var engine = Index();
        Assert.Empty(engine.Search("temporary"));
        Assert.Single(engine.Search("vocabulary"));
        Assert.DoesNotContain(engine.Search("old words"), x => x.ContentMatch);
    }

    [Fact]
    public void ExcludedFoldersAreSkipped()
    {
        Write("node_modules/pkg/index.js", "x");
        Write("src/index.js", "x");
        using var engine = Index();
        var r = engine.Search("index");
        Assert.Single(r, x => x.Name == "index.js");
    }

    [Fact]
    public void WatcherPicksUpNewFiles()
    {
        Write("existing.txt", "x");
        // "zebra" is only in the file's text; on a laptop running on battery content reading would pause.
        _settings.PauseContentIndexingOnBattery = false;
        using var store = new IndexStore(_db, _settings);
        using var indexer = new Indexer(store, _settings, new[] { _root }) { Debounce = TimeSpan.FromMilliseconds(100) };
        indexer.Start();
        using var engine = new SearchEngine(_db);
        WaitFor(() => engine.Search("existing").Count == 1);
        Write("brandnew.txt", "zebra crossing");
        WaitFor(() => engine.Search("zebra").Count == 1);
        File.Delete(Path.Combine(_root, "brandnew.txt"));
        WaitFor(() => engine.Search("brandnew").Count == 0);
    }

    private static void WaitFor(Func<bool> cond)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < until) { if (cond()) return; Thread.Sleep(100); }
        Assert.True(cond(), "condition not met within timeout");
    }
}
