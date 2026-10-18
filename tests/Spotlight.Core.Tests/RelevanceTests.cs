using Spotlight.Core;
using Spotlight.Core.Indexing;
using Spotlight.Core.Search;

/// <summary>Regressions for 0.4.1: documents the user is looking for are read first, long books
/// with many mentions rank well, and a query can be split between name and text.</summary>
public sealed class RelevanceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spotlight-rel-" + Guid.NewGuid().ToString("N"));
    private string Db => Path.Combine(_dir, "index.db");
    private string Root => Path.Combine(_dir, "files");
    private readonly Settings _settings = new() { PauseContentIndexingOnBattery = false };

    public RelevanceTests() => Directory.CreateDirectory(Root);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private string Write(string name, string text)
    {
        var p = Path.Combine(Root, name);
        File.WriteAllText(p, text);
        return p;
    }

    private SearchEngine Index()
    {
        using (var store = new IndexStore(Db, _settings))
        using (var indexer = new Indexer(store, _settings, new[] { Root }))
            indexer.RunOnce();
        return new SearchEngine(Db);
    }

    [Fact]
    public void LongBookWithManyMentionsBeatsManyNotesWithOne()
    {
        for (int i = 0; i < 150; i++) Write($"note{i:D3}.txt", $"第{i}题 货币需求理论 弗里德曼 答案 B");
        var book = string.Concat(Enumerable.Range(0, 400).Select(i =>
            (i % 60 == 7 ? "代表人物：凯恩斯、弗里德曼、施瓦兹等。" : "") + $"第{i}页 经济学基础知识复习，货币供给与需求，利率与汇率。\n"));
        Write("课件金融保险.txt", book);
        using var engine = Index();
        var r = engine.Search("弗里德曼", 60);
        int at = r.FindIndex(x => x.Name == "课件金融保险.txt");
        Assert.InRange(at, 0, 9);
        Assert.Equal(7, r[at].HitCount);
    }

    [Fact]
    public void TermFrequencyIsRecoveredFromBm25()
    {
        // score/idf for tf = 5 in a document 20 times the average length
        double k = 1.2 * (0.25 + 0.75 * 20);
        double r = 5 * 2.2 / (5 + k);
        Assert.Equal(5, SearchEngine.EstimateTermFrequency(r, 20), 3);
        Assert.Equal(1, SearchEngine.EstimateTermFrequency(0, 1));
    }

    [Fact]
    public void QueryCanBeSplitBetweenNameAndText()
    {
        Write("课件金融保险.txt", "代表人物：凯恩斯、弗里德曼。");
        Write("课件经济学.txt", "菲利普斯曲线");
        Write("笔记.txt", "弗里德曼");
        using var engine = Index();
        var r = engine.Search("课件 弗里德曼");
        Assert.Equal("课件金融保险.txt", r[0].Name);
        Assert.True(r[0].ContentMatch);
        Assert.DoesNotContain(r, x => x.Name == "课件经济学.txt");
        Assert.DoesNotContain(r, x => x.Name == "笔记.txt");
    }

    [Fact]
    public void DocumentsAskedForAreReadBeforeTheBacklog()
    {
        for (int i = 0; i < 40; i++) Write($"backlog{i:D2}.txt", "filler text");
        var wanted = Write("wanted.txt", "zebra crossing");
        long wantedId;
        using (var store = new IndexStore(Db, _settings))
        {
            // Names only, as after the first crawl: every text is still waiting.
            long gen = store.NextGeneration();
            store.BeginBatch();
            foreach (var f in new DirectoryInfo(Root).GetFiles()) store.Upsert(f, gen);
            store.CommitBatch();
            wantedId = store.Find(wanted)!.Id;
        }

        using var s = new IndexStore(Db, _settings);
        using var indexer = new Indexer(s, _settings, new[] { Root });
        int pendingWhenRead = -1;
        var done = new ManualResetEventSlim();
        indexer.ReadAhead += ids => { if (ids.Contains(wantedId)) { pendingWhenRead = s.PendingContentCount(); done.Set(); } };
        indexer.ReadSoon(new[] { wantedId });
        indexer.Start();
        Assert.True(done.Wait(TimeSpan.FromSeconds(20)));
        Assert.Equal(40, pendingWhenRead);
        using var engine = new SearchEngine(Db);
        Assert.Contains(engine.Search("zebra"), x => x.Name == "wanted.txt" && x.ContentMatch);
    }

    [Fact]
    public void PendingDocumentsAreFlagged()
    {
        Write("report.txt", "x");
        using (var store = new IndexStore(Db, _settings))
        {
            long gen = store.NextGeneration();
            store.BeginBatch();
            store.Upsert(new FileInfo(Path.Combine(Root, "report.txt")), gen);
            store.CommitBatch();
        }
        using var engine = new SearchEngine(Db);
        Assert.True(Assert.Single(engine.Search("report")).ContentPending);
    }
}
