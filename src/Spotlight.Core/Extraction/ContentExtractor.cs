namespace Spotlight.Core.Extraction;

public interface IContentExtractor
{
    bool CanHandle(string extension);

    /// <summary>Returns the plain text of the file, at most <paramref name="maxChars"/> characters.</summary>
    string Extract(string path, int maxChars, CancellationToken ct);
}

/// <summary>Picks the extractor for a file by extension.</summary>
public sealed class ContentExtractors
{
    private readonly IContentExtractor[] _extractors =
    {
        new PdfExtractor(),
        new OpenXmlExtractor(),
        new XlsExtractor(),
        new HtmlExtractor(),
        new PlainTextExtractor(),
    };

    public static ContentExtractors Default { get; } = new();

    public IContentExtractor? For(string extension) => _extractors.FirstOrDefault(e => e.CanHandle(extension));

    public bool CanExtract(string extension) => For(extension) != null;
}
