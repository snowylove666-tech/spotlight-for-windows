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

        _settings = Settings.Load();
        var dbPath = Path.Combine(Settings.AppDataDir, "index.db");
        _store = new IndexStore(dbPath, _settings);
        _engine = new SearchEngine(dbPath);

        _window = new SearchWindow(_engine, _store, _settings);
        _window.HiddenAway += ScheduleTrim;
        _window.ReadContentSoon = ids => _indexer.ReadSoon(ids);
        StartIndexer();
        RegisterHotkey();

        SingleInstance.ListenForShow(() => Dispatcher.UIThread.Post(_window.ShowSearch));
        CreateTrayIcon();

        desktop.ShutdownRequested += (_, _) => Cleanup();
        // Launched by the user (not by Windows at sign-in): show the window so they see it's running.
        bool background = desktop.Args?.Contains("--background") == true;
        if (!background) _window.ShowSearch();
        else ScheduleTrim();

        base.OnFrameworkInitializationCompleted();
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
        var check = new NativeMenuItem("检查某个文件为什么搜不到…");
        check.Click += async (_, _) => await CheckFileAsync();
        var quit = new NativeMenuItem("退出");
        quit.Click += (_, _) => Quit();

        _tray = new TrayIcon
        {
            Icon = new WindowIcon(iconStream),
            ToolTipText = "聚焦搜索",
            Menu = new NativeMenu { _showItem, new NativeMenuItemSeparator(), settings, rescan, check, new NativeMenuItemSeparator(), quit },
        };
        _tray.Clicked += (_, _) => _window.ShowSearch();
        TrayIcon.SetIcons(this, new TrayIcons { _tray });
    }

    private void OpenSettings()
    {
        if (_settingsWindow != null) { _settingsWindow.Activate(); return; }
        _settingsWindow = new SettingsWindow(_settings);
        _settingsWindow.Saved += () =>
        {
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
