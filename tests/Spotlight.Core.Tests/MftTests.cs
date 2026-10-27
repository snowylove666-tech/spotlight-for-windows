using Spotlight.Core;
using Spotlight.Core.Indexing;

/// <summary>
/// 0.8.2: with the SpotlightWin MFT service installed, a crawl reads the drive's master file table.
/// It must index exactly what listing the folders does. Skipped where the service isn't installed.
/// </summary>
public sealed class MftTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spotlight-mft-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_dir, "root");

    public MftTests()
    {
        Directory.CreateDirectory(Path.Combine(Root, "a", "b"));
        Directory.CreateDirectory(Path.Combine(Root, "node_modules", "x"));
        File.WriteAllText(Path.Combine(Root, "a", "b", "deep.md"), "deep");
        File.WriteAllText(Path.Combine(Root, "a", "big.bin"), new string('x', 5000));
        File.WriteAllText(Path.Combine(Root, "node_modules", "x", "skip.js"), "x");
        var hidden = Path.Combine(Root, "hidden.md");
        File.WriteAllText(hidden, "x");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        // A hard link: two names of one file, as the table has them.
        if (OperatingSystem.IsWindows()) CreateHardLink(Path.Combine(Root, "a", "link.md"), Path.Combine(Root, "a", "b", "deep.md"), IntPtr.Zero);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool CreateHardLink(string link, string existing, IntPtr security);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private (List<(string path, long size, long mtime, long fid)> rows, ScanReport report) Crawl(string db, bool mft, bool allNames)
    {
        var s = new Settings { PauseContentIndexingOnBattery = false, IndexContents = false, IndexAllNames = allNames };
        using var store = new IndexStore(db, s);
        using (var indexer = new Indexer(store, s, new[] { Root }) { MftWorthwhile = mft ? 0 : long.MaxValue })
        {
            indexer.RunOnce();
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + db);
            conn.Open();
            using var c = conn.CreateCommand();
            c.CommandText = "SELECT path, size, mtime, COALESCE(fid, 0) FROM files ORDER BY path";
            using var r = c.ExecuteReader();
            var rows = new List<(string, long, long, long)>();
            // A folder's time in its parent's listing is a copy NTFS refreshes lazily: right after
            // something was created inside, it trails the table's (current) time by a moment.
            while (r.Read()) rows.Add((r.GetString(0), r.GetInt64(1), Directory.Exists(r.GetString(0)) ? 0 : r.GetInt64(2), r.GetInt64(3)));
            return (rows, indexer.LastScan!);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheTableGivesTheSameIndexAsListingFolders(bool allNames)
    {
        if (!MftService.Installed) return;
        var (byFolder, _) = Crawl(Path.Combine(_dir, "folders.db"), mft: false, allNames);
        var (byMft, report) = Crawl(Path.Combine(_dir, "mft.db"), mft: true, allNames);
        Assert.Contains("读取主文件表", report.Mft);
        var diff = byFolder.Except(byMft).Select(r => "folders: " + r).Concat(byMft.Except(byFolder).Select(r => "mft: " + r)).ToList();
        Assert.True(diff.Count == 0, string.Join(" | ", diff));
        Assert.Contains(byMft, r => r.path.EndsWith("link.md"));
        Assert.Equal(allNames, byMft.Any(r => r.path.EndsWith("skip.js")));
    }
}
