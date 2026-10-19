using Spotlight.Core;
using Spotlight.Core.Indexing;
using Spotlight.Core.Search;

/// <summary>0.6.0: filtering results by kind and ordering them by date, name, size or format.</summary>
public sealed class FilterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spotlight-filter-" + Guid.NewGuid().ToString("N"));
    private string Db => Path.Combine(_dir, "index.db");
    private string Root => Path.Combine(_dir, "files");

    public FilterTests()
    {
        Directory.CreateDirectory(Root);
        Write("budget-old.txt", "x", 10, daysAgo: 30);
        Write("budget-new.md", "xxxxxxxxxx", 1000, daysAgo: 1);
        Write("budget-mid.csv", "x", 100, daysAgo: 10);
        Write("budget.png", "x", 50, daysAgo: 5);
        Directory.CreateDirectory(Path.Combine(Root, "budget"));
        var s = new Settings { PauseContentIndexingOnBattery = false };
        using var store = new IndexStore(Db, s);
        using var indexer = new Indexer(store, s, new[] { Root });
        indexer.RunOnce();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private void Write(string name, string text, int size, int daysAgo)
    {
        var p = Path.Combine(Root, name);
        File.WriteAllText(p, new string('x', size));
        File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddDays(-daysAgo));
    }

    [Fact]
    public void KindFilterKeepsOnlyThatKind()
    {
        using var e = new SearchEngine(Db);
        Assert.Equal(5, e.Search("budget").Count);
        var text = e.Search("budget", new SearchFilter(new[] { FileKind.Text }));
        Assert.Equal(new[] { "budget-new.md", "budget-old.txt" }, text.Select(r => r.Name).OrderBy(n => n));
        Assert.Equal("budget", Assert.Single(e.Search("budget", new SearchFilter(new[] { FileKind.Folder }))).Name);
        Assert.Empty(e.Search("budget", new SearchFilter(new[] { FileKind.Pdf })));
        Assert.Empty(e.Search("1+1", new SearchFilter(new[] { FileKind.Pdf }))); // no calculator under a filter
    }

    [Fact]
    public void ResultsCanBeOrderedByDateNameSizeAndFormat()
    {
        using var e = new SearchEngine(Db);
        var files = new SearchFilter(new[] { FileKind.Text, FileKind.Spreadsheet, FileKind.Image });
        string[] Names(SortBy s, bool desc) => e.Search("budget", files with { Sort = s, Descending = desc }).Select(r => r.Name).ToArray();

        Assert.Equal(new[] { "budget-new.md", "budget.png", "budget-mid.csv", "budget-old.txt" }, Names(SortBy.Modified, true));
        Assert.Equal(new[] { "budget-old.txt", "budget-mid.csv", "budget.png", "budget-new.md" }, Names(SortBy.Modified, false));
        Assert.Equal(new[] { "budget-new.md", "budget-mid.csv", "budget.png", "budget-old.txt" }, Names(SortBy.Size, true));
        Assert.Equal(Names(SortBy.Name, false).Reverse(), Names(SortBy.Name, true));
        Assert.Equal(4, Names(SortBy.Name, false).Distinct().Count());
        // Format: grouped by kind name, then extension.
        var byKind = e.Search("budget", files with { Sort = SortBy.Kind, Descending = false }).Select(r => r.Kind).ToList();
        Assert.Equal(byKind.Distinct().Count(), byKind.Select((k, i) => (k, i)).Count(x => x.i == 0 || byKind[x.i - 1] != x.k));
    }
}
