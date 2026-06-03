using ManufacturingAI.Core.Interfaces;
using System.Text;

namespace ManufacturingAI.Core.Parser.Parsers;

public class TxtParser : IDocumentParser
{
    public bool CanParse(string mimeType) =>
        mimeType is "text/plain" or "text/txt" or "text/markdown" or "text/x-markdown";

    public async Task<ParsedDocument> ParseAsync(Stream stream, string fileName, CancellationToken ct = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var fullText = await reader.ReadToEndAsync(ct);

        // Split on blank lines → paragraphs → each becomes a section
        var paragraphs = fullText
            .Split(["\r\n\r\n", "\n\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        var sections = new List<ParsedSection>();
        for (int i = 0; i < paragraphs.Count; i++)
            sections.Add(new ParsedSection($"Paragraph {i + 1}", paragraphs[i], null));

        return new ParsedDocument(
            PlainText: fullText,
            Sections: sections,
            Metadata: new Dictionary<string, string>
            {
                ["title"] = Path.GetFileNameWithoutExtension(fileName),
                ["paragraphCount"] = paragraphs.Count.ToString()
            });
    }
}
