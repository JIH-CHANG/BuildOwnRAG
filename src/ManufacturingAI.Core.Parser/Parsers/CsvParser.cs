using ManufacturingAI.Core.Interfaces;
using System.Text;

namespace ManufacturingAI.Core.Parser.Parsers;

public class CsvParser : IDocumentParser
{
    public bool CanParse(string mimeType) => mimeType is "text/csv" or "application/csv";

    public async Task<ParsedDocument> ParseAsync(Stream stream, string fileName, CancellationToken ct = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var lines = new List<string>();
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
            if (!string.IsNullOrWhiteSpace(line))
                lines.Add(line);

        if (lines.Count == 0)
            return new ParsedDocument(string.Empty, [], []);

        var headers = ParseCsvRow(lines[0]);
        var sections = new List<ParsedSection>();
        var plainText = new StringBuilder();

        // Header row as context
        plainText.AppendLine(string.Join(" | ", headers));

        for (int i = 1; i < lines.Count; i++)
        {
            var values = ParseCsvRow(lines[i]);
            var parts = new List<string>();
            for (int j = 0; j < Math.Min(headers.Count, values.Count); j++)
                if (!string.IsNullOrWhiteSpace(values[j]))
                    parts.Add($"{headers[j]}: {values[j].Trim()}");

            if (parts.Count == 0) continue;

            var rowText = string.Join(" | ", parts);
            plainText.AppendLine(rowText);
            sections.Add(new ParsedSection($"Row {i}", rowText, null));
        }

        return new ParsedDocument(
            PlainText: plainText.ToString(),
            Sections: sections,
            Metadata: new Dictionary<string, string>
            {
                ["title"] = Path.GetFileNameWithoutExtension(fileName),
                ["rowCount"] = (lines.Count - 1).ToString(),
                ["columns"] = string.Join(",", headers)
            });
    }

    // RFC 4180 minimal CSV field parser
    private static List<string> ParseCsvRow(string line)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"') inQuotes = false;
                else field.Append(c);
            }
            else
            {
                if (c == '"') inQuotes = true;
                else if (c == ',') { fields.Add(field.ToString()); field.Clear(); }
                else field.Append(c);
            }
        }
        fields.Add(field.ToString());
        return fields;
    }
}
