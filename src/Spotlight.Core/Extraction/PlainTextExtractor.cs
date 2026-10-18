namespace Spotlight.Core.Extraction;

public sealed class PlainTextExtractor : IContentExtractor
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".log", ".csv", ".tsv", ".ini", ".cfg", ".conf", ".rst", ".tex", ".json", ".xml",
        ".yaml", ".yml", ".toml", ".cs", ".c", ".cpp", ".h", ".hpp", ".java", ".kt", ".py", ".js", ".ts", ".tsx", ".jsx",
        ".go", ".rs", ".rb", ".php", ".swift", ".m", ".sql", ".sh", ".ps1", ".bat", ".cmd", ".css", ".scss", ".vue",
        ".lua", ".r", ".dart", ".scala", ".srt", ".ass", ".vtt", ".properties", ".gradle",
    };

    public bool CanHandle(string extension) => Extensions.Contains(extension);

    public string Extract(string path, int maxChars, CancellationToken ct)
    {
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            Span<byte> head = stackalloc byte[4096];
            int n = fs.Read(head);
            if (TextDecoder.LooksBinary(head[..n])) return "";
        }
        return TextDecoder.ReadFile(path, maxChars);
    }
}
