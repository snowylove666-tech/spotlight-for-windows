using System.Collections.Concurrent;
using System.Diagnostics;
using Spotlight.Core.Extraction;

namespace Spotlight.Core.Indexing;

public enum IndexPhase { Starting, Scanning, ReadingContents, Idle, PausedOnBattery }

/// <summary>What one full scan found: shown after "重新扫描" so the user can see it ran and what changed.</summary>
public sealed record ScanReport(DateTime Started, DateTime Finished, long FilesBefore, long FilesAfter,
    long Added, long Removed, long SizeBefore, long SizeAfter, IReadOnlyList<string> MissingRoots, int TextDropped)
{
    public TimeSpan Took => Finished - Started;
}

public sealed record IndexStatus(IndexPhase Phase, long Files, long WithContent, int PendingContent, string? Detail = null,
    bool Fast = false, TimeSpan? Remaining = null)
{
    private string Eta => Remaining is not { } r ? "" :
        r.TotalMinutes < 1 ? "，不到 1 分钟" :
        r.TotalHours < 1 ? $"，约 {Math.Ceiling(r.TotalMinutes)} 分钟" :
        $"，约 {r.TotalHours:F1} 小时";

    public string Describe() => Phase switch
    {
        IndexPhase.Starting => "正在启动索引…",
        IndexPhase.Scanning => $"正在扫描文件… 已收录 {Files:N0} 项",
        IndexPhase.ReadingContents => $"正在读取文档内容{(Fast ? "（插电加速中）" : "")}… 剩余 {PendingContent:N0} 个{Eta}；文件名已可搜索",
        IndexPhase.PausedOnBattery => $"使用电池中，已暂停读取内容（剩余 {PendingContent:N0} 个）",
        _ => $"已索引 {Files:N0} 项，其中 {WithContent:N0} 个文档含全文",
    };
}

/// <summary>
/// Keeps the index in sync with the disk on one low-priority background thread:
///  1. NTFS drives: the change journal (USN), read once a second from where the last read stopped,
///     also across restarts and sleep, like Everything. A drive is crawled only the first time,
///     when the search locations change, or when the journal no longer reaches back far enough.
///  2. Other drives (FAT/exFAT USB sticks, network shares): a crawl at start-up and
///     FileSystemWatcher events afterwards.
///  3. Content extraction for new or changed documents: throttled while the user is at the
///     computer, paused while on battery.
/// </summary>
public sealed class Indexer : IDisposable
{
    private readonly IndexStore _store;
    private readonly Settings _settings;
    private readonly ConcurrentDictionary<string, byte> _changes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _rescanRoots = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly List<FileSystemWatcher> _watchers = new();
    // Volume root ("C:\") → its change journal, for drives that have one.
    private readonly Dictionary<string, UsnJournal> _journals = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Drives followed through their NTFS change journal ("C:\", "D:\").</summary>
    public IReadOnlyList<string> JournalVolumes => _journals.Keys.ToList();
    private readonly HashSet<string> _excludedNames;
    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;
    private volatile bool _fullRescanRequested;

    public event Action<IndexStatus>? StatusChanged;

    /// <summary>Raised on the indexer thread after each full scan.</summary>
    public event Action<ScanReport>? ScanFinished;
    public ScanReport? LastScan { get; private set; }
    private int _textDropped;

    /// <summary>Raised on the indexer thread whenever it has caught up and is about to sleep.</summary>
    public event Action? Idle;
    public IndexStatus Status { get; private set; } = new(IndexPhase.Starting, 0, 0, 0);

    /// <summary>Delay between a file change and indexing it; editors often write a file several times.</summary>
    public TimeSpan Debounce { get; init; } = TimeSpan.FromSeconds(2);

    public IReadOnlyList<string> Roots { get; }

    public Indexer(IndexStore store, Settings settings, IReadOnlyList<string>? roots = null)
    {
        _store = store;
        _settings = settings;
        _excludedNames = new HashSet<string>(settings.ExcludedDirNames, StringComparer.OrdinalIgnoreCase);
        _ownFiles = Path.GetFullPath(store.DbPath);
        Roots = roots ?? Settings.AppRoots().Concat(settings.EffectiveRoots()).ToList();
    }

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Spotlight indexer" };
        _thread.Start();
    }

    public void RequestFullRescan()
    {
        _fullRescanRequested = true;
        _wake.Set();
    }

    private readonly ConcurrentQueue<long> _urgent = new();

    /// <summary>Reads these documents ahead of the backlog (even on battery): the user just searched
    /// for them by name, so their text is wanted now, not after thousands of others.</summary>
    public void ReadSoon(IEnumerable<long> ids)
    {
        bool any = false;
        foreach (var id in ids) { _urgent.Enqueue(id); any = true; }
        if (any) _wake.Set();
    }

    /// <summary>Raised on the indexer thread after documents passed to <see cref="ReadSoon"/> were read.</summary>
    public event Action<IReadOnlyList<long>>? ReadAhead;

    private void ReadUrgent(CancellationToken ct)
    {
        var ids = new HashSet<long>();
        while (ids.Count < 32 && _urgent.TryDequeue(out var id)) ids.Add(id);
        var batch = _store.PendingContentByIds(ids);
        if (batch.Count == 0) return;
        var results = new (long, long, string?, string?)[batch.Count];
        Parallel.For(0, batch.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4), CancellationToken = ct }, i =>
        {
            var text = ExtractSafely(batch[i], ct, out var note);
            results[i] = (batch[i].Id, batch[i].MTime, text, note);
        });
        ct.ThrowIfCancellationRequested();
        _store.SetContents(results);
        ReadAhead?.Invoke(batch.Select(b => b.Id).ToList());
    }

    // ---------------------------------------------------------------- main loop

    private void Run()
    {
        PowerAware.MakeCurrentThreadBackground();
        var ct = _cts.Token;
        try
        {
            OpenJournals();
            // Watch first: files created or deleted while the (possibly long) first crawl runs are
            // applied during it (see ApplyFileChanges), instead of waiting for the next full scan.
            StartWatchers();
            Guarded(() => { DropUnwantedText(); CatchUp(ct); });
            bool busy = true;
            while (!ct.IsCancellationRequested)
            {
                bool moreContent = true;
                Guarded(() =>
                {
                    if (_fullRescanRequested) { _fullRescanRequested = false; FullScan(ct); busy = true; }
                    busy |= ReadJournals(ct);
                    busy |= !_changes.IsEmpty || !_rescanRoots.IsEmpty;
                    ApplyChanges(ct);
                    while (_rescanRoots.TryDequeue(out var root)) Rescan(root, ct);
                    ApplyChanges(ct);
                    moreContent = ReadContents(ct, out bool readAny);
                    busy |= readAny;
                    // Only after real work: the journal is polled every second, and trimming memory
                    // or redrawing the status that often would cost more than the polling.
                    if (busy)
                    {
                        Publish(IndexPhase.Idle);
                        _store.ReleaseMemory();
                        Idle?.Invoke();
                        busy = false;
                    }
                });
                // Journals: look again in a second (a read with nothing new costs microseconds).
                // Otherwise sleep until a watcher fires; with content left to read (paused on battery,
                // or throttled) re-check once a minute.
                var wait = _journals.Count > 0 ? TimeSpan.FromSeconds(1) : moreContent ? TimeSpan.FromSeconds(60) : Timeout.InfiniteTimeSpan;
                // Resting between documents while the user is active: come back when the rest is over.
                var rest = _restUntil - DateTime.UtcNow;
                if (moreContent && rest > TimeSpan.Zero && (wait == Timeout.InfiniteTimeSpan || rest < wait)) wait = rest;
                _wake.WaitOne(wait);
                if (!_changes.IsEmpty) Thread.Sleep(Debounce);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Runs one step of the loop. An unexpected error (a locked database, a driver returning
    /// garbage) is logged and the loop goes on: before, it ended the indexer thread for good and the
    /// index silently stopped following the disk.</summary>
    private void Guarded(Action step)
    {
        try { step(); }
        catch (OperationCanceledException) { throw; }
        catch (ObjectDisposedException) { throw; }
        catch (Exception e)
        {
            ErrorLog.Write("indexer", e);
            Thread.Sleep(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>Runs one full crawl and the content pass synchronously. Used by the CLI and tests.</summary>
    public void RunOnce(CancellationToken ct = default)
    {
        DropUnwantedText();
        FullScan(ct);
        while (ReadContents(ct, out _, ignoreBattery: true, unthrottled: true) && _store.PendingContentCount() > 0) { }
        Publish(IndexPhase.Idle);
    }

    // ---------------------------------------------------------------- change journals

    private void OpenJournals()
    {
        foreach (var volume in Roots.Select(r => Path.GetPathRoot(r)).Where(v => !string.IsNullOrEmpty(v)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (UsnJournal.Open(volume!) is { } j) _journals[j.Volume] = j;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
        }
    }

    private UsnJournal? JournalOf(string path) =>
        Path.GetPathRoot(path) is { Length: > 0 } v && _journals.TryGetValue(v, out var j) ? j : null;

    /// <summary>What decides which files are indexed: when it changes, the drives are crawled again.</summary>
    private string ScopeSignature() => string.Join("|",
        string.Join(";", Roots), string.Join(";", _settings.ExcludedDirNames), string.Join(";", _settings.ExcludedPaths),
        string.Join(";", _settings.ExcludedExtensions), string.Join(";", _settings.ExcludedKinds), _settings.IndexHiddenFiles,
        "fid1"); // bump to crawl again after a change in what a crawl records

    private (ulong journal, long usn)? Cursor(UsnJournal j) =>
        _store.GetMeta("usn:" + j.Volume) is { } v && v.Split(':') is [var id, var usn]
        && ulong.TryParse(id, System.Globalization.NumberStyles.HexNumber, null, out var jid) && long.TryParse(usn, out var u)
            ? (jid, u) : null;

    private void SaveCursor(UsnJournal j, long usn) => _store.SetMeta("usn:" + j.Volume, $"{j.JournalId:x}:{usn}");

    /// <summary>
    /// Start-up: drives whose journal still reaches back to where the last run stopped only replay
    /// the changes since (seconds, even after days off); the others are crawled. Everything is
    /// crawled when the search locations or filters changed.
    /// </summary>
    private void CatchUp(CancellationToken ct)
    {
        string scope = ScopeSignature();
        bool sameScope = _store.GetMeta("scope") == scope;
        var current = new List<UsnJournal>();
        if (sameScope)
            foreach (var j in _journals.Values)
                if (j.Query() && Cursor(j) is { } c && c.journal == j.JournalId && c.usn >= j.FirstUsn && c.usn <= j.NextUsn)
                    current.Add(j);
        var stale = Roots.Where(r => JournalOf(r) is not { } j || !current.Contains(j)).ToList();
        if (!sameScope || stale.Count == Roots.Count)
        {
            FullScan(ct);
            return;
        }
        if (stale.Count > 0)
        {
            var starts = _journals.Values.Where(j => !current.Contains(j) && j.Query()).ToDictionary(j => j, j => j.NextUsn);
            long gen = _store.NextGeneration();
            foreach (var root in stale) ScanRoot(root, gen, ct);
            foreach (var (j, usn) in starts) SaveCursor(j, usn);
        }
        ReadJournals(ct);
    }

    private DateTime _journalsReadAt;

    /// <summary>A second has passed since the journals were last read: content reading steps aside.</summary>
    private bool JournalsDue => _journals.Count > 0 && DateTime.UtcNow - _journalsReadAt > TimeSpan.FromSeconds(1);

    /// <summary>Applies what the journals recorded since the last read. True if anything changed.</summary>
    private bool ReadJournals(CancellationToken ct)
    {
        _journalsReadAt = DateTime.UtcNow;
        bool any = false;
        foreach (var j in _journals.Values)
        {
            if (Cursor(j) is not { } c) continue;
            var changes = new List<UsnChange>();
            var result = c.journal == j.JournalId ? j.Read(c.usn, changes) : UsnJournal.ReadResult.Reset;
            if (result == UsnJournal.ReadResult.Reset)
            {
                // The journal was recreated or dropped what we hadn't read: crawl this drive again.
                if (!j.Query()) continue;
                long start = j.NextUsn, gen = _store.NextGeneration();
                foreach (var root in Roots.Where(r => JournalOf(r) == j)) ScanRoot(root, gen, ct);
                SaveCursor(j, start);
                any = true;
                continue;
            }
            if (result != UsnJournal.ReadResult.Ok) continue;
            if (changes.Count > 0 && ApplyJournal(j, changes, ct)) any = true;
            if (j.NextUsn != c.usn) SaveCursor(j, j.NextUsn);
        }
        return any;
    }

    private const UsnReason Relevant = UsnReason.FileCreate | UsnReason.FileDelete | UsnReason.RenameOldName | UsnReason.RenameNewName
        | UsnReason.DataOverwrite | UsnReason.DataExtend | UsnReason.DataTruncation | UsnReason.BasicInfoChange
        | UsnReason.HardLinkChange | UsnReason.ReparsePointChange;

    /// <summary>
    /// One batch of journal records. Each file is handled once, as it is now: opened by id for its
    /// current path and details (or found gone). Records for places that aren't indexed (AppData,
    /// a browser cache: most of what happens on C:) are skipped after two index lookups, without
    /// touching the file. Returns true if the index changed.
    /// </summary>
    private bool ApplyJournal(UsnJournal j, List<UsnChange> changes, CancellationToken ct)
    {
        var order = new List<long>();
        var last = new Dictionary<long, UsnChange>();
        var why = new Dictionary<long, UsnReason>();
        foreach (var c in changes)
        {
            if (!last.ContainsKey(c.Fid)) order.Add(c.Fid);
            last[c.Fid] = c;
            why[c.Fid] = why.GetValueOrDefault(c.Fid) | c.Reason;
        }
        bool changed = false;
        int urgent = 0, inBatch = 0;
        long gen = _store.NextGeneration();
        void Flush() { if (inBatch > 0) { _store.CommitBatch(); inBatch = 0; } }
        try
        {
            foreach (var fid in order)
            {
                ct.ThrowIfCancellationRequested();
                if ((why[fid] & Relevant) == 0) continue;
                var rec = last[fid];
                if (inBatch == 0) { _store.BeginBatch(); inBatch = 1; }
                var known = _store.FindByFid(j.Volume, fid);
                bool inIndexedFolder = _store.FindByFid(j.Volume, rec.ParentFid) is { isDir: true };
                if (known == null && !inIndexedFolder) continue;
                FsEntry? now = (rec.Reason & UsnReason.FileDelete) != 0 ? null : j.Resolve(fid);
                if (now is not { } e)
                {
                    if (known is { } gone) { _store.DeletePath(gone.path); changed = true; }
                    continue;
                }
                bool moved = known is { } k && !k.path.Equals(e.Path, StringComparison.OrdinalIgnoreCase);
                if (moved) { _store.DeletePath(known!.Value.path); changed = true; }
                if (!InScope(e)) continue;
                if (e.IsDir && (known == null || moved))
                {
                    // A new folder, or one moved here: index what is inside it too.
                    Flush();
                    ScanRoot(e.Path, gen, ct, sweep: false);
                    changed = true;
                    continue;
                }
                if (inBatch == 0) { _store.BeginBatch(); inBatch = 1; }
                _store.Upsert(e, gen);
                changed = true;
                if (++inBatch > 500) Flush();
                // A file just saved or downloaded is likely what the user searches for next.
                if (!e.IsDir && urgent < 200 && _store.Find(e.Path) is { WantsContent: true, ContentRead: false } r)
                {
                    _urgent.Enqueue(r.Id);
                    urgent++;
                }
            }
        }
        finally { Flush(); }
        return changed;
    }

    /// <summary>Whether a file reported by the journal belongs in the index, by the crawl's rules:
    /// under a search location, not excluded, not hidden/system, and in a folder that is indexed.</summary>
    private bool InScope(FsEntry e)
    {
        if (IsExcluded(e.Path)) return false;
        if ((e.Attributes & AttributesToSkip) != 0) return false;
        if (e.IsDir ? _excludedNames.Contains(e.Name) : _settings.IsExcludedFile(e.Extension)) return false;
        if (Roots.Any(r => r.Equals(e.Path, StringComparison.OrdinalIgnoreCase))) return true;
        var parent = Path.GetDirectoryName(e.Path);
        return parent != null && _store.Contains(parent);
    }

    private FileAttributes AttributesToSkip => _settings.IndexHiddenFiles ? FileAttributes.System : FileAttributes.System | FileAttributes.Hidden;

    /// <summary>File types or folders switched to name-only: drop their text and give the space back.</summary>
    private void DropUnwantedText()
    {
        // Older versions indexed the index itself (see _ownFiles).
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal", ".moving" }) _store.DeletePath(_ownFiles + suffix);
        _store.ReclassifyExecutables();
        int dropped = _textDropped = _store.PurgeIneligibleContent();
        if (dropped == 0) return;
        if (dropped > 500) _store.Optimize();
        _store.ReleaseSpace();
    }

    private void FullScan(CancellationToken ct)
    {
        var started = DateTime.Now;
        var (before, _) = _store.Counts(exact: true);
        long sizeBefore = _store.SizeBytes(), insertedBefore = _store.Inserted, deletedBefore = _store.Deleted;
        long gen = _store.NextGeneration();
        var starts = _journals.Values.Where(j => j.Query()).ToDictionary(j => j, j => j.NextUsn);
        foreach (var root in Roots) ScanRoot(root, gen, ct);
        // Anything not seen by this crawl is gone, outside the configured locations, or now filtered out.
        _store.DeleteOlderThan(gen);
        foreach (var (j, usn) in starts) SaveCursor(j, usn);
        _store.SetMeta("scope", ScopeSignature());
        var (after, _) = _store.Counts(exact: true);
        // Each root sweeps its own stale rows as it finishes; count those too.
        LastScan = new ScanReport(started, DateTime.Now, before, after, _store.Inserted - insertedBefore, _store.Deleted - deletedBefore,
            sizeBefore, _store.SizeBytes(), Roots.Where(r => !Directory.Exists(r)).ToList(), _textDropped);
        _textDropped = 0;
        ScanFinished?.Invoke(LastScan);
    }

    /// <summary>Crawls one root again (its watcher lost events). Roots nested in it (e.g. the Start
    /// Menu under C:\) are crawled in the same generation, so its stale-row sweep keeps them.</summary>
    private void Rescan(string root, CancellationToken ct)
    {
        long gen = _store.NextGeneration();
        foreach (var nested in Roots.Where(r => !r.Equals(root, StringComparison.OrdinalIgnoreCase)
                     && r.StartsWith(Settings.WithSeparator(root), StringComparison.OrdinalIgnoreCase)))
            ScanRoot(nested, gen, ct);
        ScanRoot(root, gen, ct);
    }

    // ---------------------------------------------------------------- crawling

    private void ScanRoot(string root, long gen, CancellationToken ct, bool sweep = true)
    {
        if (!Directory.Exists(root)) return;
        Publish(IndexPhase.Scanning, root);
        var stack = new Stack<string>();
        stack.Push(root);
        var skip = AttributesToSkip;
        int inBatch = 0;
        var sw = Stopwatch.StartNew();
        _store.BeginBatch();
        try
        {
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var dir = stack.Pop();
                foreach (var (e, isLink) in DirLister.List(dir))
                {
                    if ((e.Attributes & skip) != 0) continue;
                    if (e.IsDir)
                    {
                        if (_excludedNames.Contains(e.Name) || IsExcludedPath(e.Path)) continue;
                        // Don't follow junctions/symlinks: they create loops and duplicate trees.
                        if (!isLink) stack.Push(e.Path);
                    }
                    else if (_settings.IsExcludedFile(e.Extension) || IsExcludedPath(e.Path)) continue;
                    _store.Upsert(e, gen);
                    if (++inBatch >= 2000)
                    {
                        _store.CommitBatch();
                        inBatch = 0;
                        // A crawl of a whole drive takes minutes: don't make new files wait for it.
                        ApplyFileChanges(ct);
                        if (sw.ElapsedMilliseconds > 1000) { Publish(IndexPhase.Scanning, root); sw.Restart(); }
                        _store.BeginBatch();
                    }
                }
            }
            if (DirLister.Stat(root) is { } self) _store.Upsert(self, gen);
            else _store.Upsert(new DirectoryInfo(root), gen);
        }
        finally { _store.CommitBatch(); }
        if (sweep) _store.DeleteStale(root, gen);
    }

    /// <summary>True for junctions and symbolic links. Other reparse points are real folders: cloud sync
    /// roots (Baidu Netdisk sync disk, OneDrive, iCloud) mark every folder as one, and must be crawled.</summary>
    internal static bool IsLink(DirectoryInfo d)
    {
        if ((d.Attributes & FileAttributes.ReparsePoint) == 0) return false;
        try { return d.LinkTarget != null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
    }

    // Enumeration can throw midway (folder deleted, permissions changed); skip the rest of that folder.
    private static IEnumerable<FileSystemInfo> Safe(IEnumerable<FileSystemInfo> source)
    {
        using var it = source.GetEnumerator();
        while (true)
        {
            try { if (!it.MoveNext()) yield break; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { yield break; }
            yield return it.Current;
        }
    }

    private EnumerationOptions EnumOptions() => new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        AttributesToSkip = _settings.IndexHiddenFiles ? FileAttributes.System : FileAttributes.System | FileAttributes.Hidden,
    };

    // The index (e.g. D:\SpotlightWinData\index.db, -wal, -shm) sits inside a watched drive. Indexing
    // it made every write to the index raise a change event that woke the indexer to write again,
    // which kept it too busy to pick up new files.
    private readonly string _ownFiles;

    private bool IsExcludedPath(string path) =>
        path.StartsWith(_ownFiles, StringComparison.OrdinalIgnoreCase) ||
        _settings.ExcludedPaths.Any(p => path.StartsWith(Path.TrimEndingDirectorySeparator(p), StringComparison.OrdinalIgnoreCase));

    private bool IsExcluded(string path)
    {
        if (IsExcludedPath(path)) return true;
        var root = Roots.FirstOrDefault(r => path.StartsWith(r, StringComparison.OrdinalIgnoreCase));
        if (root == null) return true;
        var rel = path[root.Length..];
        foreach (var seg in rel.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (_excludedNames.Contains(seg)) return true;
            if (!_settings.IndexHiddenFiles && seg.StartsWith('.') && !OperatingSystem.IsWindows()) return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- watching

    private void StartWatchers()
    {
        foreach (var root in Roots)
        {
            if (JournalOf(root) != null) continue; // followed through the change journal
            try
            {
                var w = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    InternalBufferSize = 64 * 1024,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                w.Created += (_, e) => Enqueue(e.FullPath);
                w.Changed += (_, e) => Enqueue(e.FullPath);
                w.Deleted += (_, e) => Enqueue(e.FullPath);
                w.Renamed += (_, e) => { Enqueue(e.OldFullPath); Enqueue(e.FullPath); };
                // The kernel buffer overflowed (huge copy/unzip): we lost events, so re-crawl that root.
                w.Error += (_, _) => { _rescanRoots.Enqueue(root); _wake.Set(); };
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch (Exception e) when (e is ArgumentException or IOException or PlatformNotSupportedException) { }
        }
    }

    private void Enqueue(string path)
    {
        if (IsExcluded(path)) return;
        _changes[path] = 0;
        _wake.Set();
    }

    /// <summary>Applies queued changes to single files (not folders, whose subtree may be large).
    /// Called in the middle of crawls and content batches so new files show up within seconds.</summary>
    private void ApplyFileChanges(CancellationToken ct) => ApplyChanges(ct, filesOnly: true);

    private void ApplyChanges(CancellationToken ct, bool filesOnly = false)
    {
        if (_changes.IsEmpty) return;
        var paths = _changes.Keys.ToList();
        foreach (var p in paths) _changes.TryRemove(p, out _);
        var options = EnumOptions();
        int urgent = 0;
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (Directory.Exists(path))
                {
                    if (filesOnly) { _changes[path] = 0; continue; }
                    // New or moved-in folder: index its whole subtree.
                    ScanRoot(path, _store.NextGeneration(), ct);
                }
                else if (File.Exists(path))
                {
                    var fi = new FileInfo(path);
                    if ((fi.Attributes & options.AttributesToSkip) != 0) continue;
                    if (_settings.IsExcludedFile(fi.Extension)) { _store.DeletePath(path); continue; }
                    _store.BeginBatch();
                    try { _store.Upsert(fi, _store.NextGeneration()); }
                    finally { _store.CommitBatch(); }
                    // A file just saved or downloaded is likely what the user searches for next:
                    // read it ahead of the backlog (a few hundred at most, e.g. when a folder is pasted).
                    if (urgent < 200 && _store.Find(path) is { WantsContent: true, ContentRead: false } rec)
                    {
                        _urgent.Enqueue(rec.Id);
                        urgent++;
                    }
                }
                else _store.DeletePath(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    // ---------------------------------------------------------------- contents

    /// <summary>Extracts text for pending documents. Returns true if work remains.</summary>
    /// <param name="readAny">Whether any document was read.</param>
    /// <param name="unthrottled">Tests and the CLI: don't slow down for the user.</param>
    private bool ReadContents(CancellationToken ct, out bool readAny, bool ignoreBattery = false, bool unthrottled = false)
    {
        readAny = false;
        if (!_settings.IndexContents) return false;
        // Resting after a document while the user is active (see Throttle): come back later.
        if (!unthrottled && DateTime.UtcNow < _restUntil) return true;
        _rate.Restart();
        while (!ct.IsCancellationRequested)
        {
            while (!_urgent.IsEmpty) ReadUrgent(ct);
            // File changes take priority over the (possibly long) content backlog.
            if (!_changes.IsEmpty || !_rescanRoots.IsEmpty || _fullRescanRequested || JournalsDue) return true;
            bool onBattery = PowerAware.OnBattery();
            if (!ignoreBattery && _settings.PauseContentIndexingOnBattery && onBattery)
            {
                Publish(IndexPhase.PausedOnBattery);
                return true;
            }

            // Fast mode: plugged in with a real backlog (first run, new drive) and nobody at the
            // computer → several workers. Otherwise one background-priority worker, which is all
            // day-to-day changes need; while the user is typing or clicking it also pauses between
            // documents (see Throttle), so reading never competes with what they are doing.
            bool userActive = !unthrottled && UserActivity.Active;
            int workers = _settings.FastIndexingWhenPluggedIn && !onBattery && !userActive && _store.PendingContentCount() > 50
                ? Math.Clamp(Environment.ProcessorCount / 2, 2, 6)
                : 1;
            FastMode = workers > 1;

            // Small batches, so a document asked for with ReadSoon waits for at most a few others.
            var batch = _store.PendingContent(workers * 4);
            if (batch.Count == 0) return false;
            Publish(IndexPhase.ReadingContents);

            var results = new (long, long, string?, string?)[batch.Count];
            if (workers == 1)
            {
                for (int i = 0; i < batch.Count && !ChangesWaiting; i++)
                {
                    var sw = Stopwatch.StartNew();
                    var text = ExtractSafely(batch[i], ct, out var note);
                    results[i] = (batch[i].Id, batch[i].MTime, text, note);
                    if (userActive) { Throttle(sw.Elapsed); break; }
                }
            }
            else
            {
                Parallel.For(0, batch.Count, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = ct }, (i, loop) =>
                {
                    // A file was created or a document asked for: stop starting new documents.
                    if (ChangesWaiting) { loop.Stop(); return; }
                    var text = ExtractSafely(batch[i], ct, out var note);
                    results[i] = (batch[i].Id, batch[i].MTime, text, note);
                });
            }
            ct.ThrowIfCancellationRequested();
            var done = results.Where(r => r.Item1 != 0).ToList();
            _store.SetContents(done);
            _rate.Add(done.Count);
            readAny |= done.Count > 0;
            if (DateTime.UtcNow < _restUntil) return true;
        }
        return true;
    }

    private DateTime _restUntil;

    /// <summary>While the user is at the computer, rest three times as long as a document took
    /// (at least 0.3 s, at most 5 s): reading then uses at most a quarter of one core and of the disk.
    /// The rest is not a sleep: the journals and file changes are still handled meanwhile.</summary>
    private void Throttle(TimeSpan took) =>
        _restUntil = DateTime.UtcNow + TimeSpan.FromMilliseconds(Math.Clamp(took.TotalMilliseconds * 3, 300, 5000));

    private bool ChangesWaiting => !_changes.IsEmpty || !_urgent.IsEmpty || JournalsDue;

    /// <summary>True while documents are being read with several workers.</summary>
    public bool FastMode { get; private set; }

    private readonly Throughput _rate = new();

    /// <summary>Documents per second over the last minute or so, for the time-remaining estimate.</summary>
    private sealed class Throughput
    {
        private readonly Queue<(DateTime at, int n)> _samples = new();
        private readonly object _gate = new();

        public void Restart() { lock (_gate) _samples.Clear(); }

        public void Add(int n)
        {
            lock (_gate)
            {
                _samples.Enqueue((DateTime.UtcNow, n));
                while (_samples.Count > 2 && DateTime.UtcNow - _samples.Peek().at > TimeSpan.FromSeconds(90)) _samples.Dequeue();
            }
        }

        public double PerSecond
        {
            get
            {
                lock (_gate)
                {
                    if (_samples.Count < 2) return 0;
                    var span = (_samples.Last().at - _samples.Peek().at).TotalSeconds;
                    return span <= 0 ? 0 : _samples.Skip(1).Sum(s => s.n) / span;
                }
            }
        }
    }

    /// <summary>Time allowed for one document: a minute, plus a second per MB, at most 15 minutes.
    /// The indexer runs at background priority, so big PDFs need far longer than in the foreground.</summary>
    internal static TimeSpan ExtractionBudget(long size) =>
        TimeSpan.FromSeconds(Math.Min(900, 60 + size / (1024 * 1024)));

    private string? ExtractSafely(PendingContent item, CancellationToken ct, out string? note)
    {
        note = null;
        var extractor = ContentExtractors.Default.For(item.Ext);
        if (extractor == null) return null;
        // A single malformed document must not stall the queue.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ExtractionBudget(item.Size));
        try
        {
            var text = extractor is PdfExtractor pdf
                ? pdf.ExtractWithNote(item.Path, _settings.MaxContentChars, timeout.Token, out note)
                : extractor.Extract(item.Path, MaxChars(item.Ext), timeout.Token);
            if (text != null && Settings.CategoryOf(item.Ext) == ContentCategory.Spreadsheet && text.Length >= MaxChars(item.Ext))
                note = $"表格文字较多，只读取了前 {MaxChars(item.Ext):N0} 个字符";
            // Shutting down: don't record a half-read document as done.
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(text) && note == null)
                note = item.Ext == ".pdf" ? "没有可提取的文字（可能是扫描件）" : "没有可提取的文字";
            return text;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { note = "读取超时"; return null; }
        catch (Exception e) when (e is not OperationCanceledException) { note = "读取失败：" + e.Message; return null; }
    }

    /// <summary>Spreadsheets get a smaller text budget than prose: a big data sheet would otherwise
    /// cost megabytes of index for numbers nobody searches for.</summary>
    private int MaxChars(string ext) =>
        Settings.CategoryOf(ext) == ContentCategory.Spreadsheet && !_settings.FullContentIndex ? Math.Min(_settings.MaxContentChars, _settings.MaxSpreadsheetChars) : _settings.MaxContentChars;

    // ---------------------------------------------------------------- diagnostics

    /// <summary>Explains, in words for the user, whether a file is indexed and whether its text is searchable.</summary>
    public string Diagnose(string path)
    {
        path = Path.GetFullPath(path);
        var lines = new List<string> { path };
        if (!File.Exists(path) && !Directory.Exists(path)) return "文件不存在：" + path;
        if (!Roots.Any(r => path.StartsWith(Settings.WithSeparator(r), StringComparison.OrdinalIgnoreCase) || path.Equals(r, StringComparison.OrdinalIgnoreCase)))
            return $"{path}\n不在索引范围内。当前索引的位置：{string.Join("、", Roots)}。可以在设置文件的 Roots 中加入它所在的文件夹。";
        if (IsExcluded(path)) return path + "\n位于被排除的文件夹（ExcludedDirNames / ExcludedPaths）。";

        var rec = _store.Find(path);
        if (rec == null) return path + "\n还没有被扫描到。索引可能仍在进行中，或者它是隐藏/系统文件。";
        lines.Add("文件名已索引。");
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (!rec.WantsContent)
        {
            if (!_settings.IndexContents) lines.Add("正文索引已在设置中关闭。");
            else if (!ContentExtractors.Default.CanExtract(ext)) lines.Add($"不支持读取 {ext} 文件的正文。");
            else if (_settings.IsExcludedContentPath(path)) lines.Add("它所在的文件夹设置为只收录文件名、不读正文（设置 →「只收录文件名的文件夹」）。");
            else if (!_settings.WantsContentFor(ext, path)) lines.Add($"{ext} 这类文件设置为只收录文件名（可在设置 →「读取正文的文件类型」中打开）。");
            else
            {
                long limitMb = ext == ".pdf" ? _settings.MaxPdfFileSizeMB : _settings.MaxContentFileSizeMB;
                if (rec.Size > limitMb * 1024L * 1024L) lines.Add($"文件 {rec.Size / 1024 / 1024} MB，超过正文索引上限 {limitMb} MB。");
                else lines.Add("这是网盘的「仅在线」文件，为避免触发下载没有读取正文。");
            }
        }
        else if (!rec.ContentRead)
        {
            ReadSoon(new[] { rec.Id });
            lines.Add($"正文还在排队读取（还有 {_store.PendingContentCount():N0} 个文档待读取），已安排立即读取它，稍等片刻即可按正文搜到。");
        }
        else if (rec.HasContent) lines.Add($"正文已读取 {rec.ContentChars:N0} 个字符。" + (rec.Note != null ? rec.Note + "。" : ""));
        else lines.Add("正文为空：" + (rec.Note ?? "没有可提取的文字"));
        return string.Join("\n", lines);
    }

    // ---------------------------------------------------------------- status

    private DateTime _lastPublish;

    private void Publish(IndexPhase phase, string? detail = null)
    {
        // Counting is cheap but not free; throttle while busy.
        if (phase == Status.Phase && phase != IndexPhase.Idle && (DateTime.UtcNow - _lastPublish).TotalMilliseconds < 500) return;
        _lastPublish = DateTime.UtcNow;
        var (files, content) = _store.Counts();
        int pending = _settings.IndexContents ? _store.PendingContentCount() : 0;
        double rate = _rate.PerSecond;
        TimeSpan? eta = phase == IndexPhase.ReadingContents && rate > 0 ? TimeSpan.FromSeconds(pending / rate) : null;
        Status = new IndexStatus(phase, files, content, pending, detail, FastMode, eta);
        StatusChanged?.Invoke(Status);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _wake.Set();
        foreach (var w in _watchers) w.Dispose();
        _thread?.Join(TimeSpan.FromSeconds(3));
        _wake.Dispose();
    }
}
