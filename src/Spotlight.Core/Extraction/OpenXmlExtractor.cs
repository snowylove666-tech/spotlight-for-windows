using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Spotlight.Core.Extraction;

/// <summary>Reads the text out of .docx/.xlsx/.pptx (and their OpenDocument cousins) by walking
/// the XML parts inside the zip. No Office installation is required.</summary>
public sealed class OpenXmlExtractor : IContentExtractor
{
    public bool CanHandle(string extension) => extension.ToLowerInvariant() is ".docx" or ".docm" or ".xlsx" or ".xlsm" or ".pptx" or ".odt" or ".ods" or ".odp";

    public string Extract(string path, int maxChars, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        IEnumerable<ZipArchiveEntry> parts = ext switch
        {
            ".docx" or ".docm" => zip.Entries.Where(e => e.FullName is "word/document.xml"
                || e.FullName.StartsWith("word/header") || e.FullName.StartsWith("word/footer")
                || e.FullName is "word/footnotes.xml" or "word/endnotes.xml"),
            ".xlsx" or ".xlsm" => zip.Entries.Where(e => e.FullName is "xl/sharedStrings.xml"
                || (e.FullName.StartsWith("xl/worksheets/sheet") && e.FullName.EndsWith(".xml"))),
            ".pptx" => zip.Entries.Where(e => (e.FullName.StartsWith("ppt/slides/slide") || e.FullName.StartsWith("ppt/notesSlides/"))
                && e.FullName.EndsWith(".xml")).OrderBy(e => e.FullName.Length).ThenBy(e => e.FullName),
            _ => zip.Entries.Where(e => e.FullName == "content.xml"),
        };

        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            ct.ThrowIfCancellationRequested();
            using var stream = part.Open();
            AppendXmlText(stream, sb, maxChars);
            if (sb.Length >= maxChars) return sb.ToString(0, maxChars);
        }
        return sb.ToString();
    }

    // Text lives in <w:t>, <a:t>, <t> (xlsx), <v> (xlsx numbers) and <text:p>/<text:span> (ODF).
    // Paragraph-like ends become newlines so words from different paragraphs don't merge.
    private static void AppendXmlText(Stream stream, StringBuilder sb, int maxChars)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreComments = true });
        bool odf = false;
        reader.Read();
        while (!reader.EOF && sb.Length < maxChars)
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.Prefix == "text") odf = true;
                var name = reader.LocalName;
                if ((name is "t" or "v") && !reader.IsEmptyElement && !odf)
                {
                    // ReadElementContentAsString already moves past the element, so don't Read() again.
                    sb.Append(reader.ReadElementContentAsString());
                    if (name == "v") sb.Append(' ');
                    continue;
                }
                if (name is "tab" or "br" or "s") sb.Append(' ');
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (reader.LocalName is "p" or "si" or "c" or "h") sb.Append('\n');
            }
            else if (odf && reader.NodeType is XmlNodeType.Text or XmlNodeType.SignificantWhitespace)
            {
                sb.Append(reader.Value);
            }
            reader.Read();
        }
    }
}
