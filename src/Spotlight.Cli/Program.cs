using System.Diagnostics;
using Spotlight.Core;
using Spotlight.Core.Indexing;
using Spotlight.Core.Search;

// Command-line front end to the same engine the launcher uses; handy for scripting and diagnostics.
//   spotlight-cli index <folder>... [--db file]   crawl folders and read their contents
//   spotlight-cli search <query> [--db file]      print ranked results
Console.OutputEncoding = System.Text.Encoding.UTF8;
var argList = args.ToList();
string? db = null;
int dbAt = argList.IndexOf("--db");
if (dbAt >= 0 && dbAt + 1 < argList.Count) { db = argList[dbAt + 1]; argList.RemoveRange(dbAt, 2); }

if (argList.Count < 2 || argList[0] is not ("index" or "search" or "check" or "mftcheck"))
{
    Console.WriteLine("用法:\n  spotlight-cli index <文件夹>... [--db 索引文件]\n  spotlight-cli search <关键词> [--db 索引文件]\n  spotlight-cli check <文件> [--db 索引文件]");
    return 1;
}

var settings = db == null ? Settings.Load() : Settings.Load(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(db))!, "settings.json"));
db ??= settings.IndexDbPath;
if (argList[0] == "index")
{
    var roots = argList.Skip(1).Select(Path.GetFullPath).ToList();
    using var store = new IndexStore(db, settings);
    using var indexer = new Indexer(store, settings, roots);
    var sw = Stopwatch.StartNew();
    indexer.StatusChanged += s => Console.Error.Write($"\r{s.Describe()}        ");
    indexer.RunOnce();
    Console.Error.WriteLine();
    Console.WriteLine($"{indexer.Status.Describe()}，用时 {sw.Elapsed.TotalSeconds:F1} 秒");
    if (indexer.LastScan is { } scan)
        Console.WriteLine($"新增 {scan.Added:N0}，删除 {scan.Removed:N0}，未变 {scan.Unchanged:N0}；{scan.Timings}；{scan.Mft ?? "逐个文件夹扫描"}");
    return 0;
}

if (argList[0] == "mftcheck")
{
    // Reads the drive's master file table through the MFT service and compares it with the folders on disk.
    var folder = Path.GetFullPath(argList[1]);
    var sw = Stopwatch.StartNew();
    var volume = MftVolume.Load(Path.GetPathRoot(folder)!, out var why);
    if (volume == null) { Console.WriteLine("无法读取主文件表：" + why); return 2; }
    Console.WriteLine($"主文件表 {volume.Count:N0} 项，读取用时 {sw.Elapsed.TotalSeconds:F2} 秒");
    sw.Restart();
    Console.Write(volume.CompareWithDisk(folder));
    Console.WriteLine($"比较用时 {sw.Elapsed.TotalSeconds:F1} 秒");
    return 0;
}

if (argList[0] == "check")
{
    using var store = new IndexStore(db, settings);
    using var indexer = new Indexer(store, settings);
    Console.WriteLine(indexer.Diagnose(argList[1]));
    return 0;
}

using var engine = new SearchEngine(db);
var query = string.Join(' ', argList.Skip(1));
engine.Search(query, settings.MaxResults); // warm-up: the launcher keeps the engine hot, so time the second run
var timer = Stopwatch.StartNew();
var results = engine.Search(query, settings.MaxResults);
timer.Stop();
foreach (var r in results)
{
    Console.WriteLine(r);
    if (r.Snippet != null) Console.WriteLine($"         └ {r.Snippet}");
}
Console.WriteLine($"{results.Count} 条结果，{timer.Elapsed.TotalMilliseconds:F1} ms");
return 0;
