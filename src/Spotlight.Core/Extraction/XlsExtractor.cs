using System.Text;
using ExcelDataReader;

namespace Spotlight.Core.Extraction;

/// <summary>Reads the cell text of old binary workbooks: Excel 97–2003 .xls and WPS .et (same
/// format). Sheet names start each sheet; cells are separated by spaces, rows by newlines.</summary>
public sealed class XlsExtractor : IContentExtractor
{
    static XlsExtractor() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public bool CanHandle(string extension) => extension.ToLowerInvariant() is ".xls" or ".et";

    public string Extract(string path, int maxChars, CancellationToken ct)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = ExcelReaderFactory.CreateReader(stream);
        var sb = new StringBuilder();
        do
        {
            sb.Append(reader.Name).Append('\n');
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    var value = reader.GetValue(i);
                    if (value == null) continue;
                    sb.Append(value is double d ? d.ToString(System.Globalization.CultureInfo.InvariantCulture) : value.ToString()).Append(' ');
                }
                sb.Append('\n');
                if (sb.Length >= maxChars) return sb.ToString(0, maxChars);
            }
        } while (reader.NextResult());
        return sb.ToString();
    }
}
