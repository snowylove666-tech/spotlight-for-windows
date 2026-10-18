using System.Collections.ObjectModel;
using Avalonia.Controls;
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

    private readonly Settings _settings;
    private readonly ObservableCollection<string> _roots = new();
    private readonly ObservableCollection<string> _excluded = new();
    private readonly List<(FileKind kind, CheckBox box)> _kindBoxes = new();
    private bool _usingDefaultRoots;

    /// <summary>Raised after the settings were saved; the caller re-registers the hotkey and re-indexes.</summary>
    public event Action? Saved;

    public SettingsWindow() : this(new Settings()) { }

    public SettingsWindow(Settings settings)
    {
        _settings = settings;
        InitializeComponent();
        RootsList.ItemsSource = _roots;
        ExcludedList.ItemsSource = _excluded;

        HotkeyBox.Text = settings.Hotkey;
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
        ContentsCheck.IsChecked = settings.IndexContents;
        PdfLimit.Value = settings.MaxPdfFileSizeMB;
        DocLimit.Value = settings.MaxContentFileSizeMB;
        BatteryCheck.IsChecked = settings.PauseContentIndexingOnBattery;
        FastCheck.IsChecked = settings.FastIndexingWhenPluggedIn;
        HiddenCheck.IsChecked = settings.IndexHiddenFiles;
        AutostartCheck.IsChecked = SafeAutostart();

        AddRoot.Click += async (_, _) => { if (await PickFolder("选择要搜索的文件夹或磁盘") is { } p) { AddUnique(_roots, p); _usingDefaultRoots = false; UpdateRootsHint(); } };
        RemoveRoot.Click += (_, _) => { if (RootsList.SelectedItem is string s) { _roots.Remove(s); _usingDefaultRoots = false; UpdateRootsHint(); } };
        DefaultRoots.Click += (_, _) => SetRoots(new Settings().EffectiveRoots(), true);
        AddExcluded.Click += async (_, _) => { if (await PickFolder("选择不需要搜索的文件夹") is { } p) AddUnique(_excluded, p); };
        RemoveExcluded.Click += (_, _) => { if (ExcludedList.SelectedItem is string s) _excluded.Remove(s); };
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

    private static bool SafeAutostart()
    {
        try { return Platform.StartsWithWindows; } catch (Exception) { return false; }
    }

    private void Save()
    {
        var hotkey = (HotkeyBox.Text ?? "").Trim();
        if (!GlobalHotkey.TryParse(hotkey, out _, out _))
        {
            Error.Text = "快捷键格式不正确，例如 Alt+Space。";
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
        _settings.MaxPdfFileSizeMB = (int)(PdfLimit.Value ?? 300);
        _settings.MaxContentFileSizeMB = (int)(DocLimit.Value ?? 20);
        _settings.PauseContentIndexingOnBattery = BatteryCheck.IsChecked == true;
        _settings.FastIndexingWhenPluggedIn = FastCheck.IsChecked == true;
        _settings.IndexHiddenFiles = HiddenCheck.IsChecked == true;
        try { Platform.StartsWithWindows = AutostartCheck.IsChecked == true; } catch (Exception) { }

        try { _settings.Save(); }
        catch (Exception e)
        {
            Error.Text = "无法保存设置：" + e.Message;
            return;
        }
        Saved?.Invoke();
        Close();
    }
}
