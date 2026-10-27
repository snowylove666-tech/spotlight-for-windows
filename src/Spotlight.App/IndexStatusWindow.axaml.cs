using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Spotlight.Core;
using Spotlight.Core.Indexing;

namespace Spotlight.App;

/// <summary>
/// "索引状态": what is indexed and what is not, how far the indexer is, and — after "重新扫描" or
/// switching the full content index — what changed, so the user can see that it happened.
/// </summary>
public partial class IndexStatusWindow : Window
{
    private readonly Settings _settings;
    private readonly Func<(Indexer indexer, IndexStore store)> _current;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    // The state when the user started something (a rescan, a content setting), to show the change.
    private Snapshot? _baseline;
    private string? _action;
    private DateTime _actionAt;

    private sealed record Snapshot(long Files, long WithContent, long Size);

    /// <summary>Raised by the "重新扫描所有文件" button.</summary>
    public event Action? RescanRequested;

    // Parameterless constructor for the XAML previewer.
    public IndexStatusWindow() : this(new Settings(), () => throw new InvalidOperationException()) { }

    public IndexStatusWindow(Settings settings, Func<(Indexer, IndexStore)> current)
    {
        _settings = settings;
        _current = current;
        InitializeComponent();
        CloseButton.Click += (_, _) => Close();
        RescanButton.Click += (_, _) => RescanRequested?.Invoke();
        _timer.Tick += (_, _) => Refresh();
        Opened += (_, _) => { Refresh(); _timer.Start(); };
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>Remembers the state now and describes the change from here on.</summary>
    public void Track(string action)
    {
        var (indexer, store) = _current();
        _baseline = new Snapshot(indexer.Status.Files, indexer.Status.WithContent, store.SizeBytes());
        if (_baseline.Files == 0) { var (f, c) = store.Counts(); _baseline = _baseline with { Files = f, WithContent = c }; }
        _action = action;
        _actionAt = DateTime.Now;
        Refresh();
    }

    private void Refresh()
    {
        (Indexer indexer, IndexStore store) cur;
        try { cur = _current(); } catch (Exception) { return; }
        var s = cur.indexer.Status;
        long size = cur.store.SizeBytes();
        long files = s.Files, withContent = s.WithContent;
        if (s.Phase == IndexPhase.Starting) (files, withContent) = cur.store.Counts();

        Phase.Text = s.Phase switch
        {
            IndexPhase.Idle => "✓ 已完成，正在跟踪文件变化（新增、修改、删除的文件几秒内会同步）",
            _ => s.Describe(),
        };
        // How changes are followed, per drive.
        var journaled = cur.indexer.JournalVolumes.Select(v => v.TrimEnd('\\')).ToList();
        var watched = cur.indexer.Roots.Select(r => Path.GetPathRoot(r)!.TrimEnd('\\')).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(v => !journaled.Contains(v, StringComparer.OrdinalIgnoreCase)).ToList();
        Phase.Text += "\n" + (journaled.Count > 0 ? $"{string.Join("、", journaled)} 用 NTFS 变更日志跟踪（和 Everything 一样；程序关闭或待机期间的变化，下次启动几秒内补上）" : "")
            + (journaled.Count > 0 && watched.Count > 0 ? "；" : "")
            + (watched.Count > 0 ? $"{string.Join("、", watched)} 用文件夹监视（非 NTFS 盘，启动时会重新扫描）" : "");
        Counts.Text = $"共收录 {files:N0} 个文件和文件夹，其中 {withContent:N0} 个文档可以按内容搜索"
            + (s.PendingContent > 0 ? $"，还有 {s.PendingContent:N0} 个文档的正文等待读取" : "");
        string folder = Path.GetDirectoryName(cur.store.DbPath) ?? "";
        Size.Text = $"索引占用 {Mb(size)}，位于 {folder}" + FreeSpace(folder);

        var last = cur.indexer.LastScan;
        LastScan.Text = last == null
            ? "本次启动后的首次扫描还在进行中。"
            : $"{last.Finished:MM-dd HH:mm} 完成，用时 {Took(last.Took)}：扫描前 {last.FilesBefore:N0} 项 → 扫描后 {last.FilesAfter:N0} 项，"
              + $"新增 {last.Added:N0}，删除 {last.Removed:N0}"
              + (last.TextDropped > 0 ? $"，清除了 {last.TextDropped:N0} 个文档不再需要的正文" : "")
              + (last.Unchanged > 0 ? $"，{last.Unchanged:N0} 项没有变化（不重写）" : "")
              + (last.MissingRoots.Count > 0 ? $"。以下位置不存在或无法访问：{string.Join("、", last.MissingRoots)}" : "。")
              + (last.Mft != null ? "\n" + last.Mft
                 : MftService.Installed ? "" : "\n逐个文件夹扫描。在设置 → 快速扫描 启用管理员后台服务后，直接读取主文件表。");

        bool custom = _settings.Roots.Count > 0;
        var roots = cur.indexer.Roots;
        Scope.Text = _settings.IndexAllNames
            ? $"收录全部文件名（像 Everything）：所有本地磁盘的全部文件和文件夹，共 {roots.Count} 个磁盘。AppData、Windows、Program Files、node_modules、隐藏文件等只收录文件名，排在你自己的文件后面"
            : custom
            ? $"自定义：{roots.Count} 个位置（设置 → 搜索位置）"
            : $"默认（全盘）：除系统盘外的所有本地磁盘，加上开始菜单、用户文件夹（桌面、文档、下载…）和系统盘上你自己建的文件夹，共 {roots.Count} 个位置";
        Roots.Text = string.Join("\n", roots.Take(14)) + (roots.Count > 14 ? $"\n…等 {roots.Count} 个" : "");

        ContentInfo.Text = !_settings.IndexContents
            ? "已关闭：只按文件名搜索（设置 → 文档正文）。"
            : _settings.FullContentIndex
                ? "完整正文索引：开启。所有类型（含代码、日志、网页）、所有文件夹都读取正文，搜得最全，索引最大。"
                : "部分正文索引（节省空间）：只读取 " + string.Join("、", _settings.ContentCategories.Select(CategoryName))
                  + " 的正文；其他类型只收录文件名。需要更全时，在设置 → 文档正文 打开「完整正文索引」。";

        var ex = new List<string>();
        if (_settings.ExcludedExtensions.Count > 0) ex.Add("你排除的扩展名：" + string.Join("、", _settings.ExcludedExtensions) + "（这些文件完全不收录）");
        if (_settings.ExcludedKinds.Count > 0) ex.Add("你排除的文件类型：" + string.Join("、", _settings.ExcludedKinds));
        if (_settings.ExcludedPaths.Count > 0) ex.Add("你排除的文件夹：" + string.Join("、", _settings.ExcludedPaths));
        if (!_settings.FullContentIndex && _settings.ExcludedContentPaths.Count > 0)
            ex.Add("只收录文件名、不读正文的文件夹：" + string.Join("、", _settings.ExcludedContentPaths));
        if (_settings.IndexAllNames)
            ex.Add("回收站、System Volume Information 等 Windows 内部文件夹");
        else
        {
            if (!_settings.IndexHiddenFiles) ex.Add("隐藏文件和系统文件");
            ex.Add($"系统和程序文件夹（Windows、Program Files、AppData、node_modules 等 {_settings.ExcludedDirNames.Count} 个名称）——在设置 → 搜索位置 打开「收录全部文件名」可以按文件名搜到它们");
        }
        ex.Add($"超过大小上限的文档只收录文件名（PDF {_settings.MaxPdfFileSizeMB} MB，其他 {_settings.MaxContentFileSizeMB} MB）");
        ex.Add("网盘里「仅在线」、没下载到本地的文件只收录文件名");
        Excluded.Text = string.Join("\n", ex.Select(e => "· " + e));

        UpdateBanner(cur.indexer, files, withContent, size, s);
    }

    private void UpdateBanner(Indexer indexer, long files, long withContent, long size, IndexStatus s)
    {
        if (_baseline == null || _action == null) { Banner.IsVisible = false; return; }
        var last = indexer.LastScan;
        bool scanDone = last != null && last.Started >= _actionAt.AddSeconds(-2);
        bool busy = s.Phase is IndexPhase.Starting or IndexPhase.Scanning || !scanDone;
        string change = $"文件 {_baseline.Files:N0} → {files:N0}（{Signed(files - _baseline.Files)}），"
            + $"可按内容搜索 {_baseline.WithContent:N0} → {withContent:N0}（{Signed(withContent - _baseline.WithContent)}），"
            + $"索引 {Mb(_baseline.Size)} → {Mb(size)}（{SignedMb(size - _baseline.Size)}）";
        string text;
        if (busy)
            text = $"{_action}：正在进行… {s.Describe()}\n{change}";
        else
        {
            text = $"✓ {_action}：扫描已完成（用时 {Took(last!.Took)}，新增 {last.Added:N0}，删除 {last.Removed:N0}）\n{change}";
            if (s.PendingContent > 0)
                text += $"\n还有 {s.PendingContent:N0} 个文档的正文在后台读取，索引大小和可按内容搜索的数量会继续变化。";
        }
        BannerText.Text = text;
        Banner.Background = new SolidColorBrush(Color.Parse(busy ? "#260A64D2" : "#2610A050"));
        Banner.IsVisible = true;
        Footer.Text = busy ? "可以关闭此窗口，扫描会在后台继续。" : "";
    }

    private static string CategoryName(string c) => c switch
    {
        "Pdf" => "PDF", "Word" => "Word", "Presentation" => "PPT", "Text" => "文本/Markdown",
        "Spreadsheet" => "表格", "Web" => "网页", "Code" => "代码", "Log" => "日志/配置", _ => c,
    };

    private static string Mb(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):F2} GB" : $"{bytes / (double)(1L << 20):F0} MB";
    private static string Signed(long n) => n == 0 ? "无变化" : n > 0 ? $"+{n:N0}" : $"{n:N0}";
    private static string SignedMb(long bytes) => Math.Abs(bytes) < 1L << 20 ? "基本不变" : (bytes > 0 ? "+" : "−") + Mb(Math.Abs(bytes));
    private static string Took(TimeSpan t) => t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} 分 {t.Seconds} 秒" : $"{Math.Max(1, (int)t.TotalSeconds)} 秒";

    private static string FreeSpace(string folder)
    {
        try { return $"（该盘剩余 {Mb(new DriveInfo(Path.GetPathRoot(folder)!).AvailableFreeSpace)}）"; }
        catch (Exception) { return ""; }
    }
}
