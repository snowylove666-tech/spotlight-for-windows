using System.Text.Json;
using System.Text.Json.Serialization;

namespace Spotlight.Core;

public sealed class Settings
{
    public const int CurrentVersion = 2;

    /// <summary>Format version of this file; older files are upgraded on load.</summary>
    public int SettingsVersion { get; set; } = 1; // files from 0.1.0 have no version field

    /// <summary>Global hotkey, e.g. "Alt+Space", "Ctrl+Shift+Space", "Win+F2".</summary>
    public string Hotkey { get; set; } = "Alt+Space";

    /// <summary>Folders to index. Empty means the user's standard folders (Desktop, Documents, Downloads, ...)
    /// plus every other local fixed drive (D:\, E:\ ...).</summary>
    public List<string> Roots { get; set; } = new();

    /// <summary>Directory names skipped anywhere in the tree.</summary>
    public List<string> ExcludedDirNames { get; set; } = new()
    {
        "node_modules", ".git", ".svn", ".hg", ".vs", ".idea", "__pycache__", ".venv", "venv",
        "$RECYCLE.BIN", "System Volume Information", ".cache", ".gradle", ".nuget", "obj",
        "Windows", "Program Files", "Program Files (x86)", "ProgramData", "$WinREAgent", "Recovery", "PerfLogs",
    };

    /// <summary>Absolute paths (and everything under them) that are never indexed.</summary>
    public List<string> ExcludedPaths { get; set; } = new();

    /// <summary>Kinds of files never indexed, by <see cref="FileKind"/> name (e.g. "Text", "Image").</summary>
    public List<string> ExcludedKinds { get; set; } = new();

    /// <summary>Extensions never indexed, e.g. ".txt", ".log".</summary>
    public List<string> ExcludedExtensions { get; set; } = new();

    /// <summary>Whether to read and index the text inside documents.</summary>
    public bool IndexContents { get; set; } = true;

    /// <summary>Largest file whose contents are indexed, in MB. PDFs get their own limit.</summary>
    public int MaxContentFileSizeMB { get; set; } = 20;
    public int MaxPdfFileSizeMB { get; set; } = 300;

    /// <summary>Longest text kept per file (characters). Longer documents are truncated.</summary>
    public int MaxContentChars { get; set; } = 5_000_000;

    /// <summary>Stop reading file contents while the laptop runs on battery (file names stay up to date).</summary>
    public bool PauseContentIndexingOnBattery { get; set; } = true;

    /// <summary>When plugged in and many documents are waiting (first run, new drive), read them with
    /// several normal-priority workers; afterwards fall back to one low-priority worker.</summary>
    public bool FastIndexingWhenPluggedIn { get; set; } = true;

    public bool IndexHiddenFiles { get; set; } = false;

    public int MaxResults { get; set; } = 30;

    [JsonIgnore] public string? FilePath { get; private set; }

    /// <summary>True if files with this extension are filtered out by <see cref="ExcludedKinds"/> or
    /// <see cref="ExcludedExtensions"/>.</summary>
    public bool IsExcludedFile(string extension)
    {
        if (ExcludedExtensions.Any(e => NormalizeExtension(e).Equals(extension, StringComparison.OrdinalIgnoreCase))) return true;
        if (ExcludedKinds.Count == 0) return false;
        var kind = FileKinds.FromExtension(extension).ToString();
        return ExcludedKinds.Contains(kind, StringComparer.OrdinalIgnoreCase);
    }

    public static string NormalizeExtension(string e)
    {
        e = e.Trim().TrimStart('*');
        return e.StartsWith('.') ? e.ToLowerInvariant() : "." + e.ToLowerInvariant();
    }

    public Settings Clone()
    {
        var copy = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions)!;
        copy.FilePath = FilePath;
        return copy;
    }

    public static string AppDataDir
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpotlightWin");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static Settings Load(string? path = null)
    {
        path ??= Path.Combine(AppDataDir, "settings.json");
        Settings s;
        try
        {
            s = File.Exists(path) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), JsonOptions) ?? new() : new();
        }
        catch (Exception)
        {
            s = new(); // a broken settings file must never stop the launcher from starting
        }
        s.FilePath = path;
        bool upgraded = s.Upgrade();
        if (upgraded || !File.Exists(path)) s.Save();
        return s;
    }

    /// <summary>Brings settings written by an older version up to date without overriding values the
    /// user changed themselves. Returns true if anything changed.</summary>
    internal bool Upgrade()
    {
        if (SettingsVersion >= CurrentVersion) return false;
        if (SettingsVersion < 2)
        {
            // 0.1.0 limits were too small for long textbooks/courseware PDFs.
            if (MaxPdfFileSizeMB == 50) MaxPdfFileSizeMB = 300;
            if (MaxContentChars == 1_000_000) MaxContentChars = 5_000_000;
            foreach (var name in new Settings().ExcludedDirNames)
                if (!ExcludedDirNames.Contains(name, StringComparer.OrdinalIgnoreCase)) ExcludedDirNames.Add(name);
        }
        SettingsVersion = CurrentVersion;
        return true;
    }

    public void Save()
    {
        if (FilePath == null) return;
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
    }

    public IReadOnlyList<string> EffectiveRoots()
    {
        if (Roots.Count > 0) return Roots.Where(Directory.Exists).ToList();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(home, "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        };
        var oneDrive = Environment.GetEnvironmentVariable("OneDrive");
        if (!string.IsNullOrEmpty(oneDrive)) candidates.Add(oneDrive);
        candidates.AddRange(DataDrives());
        return Dedupe(candidates.Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p)));
    }

    /// <summary>Local fixed drives other than the system drive (whose user folders are covered above).</summary>
    public static IEnumerable<string> DataDrives()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<string>();
        var system = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";
        var list = new List<string>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType == DriveType.Fixed && d.IsReady && !d.Name.Equals(system, StringComparison.OrdinalIgnoreCase))
                    list.Add(d.RootDirectory.FullName);
            }
            catch (IOException) { }
        }
        return list;
    }

    /// <summary>Start Menu folders, indexed so installed programs can be launched by name.</summary>
    public static IReadOnlyList<string> AppRoots()
    {
        var list = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        };
        return Dedupe(list.Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p)));
    }

    // Drop roots nested inside another root (e.g. Documents inside OneDrive) so nothing is crawled twice.
    private static List<string> Dedupe(IEnumerable<string> paths)
    {
        var full = paths.Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return full.Where(p => !full.Any(o => o != p && p.StartsWith(WithSeparator(o), StringComparison.OrdinalIgnoreCase))).ToList();
    }

    /// <summary>"D:\docs" → "D:\docs\"; a drive root "D:\" already ends with one.</summary>
    public static string WithSeparator(string dir) =>
        Path.EndsInDirectorySeparator(dir) ? dir : dir + Path.DirectorySeparatorChar;
}
