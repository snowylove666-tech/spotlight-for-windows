using System.ComponentModel;
using Avalonia.Media.Imaging;
using Spotlight.Core;
using Spotlight.Core.Search;

namespace Spotlight.App;

/// <summary>One row in the result list.</summary>
public sealed class ResultItem : INotifyPropertyChanged
{
    public ResultItem(SearchResult r)
    {
        Result = r;
        ShellIcons.Request(r.Path, r.Kind, bmp =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { Icon = bmp; Changed(nameof(Icon)); Changed(nameof(HasIcon)); }));
    }

    public SearchResult Result { get; }
    public string Name => Result.Name;
    public Bitmap? Icon { get; private set; }
    public bool HasIcon => Icon != null;

    /// <summary>Fallback glyph while the shell icon loads (or off Windows).</summary>
    public string Glyph => Result.Kind switch
    {
        FileKind.App => "🚀",
        FileKind.Folder => "📁",
        FileKind.Pdf => "📕",
        FileKind.Document => "📄",
        FileKind.Spreadsheet => "📊",
        FileKind.Presentation => "📽",
        FileKind.Image => "🖼",
        FileKind.Audio => "🎵",
        FileKind.Video => "🎬",
        FileKind.Code => "🧩",
        FileKind.Archive => "🗜",
        FileKind.Calculator => "🧮",
        _ => "📄",
    };

    public string Subtitle
    {
        get
        {
            if (Result.Kind == FileKind.Calculator) return "按 Enter 复制结果";
            if (Result.ContentMatch && Result.Snippet != null) return Partial + Where + Result.Snippet;
            var parent = Path.GetFileName(Path.GetDirectoryName(Result.Path)) ?? "";
            return Partial + $"{FileKinds.DisplayName(Result.Kind)} · {parent}";
        }
    }

    /// <summary>"模糊匹配": "含 2/3 个词 · " when not every word was found.</summary>
    private string Partial =>
        Result.TermsTotal > 1 && Result.TermsMatched > 0 && Result.TermsMatched < Result.TermsTotal
            ? $"含 {Result.TermsMatched}/{Result.TermsTotal} 个词 · " : "";

    /// <summary>"第 496 页等 11 处：" in front of a content snippet.</summary>
    private string Where
    {
        get
        {
            var r = Result;
            string many = r.HitCount > 1 ? $"{(r.Page != null ? "等 " : "")}{r.HitCount} 处" : "";
            string page = r.Page is { } p ? $"第 {p} 页" : "";
            return page.Length + many.Length > 0 ? page + many + "：" : "";
        }
    }

    public string KindText => FileKinds.DisplayName(Result.Kind);

    public string Details
    {
        get
        {
            if (Result.Kind == FileKind.Calculator) return "";
            var parts = new List<string> { FileKinds.DisplayName(Result.Kind) };
            if (Result.Kind != FileKind.Folder && Result.Kind != FileKind.App) parts.Add(FormatSize(Result.Size));
            parts.Add("修改于 " + Result.Modified.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
            if (Result.ContentPending) parts.Add("正文排队中，已优先读取");
            return string.Join("  ·  ", parts);
        }
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:F1} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:F2} GB",
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
