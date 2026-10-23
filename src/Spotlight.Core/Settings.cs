using System.Text.Json;
using System.Text.Json.Serialization;

namespace Spotlight.Core;

public sealed class Settings
{
    public const int CurrentVersion = 7;

    /// <summary>Format version of this file; older files are upgraded on load.</summary>
    public int SettingsVersion { get; set; } = 1; // files from 0.1.0 have no version field

    /// <summary>Global hotkey, e.g. "Ctrl+Space", "Ctrl+Shift+Space", "Win+F2".</summary>
    public string Hotkey { get; set; } = "Ctrl+Space";

    /// <summary>Folders to index. Empty means the user's standard folders (Desktop, Documents, Downloads, ...)
    /// plus every other local fixed drive (D:\, E:\ ...).</summary>
    public List<string> Roots { get; set; } = new();

    /// <summary>Directory names skipped anywhere in the tree.</summary>
    public List<string> ExcludedDirNames { get; set; } = new()
    {
        "node_modules", ".git", ".svn", ".hg", ".vs", ".idea", "__pycache__", ".venv", "venv",
        "$RECYCLE.BIN", "System Volume Information", ".cache", ".gradle", ".nuget", "obj",
        "Windows", "Program Files", "Program Files (x86)", "ProgramData", "$WinREAgent", "Recovery", "PerfLogs",
        "AppData", "Windows.old", "$Windows.~BT", "$Windows.~WS", "$SysReset", "OneDriveTemp", "MSOCache",    };

    /// <summary>Absolute paths (and everything under them) that are never indexed.</summary>
    public List<string> ExcludedPaths { get; set; } = new();

    /// <summary>Kinds of files never indexed, by <see cref="FileKind"/> name (e.g. "Text", "Image").</summary>
    public List<string> ExcludedKinds { get; set; } = new();

    /// <summary>Extensions never indexed, e.g. ".txt", ".log".</summary>
    public List<string> ExcludedExtensions { get; set; } = new();

    /// <summary>Whether to read and index the text inside documents.</summary>
    public bool IndexContents { get; set; } = true;

    /// <summary>Kinds of files whose text is read, by <see cref="ContentCategory"/> name. The others
    /// are found by name only: spreadsheets, logs, code and config files are mostly data, and their
    /// text made up over half of a typical index without being what people search for.</summary>
    public List<string> ContentCategories { get; set; } = DefaultContentCategories();

    public static List<string> DefaultContentCategories() =>
        new() { nameof(ContentCategory.Pdf), nameof(ContentCategory.Word), nameof(ContentCategory.Presentation), nameof(ContentCategory.Text), nameof(ContentCategory.Spreadsheet) };

    /// <summary>"完整正文索引": read the text of every kind of file that has any (code, logs, config,
    /// web pages too), also inside the name-only folders, and whole spreadsheets. Finds more, at the
    /// cost of a larger index. The narrower choices are kept and apply again when it is turned off.</summary>
    public bool FullContentIndex { get; set; }

    /// <summary>Most text kept per spreadsheet (they are mostly numbers; the start holds the headers).</summary>
    public int MaxSpreadsheetChars { get; set; } = 300_000;

    /// <summary>Folders whose files are found by name only (their text is not read), e.g. program folders.</summary>
    public List<string> ExcludedContentPaths { get; set; } = new();

    /// <summary>Folder holding index.db. Null until first start, which picks <see cref="DefaultIndexFolder"/>.</summary>
    public string? IndexFolder { get; set; }

    [JsonIgnore] public string IndexDbPath => Path.Combine(IndexFolder ?? AppDataDir, "index.db");

    /// <summary>Whether the text of this file should be read, given its extension and location.</summary>
    public bool WantsContentFor(string ext, string path)
    {
        if (!IndexContents) return false;
        var cat = CategoryOf(ext);
        if (cat == ContentCategory.None) return false;
        if (FullContentIndex) return true;
        if (!ContentCategories.Contains(cat.ToString(), StringComparer.OrdinalIgnoreCase)) return false;
        return !IsExcludedContentPath(path);
    }

    public bool IsExcludedContentPath(string path) =>
        ExcludedContentPaths.Any(p => path.StartsWith(WithSeparator(Path.TrimEndingDirectorySeparator(p)), StringComparison.OrdinalIgnoreCase));

    /// <summary>Which group of readable files an extension belongs to (None: no text is ever read).</summary>
    public static ContentCategory CategoryOf(string ext) => ext.ToLowerInvariant() switch
    {
        ".pdf" => ContentCategory.Pdf,
        ".docx" or ".docm" or ".odt" => ContentCategory.Word,
        ".pptx" or ".odp" => ContentCategory.Presentation,
        ".txt" or ".md" or ".markdown" or ".rst" or ".tex" or ".srt" or ".ass" or ".vtt" => ContentCategory.Text,
        ".xlsx" or ".xlsm" or ".xls" or ".et" or ".ods" or ".csv" or ".tsv" => ContentCategory.Spreadsheet,
        ".html" or ".htm" => ContentCategory.Web,
        ".log" or ".ini" or ".cfg" or ".conf" or ".properties" => ContentCategory.Log,
        var e when Extraction.ContentExtractors.Default.CanExtract(e) => ContentCategory.Code,
        _ => ContentCategory.None,
    };

    /// <summary>A data drive (not the system drive) with the most free space, else %AppData%.</summary>
    public static string DefaultIndexFolder()
    {
        if (!OperatingSystem.IsWindows()) return AppDataDir;
        string? best = null;
        long free = 0;
        foreach (var root in DataDrives())
        {
            try
            {
                var d = new DriveInfo(root);
                if (d.AvailableFreeSpace > free && d.AvailableFreeSpace > 10L * 1024 * 1024 * 1024) { free = d.AvailableFreeSpace; best = root; }
            }
            catch (IOException) { }
        }
        return best == null ? AppDataDir : Path.Combine(best, "SpotlightWinData");
    }

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

    public int MaxResults { get; set; } = 60;

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
        if (s.IndexFolder == null)
        {
            // Chosen once and remembered, so plugging in a bigger drive later doesn't move the index.
            s.IndexFolder = DefaultIndexFolder();
            upgraded = true;
        }
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
        if (SettingsVersion < 3)
        {
            // 30 results hid long courseware that mentions a term only a few times.
            if (MaxResults == 30) MaxResults = 60;
        }
        if (SettingsVersion < 7)
        {
            // 0.7.4: the default is Ctrl+Space; people still on the old default move with it.
            if (Hotkey.Replace(" ", "").Equals("Alt+Space", StringComparison.OrdinalIgnoreCase)) Hotkey = "Ctrl+Space";
        }
        if (SettingsVersion < 6)
        {
            // 0.7.1: AppData and similar system folders are skipped wherever they appear.
            foreach (var name in new Settings().ExcludedDirNames)
                if (!ExcludedDirNames.Contains(name, StringComparer.OrdinalIgnoreCase)) ExcludedDirNames.Add(name);
        }
        if (SettingsVersion is >= 4 and < 5)
        {
            // 0.7.0: spreadsheet text is read again by default (with a per-file budget).
            if (!ContentCategories.Contains(nameof(ContentCategory.Spreadsheet), StringComparer.OrdinalIgnoreCase))
                ContentCategories.Add(nameof(ContentCategory.Spreadsheet));
        }
        if (SettingsVersion < 4)
        {
            // 0.5.0: text only for documents (ContentCategories default), index off the system drive,
            // and the user's program folder read by name only.
            if (OperatingSystem.IsWindows() && Directory.Exists(@"D:\0.SOFT") && !ExcludedContentPaths.Contains(@"D:\0.SOFT", StringComparer.OrdinalIgnoreCase))
                ExcludedContentPaths.Add(@"D:\0.SOFT");
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
        candidates.AddRange(SystemDriveFolders(home));
        candidates.AddRange(DataDrives());
        return Dedupe(candidates.Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p)));
    }

    /// <summary>
    /// The user's own folders on the system drive besides the standard ones: other folders in the
    /// profile (e.g. C:\Users\me\工作) and top-level folders such as C:\资料. People with a single-drive
    /// laptop keep their files there, and they were not searched before 0.7.1. The system's own
    /// folders (Windows, Program Files, ProgramData, AppData, other users…) are left out, and so is the
    /// drive root itself: watching all of C:\ would mean following every write Windows makes.
    /// </summary>
    // Drivers and services that put folders at C:\ (skipped only there: D:\研究\NVIDIA is the user's).
    private static readonly string[] SystemRootJunk = { "inetpub", "Intel", "AMD", "NVIDIA", "XboxGames", "Drivers", "ESD" };

    public IEnumerable<string> SystemDriveFolders(string home)
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<string>();
        var system = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";
        var users = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(home));
        var list = new List<string>();
        void AddChildren(string? dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            try
            {
                foreach (var d in new DirectoryInfo(dir).EnumerateDirectories())
                {
                    if ((d.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0) continue;
                    if (d.Name.StartsWith('.') || d.Name.StartsWith('$') || ExcludedDirNames.Contains(d.Name, StringComparer.OrdinalIgnoreCase)) continue;
                    if (users != null && d.FullName.Equals(users, StringComparison.OrdinalIgnoreCase)) continue;
                    if (dir == system && SystemRootJunk.Contains(d.Name, StringComparer.OrdinalIgnoreCase)) continue;
                    list.Add(d.FullName);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        AddChildren(system);
        AddChildren(home);
        return list;
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
