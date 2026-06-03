using ClosedXML.Excel;
using ManufacturingAI.Core.Interfaces;
using System.Text;

namespace ManufacturingAI.Core.Parser.Parsers;

public class ExcelParser : IDocumentParser
{
    private const string MimeType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    // BOM column keyword sets (case-insensitive substring match)
    private static readonly string[] PartNumberKeywords = ["料號", "partnumber", "partno", "p/n", "item no", "零件"];
    private static readonly string[] DescriptionKeywords = ["品名", "description", "name", "品目", "名稱"];
    private static readonly string[] SpecKeywords = ["規格", "specification", "spec", "dimension", "尺寸", "size"];

    public bool CanParse(string mimeType) => mimeType == MimeType;

    public Task<ParsedDocument> ParseAsync(Stream stream, string fileName, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        ms.Position = 0;

        using var workbook = new XLWorkbook(ms);
        var sections = new List<ParsedSection>();
        var plainText = new StringBuilder();

        foreach (var sheet in workbook.Worksheets)
        {
            ct.ThrowIfCancellationRequested();
            var sectionText = ParseSheet(sheet, ct);
            if (string.IsNullOrWhiteSpace(sectionText)) continue;

            plainText.AppendLine(sectionText);
            sections.Add(new ParsedSection(sheet.Name, sectionText.Trim(), null));
        }

        return Task.FromResult(new ParsedDocument(
            PlainText: plainText.ToString(),
            Sections: sections,
            Metadata: new Dictionary<string, string>
            {
                ["title"] = Path.GetFileNameWithoutExtension(fileName),
                ["sheetCount"] = workbook.Worksheets.Count.ToString()
            }));
    }

    private static string ParseSheet(IXLWorksheet sheet, CancellationToken ct)
    {
        var usedRange = sheet.RangeUsed();
        if (usedRange is null) return string.Empty;

        int rowCount = usedRange.RowCount();
        int colCount = usedRange.ColumnCount();
        if (rowCount < 1) return string.Empty;

        // Read header row
        var headers = Enumerable.Range(1, colCount)
            .Select(c => sheet.Cell(1, c).GetString().Trim())
            .ToList();

        // Detect BOM columns
        int? partCol = FindColumn(headers, PartNumberKeywords);
        int? descCol = FindColumn(headers, DescriptionKeywords);
        int? specCol = FindColumn(headers, SpecKeywords);
        bool isBom = partCol.HasValue || descCol.HasValue;

        var sb = new StringBuilder();

        // Always include a header line for context
        sb.AppendLine(string.Join(" | ", headers.Where(h => h.Length > 0)));

        for (int row = 2; row <= rowCount; row++)
        {
            ct.ThrowIfCancellationRequested();

            if (isBom)
            {
                // BOM-style: emit structured key-value line
                var parts = new List<string>();
                if (partCol.HasValue)
                {
                    var val = sheet.Cell(row, partCol.Value + 1).GetString().Trim();
                    if (val.Length > 0) parts.Add($"料號: {val}");
                }
                if (descCol.HasValue)
                {
                    var val = sheet.Cell(row, descCol.Value + 1).GetString().Trim();
                    if (val.Length > 0) parts.Add($"品名: {val}");
                }
                if (specCol.HasValue)
                {
                    var val = sheet.Cell(row, specCol.Value + 1).GetString().Trim();
                    if (val.Length > 0) parts.Add($"規格: {val}");
                }
                if (parts.Count > 0) sb.AppendLine(string.Join(" | ", parts));
            }
            else
            {
                // General table: key-value per non-empty column
                var parts = new List<string>();
                for (int col = 1; col <= colCount; col++)
                {
                    var header = col <= headers.Count ? headers[col - 1] : string.Empty;
                    var val = sheet.Cell(row, col).GetString().Trim();
                    if (val.Length > 0)
                        parts.Add(header.Length > 0 ? $"{header}: {val}" : val);
                }
                if (parts.Count > 0) sb.AppendLine(string.Join(" | ", parts));
            }
        }

        return sb.ToString();
    }

    // Returns the 0-based index of the first header that matches any keyword
    private static int? FindColumn(List<string> headers, string[] keywords)
    {
        for (int i = 0; i < headers.Count; i++)
        {
            var h = headers[i].ToLowerInvariant().Replace(" ", "").Replace("_", "");
            if (keywords.Any(k => h.Contains(k, StringComparison.OrdinalIgnoreCase)))
                return i;
        }
        return null;
    }
}
