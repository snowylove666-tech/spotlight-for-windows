using System.Collections.Concurrent;
using System.Diagnostics;
using Spotlight.Core.Extraction;

namespace Spotlight.Core.Indexing;

public enum IndexPhase { Starting, Scanning, ReadingContents, Idle, PausedOnBattery }

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
///  1. a full crawl at start-up (cheap for unchanged files: only metadata is compared),
///  2. FileSystemWatcher events afterwards, debounced and applied incrementally,
///  3. content extraction for new or changed documents, paused while on battery.
/// </summary>
public sealed class Indexer : IDisposable
{
    private readonly IndexStore _store;
    private readonly Settings _settings;
    private readonly ConcurrentDictionary<string, byte> _changes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _rescanRoots = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly HashSet<string> _excludedNames;
    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;
    private volatile bool _fullRescanRequested;

    public event Action<IndexStatus>? StatusChanged;

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

    // ---------------------------------------------------------------- main loop

    private void Run()
    {
        PowerAware.MakeCurrentThreadBackground();
        var ct = _cts.Token;
        try
        {
            FullScan(ct);
            StartWatchers();
            while (!ct.IsCancellationRequested)
            {
                if (_fullRescanRequested) { _fullRescanRequested = false; FullScan(ct); }
                while (_rescanRoots.TryDequeue(out var root)) ScanRoot(root, _store.NextGeneration(), ct);
                ApplyChanges(ct);
                bool moreContent = ReadContents(ct);
                Publish(IndexPhase.Idle);
                _store.ReleaseMemory();
                Idle?.Invoke();
                // With content left to read (but paused on battery) re-check power once a minute.
                _wake.WaitOne(moreContent ? TimeSpan.FromSeconds(60) : Timeout.InfiniteTimeSpan);
                if (!_changes.IsEmpty) Thread.Sleep(Debounce);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Runs one full crawl and the content pass synchronously. Used by the CLI and tests.</summary>
    public void RunOnce(CancellationToken ct = default)
    {
        FullScan(ct);
        while (ReadContents(ct, ignoreBattery: true) && _store.PendingContentCount() > 0) { }
        Publish(IndexPhase.Idle);
    }

    private void FullScan(CancellationToken ct)
    {
        long gen = _store.NextGeneration();
        foreach (var root in Roots) ScanRoot(root, gen, ct);
        // Anything not seen by this crawl is gone, outside the configured locations, or now filtered out.
        _store.DeleteOlderThan(gen);
    }

    // ---------------------------------------------------------------- crawling

    private void ScanRoot(string root, long gen, CancellationToken ct)
    {
        if (!Directory.Exists(root)) return;
        Publish(IndexPhase.Scanning, root);
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(root));
        var options = EnumOptions();
        int inBatch = 0;
        var sw = Stopwatch.StartNew();
        _store.BeginBatch();
        try
        {
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var dir = stack.Pop();
                IEnumerable<FileSystemInfo> entries;
                try { entries = dir.EnumerateFileSystemInfos("*", options); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }

                foreach (var e in Safe(entries))
                {
                    if (e is DirectoryInfo d)
                    {
                        if (_excludedNames.Contains(d.Name) || IsExcludedPath(d.FullName)) continue;
                        // Don't follow junctions/symlinks: they create loops and duplicate trees.
                        if ((d.Attributes & FileAttributes.ReparsePoint) == 0) stack.Push(d);
                    }
                    else if (_settings.IsExcludedFile(e.Extension)) continue;
                    _store.Upsert(e, gen);
                    if (++inBatch >= 2000)
                    {
                        _store.CommitBatch();
                        inBatch = 0;
                        if (sw.ElapsedMilliseconds > 1000) { Publish(IndexPhase.Scanning, root); sw.Restart(); }
                        _store.BeginBatch();
                    }
                }
            }
            _store.Upsert(new DirectoryInfo(root), gen);
        }
        finally { _store.CommitBatch(); }
        _store.DeleteStale(root, gen);
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

    private bool IsExcludedPath(string path) =>
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

    private void ApplyChanges(CancellationToken ct)
    {
        if (_changes.IsEmpty) return;
        var paths = _changes.Keys.ToList();
        foreach (var p in paths) _changes.TryRemove(p, out _);
        var options = EnumOptions();
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (Directory.Exists(path))
                {
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
                }
                else _store.DeletePath(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    // ---------------------------------------------------------------- contents

    /// <summary>Extracts text for pending documents. Returns true if work remains.</summary>
    private bool ReadContents(CancellationToken ct, bool ignoreBattery = false)
    {
        if (!_settings.IndexContents) return false;
        _rate.Restart();
        while (!ct.IsCancellationRequested)
        {
            // File changes take priority over the (possibly long) content backlog.
            if (!_changes.IsEmpty || !_rescanRoots.IsEmpty || _fullRescanRequested) return true;
            bool onBattery = PowerAware.OnBattery();
            if (!ignoreBattery && _settings.PauseContentIndexingOnBattery && onBattery)
            {
                Publish(IndexPhase.PausedOnBattery);
                return true;
            }

            // Fast mode: plugged in with a real backlog (first run, new drive) → several normal-priority
            // workers. Otherwise one background-priority worker, which is all day-to-day changes need.
            int workers = _settings.FastIndexingWhenPluggedIn && !onBattery && _store.PendingContentCount() > 50
                ? Math.Clamp(Environment.ProcessorCount / 2, 2, 6)
                : 1;
            FastMode = workers > 1;

            var batch = _store.PendingContent(workers * 16);
            if (batch.Count == 0) return false;
            Publish(IndexPhase.ReadingContents);

            var results = new (long, long, string?, string?)[batch.Count];
            if (workers == 1)
            {
                for (int i = 0; i < batch.Count; i++)
                {
                    var text = ExtractSafely(batch[i], ct, out var note);
                    results[i] = (batch[i].Id, batch[i].MTime, text, note);
                }
            }
            else
            {
                Parallel.For(0, batch.Count, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = ct }, i =>
                {
                    var text = ExtractSafely(batch[i], ct, out var note);
                    results[i] = (batch[i].Id, batch[i].MTime, text, note);
                });
            }
            ct.ThrowIfCancellationRequested();
            _store.SetContents(results);
            _rate.Add(batch.Count);
        }
        return true;
    }

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
                : extractor.Extract(item.Path, _settings.MaxContentChars, timeout.Token);
            // Shutting down: don't record a half-read document as done.
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(text) && note == null)
                note = item.Ext == ".pdf" ? "没有可提取的文字（可能是扫描件）" : "没有可提取的文字";
            return text;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { note = "读取超时"; return null; }
        catch (Exception e) when (e is not OperationCanceledException) { note = "读取失败：" + e.Message; return null; }
    }

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
            else
            {
                long limitMb = ext == ".pdf" ? _settings.MaxPdfFileSizeMB : _settings.MaxContentFileSizeMB;
                if (rec.Size > limitMb * 1024L * 1024L) lines.Add($"文件 {rec.Size / 1024 / 1024} MB，超过正文索引上限 {limitMb} MB。");
                else lines.Add("这是网盘的「仅在线」文件，为避免触发下载没有读取正文。");
            }
        }
        else if (!rec.ContentRead) lines.Add($"正文还在排队读取（还有 {_store.PendingContentCount():N0} 个文档待读取）。");
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
