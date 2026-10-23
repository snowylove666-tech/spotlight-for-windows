using System.Globalization;
using ExcelDataReader;

namespace Spotlight.App;

/// <summary>A window onto one worksheet: the rows around the first cell that matches the query.</summary>
/// <param name="Current">Index into <paramref name="Rows"/> of the matching row asked for (-1 for none).</param>
internal sealed record SheetView(string SheetName, int SheetCount, IReadOnlyList<int> RowNumbers, IReadOnlyList<string[]> Rows, int ColumnCount, int Current = -1);

/// <summary>
/// Reads spreadsheets (xlsx, xlsm, xls, WPS et) for the preview pane without Office: Excel's own
/// previewer lives inside Excel and can keep the preview waiting while Excel is busy.
/// </summary>
internal static class SheetPreview
{
    // ExcelDataReader needs code page 1252. The extractors register it too, but after a restart with
    // nothing left to read none of them has run, and every spreadsheet fell back to plain text.
    static SheetPreview() => System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

    public static bool CanPreview(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".xlsx" or ".xlsm" or ".xls" or ".et";

    private const int MaxColumns = 14, RowsShown = 60, RowsBefore = 4, MaxCellChars = 40;

    /// <summary>The first sheet with a cell containing a term (or the first sheet), from a few rows
    /// above that cell. Row 1 is kept on top as the header.</summary>
    public static SheetView? Read(string path, List<string> terms, CancellationToken ct, int skip = 0)
    {
        int matched = 0;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = ExcelReaderFactory.CreateReader(stream);
        SheetView? first = null;
        var until = DateTime.UtcNow.AddSeconds(4); // huge workbooks: show what was found by then
        do
        {
            var rows = new List<string[]>();
            int hit = -1;
            while (reader.Read() && rows.Count < 20_000 && DateTime.UtcNow < until)
            {
                ct.ThrowIfCancellationRequested();
                var cells = new string[Math.Min(reader.FieldCount, MaxColumns)];
                for (int i = 0; i < cells.Length; i++) cells[i] = Format(reader.GetValue(i));
                rows.Add(cells);
                if (terms.Count > 0 && cells.Any(c => terms.Any(t => c.Contains(t, StringComparison.OrdinalIgnoreCase)))
                    && matched++ >= skip) // Ctrl+↓ asks for the next matching row
                {
                    hit = rows.Count - 1;
                    // Read on just far enough to fill the window.
                    for (int k = 0; k < RowsShown && reader.Read(); k++)
                    {
                        var more = new string[Math.Min(reader.FieldCount, MaxColumns)];
                        for (int i = 0; i < more.Length; i++) more[i] = Format(reader.GetValue(i));
                        rows.Add(more);
                    }
                    break;
                }
                if (terms.Count == 0 && rows.Count >= RowsShown) break;
            }
            if (hit >= 0) return Window(reader.Name, reader.ResultsCount, rows, hit, current: true);
            if (skip == 0) first ??= Window(reader.Name, reader.ResultsCount, rows, 0, current: false);
            if (terms.Count == 0 || DateTime.UtcNow >= until) break;
        } while (reader.NextResult());
        return first;
    }

    private static SheetView Window(string sheet, int sheets, List<string[]> rows, int hit, bool current)
    {
        int from = Math.Max(0, hit - RowsBefore);
        var numbers = Enumerable.Range(from, Math.Min(RowsShown, rows.Count - from)).ToList();
        if (from > 0) numbers.Insert(0, 0); // header row
        var picked = numbers.Select(i => rows[i]).ToList();
        int columns = picked.Count == 0 ? 0 : picked.Max(r => LastFilled(r) + 1);
        return new SheetView(sheet, sheets, numbers.Select(i => i + 1).ToList(),
            picked.Select(r => Enumerable.Range(0, columns).Select(i => i < r.Length ? r[i] : "").ToArray()).ToList(), columns,
            current ? numbers.IndexOf(hit) : -1);
    }

    private static int LastFilled(string[] row)
    {
        for (int i = row.Length - 1; i >= 0; i--) if (row[i].Length > 0) return i;
        return -1;
    }

    private static string Format(object? value)
    {
        var s = value switch
        {
            null => "",
            double d => d.ToString(Math.Abs(d) >= 1e15 || d == Math.Floor(d) ? "0" : "0.####", CultureInfo.InvariantCulture),
            DateTime dt => dt.TimeOfDay == TimeSpan.Zero ? dt.ToString("yyyy-MM-dd") : dt.ToString("yyyy-MM-dd HH:mm"),
            _ => value.ToString() ?? "",
        };
        s = s.Replace('\n', ' ').Replace('\r', ' ');
        return s.Length > MaxCellChars ? s[..MaxCellChars] + "…" : s;
    }

    /// <summary>A, B, …, Z, AA, …</summary>
    public static string ColumnName(int index)
    {
        var name = "";
        for (index++; index > 0; index = (index - 1) / 26) name = (char)('A' + (index - 1) % 26) + name;
        return name;
    }
}
