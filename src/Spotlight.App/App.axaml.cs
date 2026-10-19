using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Spotlight.Core;
using Spotlight.Core.Indexing;
using Spotlight.Core.Search;

namespace Spotlight.App;

public partial class App : Application
{
    private Settings _settings = null!;
    private IndexStore _store = null!;
    private Indexer _indexer = null!;
    private SearchEngine _engine = null!;
    private SearchWindow _window = null!;
    private GlobalHotkey? _hotkey;
    private TrayIcon? _tray;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        _ = StartSafelyAsync(desktop);
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>A failure while starting must never leave an invisible process behind (no window, no
    /// tray icon, and every later launch just signalling it): say what went wrong, log it, and exit.</summary>
    private async Task StartSafelyAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            _settings = Settings.Load();
            await StartAsync(desktop);
        }
        catch (Exception e)
        {
            var log = Path.Combine(Path.GetTempPath(), "SpotlightWin-error.log");
            try
            {
                log = Path.Combine(Settings.AppDataDir, "error.log");
                File.AppendAllText(log, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e}\n\n");
            }
            catch (Exception) { }
            var w = new Window
            {
                Title = "聚焦搜索无法启动",
                Width = 520,
                SizeToContent = SizeToContent.Height,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Content = new StackPanel
                {
                    Margin = new Thickness(24, 20),
                    Children =
                    {
                        new TextBlock { Text = "启动时出错：" + e.Message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                        new TextBlock { Text = "详细信息已保存到 " + log + "，可以发给开发者排查。", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = 0.65, FontSize = 12, Margin = new Thickness(0, 10, 0, 0) },
                    },
                },
            };
            w.Closed += (_, _) => desktop.Shutdown(1);
            w.Show();
        }
    }

    private async Task StartAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var dbPath = _settings.IndexDbPath;
        // Versions before 0.5.0 kept the index in %AppData% on the system drive: move it once.
        var legacy = Path.Combine(Settings.AppDataDir, "index.db");
        if (!SamePath(legacy, dbPath) && File.Exists(legacy) && !File.Exists(dbPath))
            await RelocateWithProgress(legacy, dbPath);

        try { _store = new IndexStore(dbPath, _settings); }
        catch (Exception) when (!SamePath(dbPath, legacy))
        {
            // The index drive is missing or read-only (e.g. an unplugged disk): run from %AppData% this
            // time without changing the setting, so the index is back once the drive is.
            dbPath = legacy;
            _store = new IndexStore(dbPath, _settings);
        }
        _engine = new SearchEngine(dbPath);

        _window = new SearchWindow(_engine, _store, _settings);
        _window.HiddenAway += ScheduleTrim;
        _window.ReadContentSoon = ids => _indexer.ReadSoon(ids);
        StartIndexer();
        RegisterHotkey();

        SingleInstance.ListenForShow(() => Dispatcher.UIThread.Post(_window.ShowSearch));
        try { Platform.RefreshStartupEntry(); } catch (Exception) { }
        CreateTrayIcon();

        desktop.ShutdownRequested += (_, _) => Cleanup();
        // Launched by the user (not by Windows at sign-in): show the window so they see it's running.
        bool background = desktop.Args?.Contains("--background") == true;
        if (!background) _window.ShowSearch();
        else ScheduleTrim();
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Moves (and compacts) the index while a small window explains the wait.</summary>
    private async Task RelocateWithProgress(string from, string to)
    {
        var text = new TextBlock { Text = "正在把索引移到 " + Path.GetDirectoryName(to) + "，并清理不需要的正文…", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var note = new TextBlock { Text = "只需一次，通常一两分钟。完成后会自动继续。", Opacity = 0.65, FontSize = 12, Margin = new Thickness(0, 8, 0, 0) };
        var w = new Window
        {
            Title = "聚焦搜索",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new StackPanel { Margin = new Thickness(24, 20), Children = { text, new ProgressBar { IsIndeterminate = true, Margin = new Thickness(0, 12, 0, 0) }, note } },
        };
        w.Show();
        try
        {
            await Task.Run(() => IndexStore.Relocate(from, to, _settings, msg => Dispatcher.UIThread.Post(() => note.Text = msg)));
        }
        catch (Exception e)
        {
            // Keep working from the old place rather than not starting at all.
            text.Text = "移动索引失败：" + e.Message;
            await Task.Delay(4000);
        }
        finally { w.Close(); }
    }

    /// <summary>Settings → 索引位置 → 更改: stop everything, move the index, start again.</summary>
    private async Task MoveIndexTo(string folder)
    {
        var from = _settings.IndexDbPath;
        var to = Path.Combine(folder, "index.db");
        if (SamePath(from, to)) return;
        _window.HideSearch();
        _indexer.Dispose();
        _engine.Dispose();
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await RelocateWithProgress(from, to);
        if (File.Exists(to))
        {
            _settings.IndexFolder = folder;
            _settings.Save();
        }
        _store = new IndexStore(_settings.IndexDbPath, _settings);
        _engine = new SearchEngine(_settings.IndexDbPath);
        _window.Rebind(_engine, _store);
        StartIndexer();
    }

    private void StartIndexer()
    {
        _indexer = new Indexer(_store, _settings);
        var indexer = _indexer;
        indexer.StatusChanged += s => Dispatcher.UIThread.Post(() => { if (indexer == _indexer) _window.SetIndexStatus(s); });
        // Caught up with the disk: if nobody is looking at the window, give memory back.
        indexer.Idle += () => Dispatcher.UIThread.Post(() => { if (!_window.IsVisible) TrimNow(); });
        indexer.ReadAhead += ids => Dispatcher.UIThread.Post(() => _window.ContentArrived(ids));
        indexer.Start();
    }

    private void RegisterHotkey()
    {
        _hotkey?.Dispose();
        _hotkey = new GlobalHotkey();
        bool ok = _hotkey.Register(_settings.Hotkey, () => Dispatcher.UIThread.Post(_window.Toggle));
        if (!ok && OperatingSystem.IsWindows())
            _window.SetNotice($"快捷键 {_settings.Hotkey} 无法注册（可能已被其他程序占用），请在托盘菜单的「设置」中换一个。");
        else _window.ClearNotice();
        if (_showItem != null) _showItem.Header = $"打开搜索    {_settings.Hotkey}";
    }

    // ---------------------------------------------------------------- memory

    private DispatcherTimer? _trimTimer;

    /// <summary>Trims a few seconds after the window hides, unless it is shown again first.</summary>
    private void ScheduleTrim()
    {
        _trimTimer?.Stop();
        _trimTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _trimTimer.Tick += (_, _) =>
        {
            _trimTimer?.Stop();
            if (!_window.IsVisible) TrimNow();
        };
        _trimTimer.Start();
    }

    private void TrimNow()
    {
        Task.Run(() =>
        {
            try { _engine.ReleaseMemory(); } catch (Exception) { }
            MemoryTrimmer.Trim();
        });
    }

    // ---------------------------------------------------------------- tray

    private NativeMenuItem? _showItem;
    private NativeMenuItem? _autostartItem;

    private static bool SafeStartsWithWindows()
    {
        try { return Platform.StartsWithWindows; } catch (Exception) { return false; }
    }
    private SettingsWindow? _settingsWindow;

    private void CreateTrayIcon()
    {
        using var iconStream = AssetLoader.Open(new Uri("avares://SpotlightWin/Assets/icon.png"));

        _showItem = new NativeMenuItem($"打开搜索    {_settings.Hotkey}");
        _showItem.Click += (_, _) => _window.ShowSearch();
        var settings = new NativeMenuItem("设置…");
        settings.Click += (_, _) => OpenSettings();
        var rescan = new NativeMenuItem("重新扫描所有文件");
        rescan.Click += (_, _) => _indexer.RequestFullRescan();
        _autostartItem = new NativeMenuItem("开机自动启动") { ToggleType = NativeMenuItemToggleType.CheckBox, IsChecked = SafeStartsWithWindows() };
        _autostartItem.Click += (_, _) =>
        {
            try { Platform.StartsWithWindows = !SafeStartsWithWindows(); } catch (Exception) { }
            _autostartItem.IsChecked = SafeStartsWithWindows();
        };
        var check = new NativeMenuItem("检查某个文件为什么搜不到…");
        check.Click += async (_, _) => await CheckFileAsync();
        var quit = new NativeMenuItem("退出");
        quit.Click += (_, _) => Quit();

        _tray = new TrayIcon
        {
            Icon = new WindowIcon(iconStream),
            ToolTipText = "聚焦搜索",
            Menu = new NativeMenu { _showItem, new NativeMenuItemSeparator(), settings, _autostartItem, rescan, check, new NativeMenuItemSeparator(), quit },
        };
        _tray.Clicked += (_, _) => _window.ShowSearch();
        TrayIcon.SetIcons(this, new TrayIcons { _tray });
    }

    private void OpenSettings()
    {
        if (_settingsWindow != null) { _settingsWindow.Activate(); return; }
        _settingsWindow = new SettingsWindow(_settings);
        _settingsWindow.IndexFolderChosen += folder => _ = MoveIndexTo(folder);
        _settingsWindow.Saved += () =>
        {
            if (_autostartItem != null) _autostartItem.IsChecked = SafeStartsWithWindows();
            RegisterHotkey();
            // New locations or filters: restart the indexer. Its first full crawl adds what is now in
            // scope and removes what is not.
            _indexer.Dispose();
            StartIndexer();
        };
        _settingsWindow.Closed += (_, _) => { _settingsWindow = null; ScheduleTrim(); };
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private async Task CheckFileAsync()
    {
        var files = await _window.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "选择要检查的文件",
            AllowMultiple = false,
        });
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        _window.ShowSearch();
        if (path != null) _window.SetNotice(_indexer.Diagnose(path), sticky: false);
    }

    private void Quit()
    {
        Cleanup();
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    private bool _cleaned;

    private void Cleanup()
    {
        if (_cleaned) return;
        _cleaned = true;
        _hotkey?.Dispose();
        _tray?.Dispose();
        _indexer.Dispose();
        _engine.Dispose();
        _store.Dispose();
    }
}
