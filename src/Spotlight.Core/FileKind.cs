namespace Spotlight.Core;

public enum FileKind
{
    Other = 0,
    App = 1,
    Folder = 2,
    Document = 3,
    Pdf = 4,
    Spreadsheet = 5,
    Presentation = 6,
    Image = 7,
    Audio = 8,
    Video = 9,
    Code = 10,
    Archive = 11,
    Text = 12,
    Calculator = 100,
}

public static class FileKinds
{
    private static readonly Dictionary<string, FileKind> ByExt = new(StringComparer.OrdinalIgnoreCase);

    static FileKinds()
    {
        void Add(FileKind k, params string[] exts) { foreach (var e in exts) ByExt["." + e] = k; }
        Add(FileKind.App, "lnk", "url", "appref-ms", "exe");
        Add(FileKind.Pdf, "pdf");
        Add(FileKind.Document, "doc", "docx", "odt", "rtf", "pages", "wps");
        Add(FileKind.Spreadsheet, "xls", "xlsx", "xlsm", "ods", "csv", "et");
        Add(FileKind.Presentation, "ppt", "pptx", "odp", "key", "dps");
        Add(FileKind.Image, "png", "jpg", "jpeg", "gif", "bmp", "webp", "heic", "tif", "tiff", "svg", "ico", "psd");
        Add(FileKind.Audio, "mp3", "wav", "flac", "aac", "m4a", "ogg", "wma");
        Add(FileKind.Video, "mp4", "mkv", "mov", "avi", "wmv", "webm", "flv");
        Add(FileKind.Archive, "zip", "rar", "7z", "tar", "gz", "bz2", "xz", "iso");
        Add(FileKind.Text, "txt", "md", "markdown", "log", "ini", "cfg", "conf", "rst", "tex");
        Add(FileKind.Code, "cs", "c", "cpp", "h", "hpp", "java", "kt", "py", "js", "ts", "tsx", "jsx", "go", "rs", "rb",
            "php", "swift", "m", "sql", "sh", "ps1", "bat", "cmd", "json", "xml", "yaml", "yml", "toml", "html", "htm",
            "css", "scss", "vue", "lua", "r", "dart", "scala", "csproj", "sln", "gradle");
    }

    public static FileKind FromExtension(string ext) => ByExt.TryGetValue(ext, out var k) ? k : FileKind.Other;

    public static string DisplayName(FileKind k) => k switch
    {
        FileKind.App => "应用",
        FileKind.Folder => "文件夹",
        FileKind.Document => "文稿",
        FileKind.Pdf => "PDF",
        FileKind.Spreadsheet => "表格",
        FileKind.Presentation => "演示文稿",
        FileKind.Image => "图片",
        FileKind.Audio => "音频",
        FileKind.Video => "视频",
        FileKind.Code => "代码",
        FileKind.Archive => "压缩包",
        FileKind.Text => "文本",
        FileKind.Calculator => "计算器",
        _ => "文件",
    };
}
