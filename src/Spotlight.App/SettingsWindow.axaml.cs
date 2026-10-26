using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Spotlight.Core;

namespace Spotlight.App;

/// <summary>Settings panel: hotkey, indexed locations, exclusions, file-type filters, content options.</summary>
public partial class SettingsWindow : Window
{
    private static readonly (FileKind kind, string label)[] Kinds =
    {
        (FileKind.Pdf, "PDF"),
        (FileKind.Document, "Word 等文稿"),
        (FileKind.Spreadsheet, "表格"),
        (FileKind.Presentation, "演示文稿"),
        (FileKind.Text, "文本（txt、md、log）"),
        (FileKind.Code, "代码"),
        (FileKind.Image, "图片"),
        (FileKind.Audio, "音频"),
        (FileKind.Video, "视频"),
        (FileKind.Archive, "压缩包"),
        (FileKind.Other, "其他文件"),
    };

    private static readonly (ContentCategory cat, string label)[] ContentKinds =
    {
        (ContentCategory.Pdf, "PDF"),
        (ContentCategory.Word, "Word 文稿"),
        (ContentCategory.Presentation, "PPT 演示文稿"),
        (ContentCategory.Text, "文本、Markdown"),
        (ContentCategory.Spreadsheet, "表格（xlsx、xls、csv、WPS）"),
        (ContentCategory.Web, "网页"),
        (ContentCategory.Code, "代码与数据（py、json、xml…）"),
        (ContentCategory.Log, "日志与配置（log、ini…）"),
    };

    private readonly Settings _settings;
    private readonly ObservableCollection<string> _noContent = new();
    private readonly List<(ContentCategory cat, CheckBox box)> _contentBoxes = new();
    private string? _newIndexFolder;

    /// <summary>Raised after saving when the user picked another folder for the index.</summary>
    public event Action<string>? IndexFolderChosen;
    private readonly ObservableCollection<string> _roots = new();
    private readonly ObservableCollection<string> _excluded = new();
    private readonly List<(FileKind kind, CheckBox box)> _kindBoxes = new();
    private bool _usingDefaultRoots;

    /// <summary>Raised after the settings were saved; the caller re-registers the hotkey and re-indexes.</summary>
    public event Action? Saved;

    /// <summary>True while the hotkey box has the focus and records keys; the app pauses the current
    /// hotkey meanwhile, or pressing it would open the search window instead of being recorded.</summary>
    public event Action<bool>? HotkeyRecording;

    /// <summary>The user asked to remove the app (the app confirms and does it).</summary>
    public event Action? UninstallRequested;

    public SettingsWindow() : this(new Settings()) { }

    public SettingsWindow(Settings settings)
    {
        _settings = settings;
        InitializeComponent();
        RootsList.ItemsSource = _roots;
        ExcludedList.ItemsSource = _excluded;

        HotkeyBox.Text = settings.Hotkey;
        HotkeyBox.AddHandler(KeyDownEvent, OnHotkeyKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        HotkeyBox.GotFocus += (_, _) => { _hotkeyBefore = HotkeyBox.Text; HotkeyRecording?.Invoke(true); };
        HotkeyBox.LostFocus += (_, _) =>
        {
            // Only modifiers were pressed (e.g. "Ctrl+…"): keep the previous hotkey.
            if (!GlobalHotkey.TryParse(HotkeyBox.Text ?? "", out _, out _)) HotkeyBox.Text = _hotkeyBefore;
            HotkeyRecording?.Invoke(false);
        };
        Closed += (_, _) => { if (HotkeyBox.IsFocused) HotkeyRecording?.Invoke(false); };
        SetRoots(settings.Roots.Count == 0 ? settings.EffectiveRoots() : settings.Roots, settings.Roots.Count == 0);
        foreach (var p in settings.ExcludedPaths) _excluded.Add(p);
        foreach (var (kind, label) in Kinds)
        {
            var box = new CheckBox
            {
                Content = label,
                IsChecked = !settings.ExcludedKinds.Contains(kind.ToString(), StringComparer.OrdinalIgnoreCase),
                Margin = new Avalonia.Thickness(0, 0, 18, 4),
            };
            _kindBoxes.Add((kind, box));
            KindsPanel.Children.Add(box);
        }
        ExtensionsBox.Text = string.Join(", ", settings.ExcludedExtensions);
        foreach (var (cat, label) in ContentKinds)
        {
            var box = new CheckBox
            {
                Content = label,
                IsChecked = settings.ContentCategories.Contains(cat.ToString(), StringComparer.OrdinalIgnoreCase),
                Margin = new Avalonia.Thickness(0, 0, 18, 4),
            };
            _contentBoxes.Add((cat, box));
            ContentKindsPanel.Children.Add(box);
        }
        NoContentList.ItemsSource = _noContent;
        foreach (var p in settings.ExcludedContentPaths) _noContent.Add(p);
        IndexFolderText.Text = settings.IndexFolder ?? Settings.AppDataDir;
        ContentsCheck.IsChecked = settings.IndexContents;
        FullContentCheck.IsChecked = settings.FullContentIndex;
        AllNamesCheck.IsChecked = settings.IndexAllNames;
        FullContentCheck.IsCheckedChanged += (_, _) => UpdateContentChoices();
        ContentsCheck.IsCheckedChanged += (_, _) => UpdateContentChoices();
        UpdateContentChoices();
        PdfLimit.Value = settings.MaxPdfFileSizeMB;
        DocLimit.Value = settings.MaxContentFileSizeMB;
        BatteryCheck.IsChecked = settings.PauseContentIndexingOnBattery;
        FastCheck.IsChecked = settings.FastIndexingWhenPluggedIn;
        HiddenCheck.IsChecked = settings.IndexHiddenFiles;
        AutostartCheck.IsChecked = SafeAutostart();
        UninstallButton.Click += (_, _) => { Close(); UninstallRequested?.Invoke(); };

        AddRoot.Click += async (_, _) => { if (await PickFolder("选择要搜索的文件夹或磁盘") is { } p) { AddUnique(_roots, p); _usingDefaultRoots = false; UpdateRootsHint(); } };
        RemoveRoot.Click += (_, _) => { if (RootsList.SelectedItem is string s) { _roots.Remove(s); _usingDefaultRoots = false; UpdateRootsHint(); } };
        DefaultRoots.Click += (_, _) => SetRoots(new Settings().EffectiveRoots(), true);
        AddExcluded.Click += async (_, _) => { if (await PickFolder("选择不需要搜索的文件夹") is { } p) AddUnique(_excluded, p); };
        RemoveExcluded.Click += (_, _) => { if (ExcludedList.SelectedItem is string s) _excluded.Remove(s); };
        AddNoContent.Click += async (_, _) => { if (await PickFolder("选择只收录文件名的文件夹") is { } p) AddUnique(_noContent, p); };
        RemoveNoContent.Click += (_, _) => { if (NoContentList.SelectedItem is string s) _noContent.Remove(s); };
        ChangeIndexFolder.Click += async (_, _) =>
        {
            if (await PickFolder("选择保存索引的文件夹") is { } p) { _newIndexFolder = p; IndexFolderText.Text = p + "（保存后搬过去）"; }
        };
        CancelButton.Click += (_, _) => Close();
        SaveButton.Click += (_, _) => Save();
    }

    private void SetRoots(IEnumerable<string> roots, bool isDefault)
    {
        _roots.Clear();
        foreach (var r in roots) _roots.Add(r);
        _usingDefaultRoots = isDefault;
        UpdateRootsHint();
    }

    private void UpdateRootsHint() => RootsHint.Text = _usingDefaultRoots
        ? "当前为默认位置：桌面、文档、下载、图片、音乐、视频、OneDrive，以及除系统盘外的所有本地磁盘。开始菜单里的应用总会被索引。"
        : "只搜索下面这些位置。开始菜单里的应用总会被索引。";

    private static void AddUnique(ObservableCollection<string> list, string path)
    {
        if (!list.Contains(path, StringComparer.OrdinalIgnoreCase)) list.Add(path);
    }

    private async Task<string?> PickFolder(string title)
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }

    // In full mode every kind and folder is read, so the narrower choices are shown greyed out
    // (kept, not cleared: they apply again when full mode is turned off).
    private void UpdateContentChoices()
    {
        bool contents = ContentsCheck.IsChecked == true;
        bool narrow = contents && FullContentCheck.IsChecked != true;
        FullContentCheck.IsEnabled = contents;
        foreach (var (_, box) in _contentBoxes) box.IsEnabled = narrow;
        NoContentList.IsEnabled = narrow;
    }

    private static bool SafeAutostart()
    {
        try { return Platform.StartsWithWindows; } catch (Exception) { return false; }
    }

    private string? _hotkeyBefore;

    /// <summary>Turns the keys pressed in the hotkey box into "Ctrl+Shift+F" and so on.</summary>
    private void OnHotkeyKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab && e.KeyModifiers == KeyModifiers.None) return; // move on to the next field
        e.Handled = true;
        if (e.KeyModifiers == KeyModifiers.None && e.Key is Key.Escape or Key.Back or Key.Delete)
        {
            HotkeyBox.Text = _hotkeyBefore; // cancel
            return;
        }
        var text = HotkeyText(e.KeyModifiers, e.Key);
        HotkeyBox.Text = text ?? ModifierText(e.KeyModifiers) + "…";
        Error.Text = text == null && e.KeyModifiers == KeyModifiers.None && KeyName(e.Key) != null
            ? "单个按键不能当快捷键，请同时按住 Ctrl、Alt、Shift 或 Win。" : "";
    }

    private static string ModifierText(KeyModifiers m) =>
        (m.HasFlag(KeyModifiers.Control) ? "Ctrl+" : "") + (m.HasFlag(KeyModifiers.Alt) ? "Alt+" : "")
        + (m.HasFlag(KeyModifiers.Shift) ? "Shift+" : "") + (m.HasFlag(KeyModifiers.Meta) ? "Win+" : "");

    /// <summary>The hotkey for these keys in the form <see cref="GlobalHotkey.TryParse"/> reads, or
    /// null when it is not complete or not usable (a lone letter would fire while typing anywhere).</summary>
    internal static string? HotkeyText(KeyModifiers mods, Key key)
    {
        var name = KeyName(key);
        if (name == null) return null;
        bool functionKey = key is >= Key.F1 and <= Key.F24;
        if (mods == KeyModifiers.None && !functionKey) return null;
        return ModifierText(mods) + name;
    }

    private static string? KeyName(Key key) => key switch
    {
        >= Key.A and <= Key.Z => key.ToString(),
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
        >= Key.F1 and <= Key.F24 => key.ToString(),
        Key.Space => "Space",
        Key.Enter => "Enter",
        Key.Insert => "Insert",
        Key.OemTilde => "`",
        Key.OemSemicolon => ";",
        Key.OemQuestion => "/",
        Key.OemPeriod => ".",
        Key.OemComma => ",",
        _ => null, // Ctrl, Shift… on their own, or keys the hotkey can't use
    };

    private void Save()
    {
        var hotkey = (HotkeyBox.Text ?? "").Trim();
        if (!GlobalHotkey.TryParse(hotkey, out _, out _))
        {
            Error.Text = "请点快捷键框，按下一个组合键，例如 Ctrl+Space。";
            return;
        }
        if (_roots.Count == 0)
        {
            Error.Text = "至少需要一个搜索位置。";
            return;
        }

        _settings.Hotkey = hotkey;
        _settings.Roots = _usingDefaultRoots ? new List<string>() : _roots.ToList();
        _settings.ExcludedPaths = _excluded.ToList();
        _settings.ExcludedKinds = _kindBoxes.Where(k => k.box.IsChecked != true).Select(k => k.kind.ToString()).ToList();
        _settings.ExcludedExtensions = (ExtensionsBox.Text ?? "")
            .Split(new[] { ',', '，', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(Settings.NormalizeExtension).Where(e => e.Length > 1).Distinct().ToList();
        _settings.IndexContents = ContentsCheck.IsChecked == true;
        _settings.FullContentIndex = FullContentCheck.IsChecked == true;
        _settings.IndexAllNames = AllNamesCheck.IsChecked == true;
        _settings.MaxPdfFileSizeMB = (int)(PdfLimit.Value ?? 300);
        _settings.MaxContentFileSizeMB = (int)(DocLimit.Value ?? 20);
        _settings.PauseContentIndexingOnBattery = BatteryCheck.IsChecked == true;
        _settings.FastIndexingWhenPluggedIn = FastCheck.IsChecked == true;
        _settings.IndexHiddenFiles = HiddenCheck.IsChecked == true;
        _settings.ContentCategories = _contentBoxes.Where(b => b.box.IsChecked == true).Select(b => b.cat.ToString()).ToList();
        _settings.ExcludedContentPaths = _noContent.ToList();
        try { Platform.StartsWithWindows = AutostartCheck.IsChecked == true; } catch (Exception) { }

        try { _settings.Save(); }
        catch (Exception e)
        {
            Error.Text = "无法保存设置：" + e.Message;
            return;
        }
        Saved?.Invoke();
        if (_newIndexFolder != null) IndexFolderChosen?.Invoke(_newIndexFolder);
        Close();
    }
}
