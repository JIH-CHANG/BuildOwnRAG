using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using ManufacturingAI.Core.Interfaces;
using System.Text;

namespace ManufacturingAI.Core.Parser.Parsers;

public class WordParser : IDocumentParser
{
    private const string MimeType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    public bool CanParse(string mimeType) => mimeType == MimeType;

    public Task<ParsedDocument> ParseAsync(Stream stream, string fileName, CancellationToken ct = default)
    {
        // Copy to MemoryStream — OpenXml needs a seekable stream
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        ms.Position = 0;

        using var doc = WordprocessingDocument.Open(ms, isEditable: false);
        var body = doc.MainDocumentPart?.Document?.Body
            ?? throw new InvalidOperationException("Word document has no body.");

        var sections = new List<ParsedSection>();
        var plainText = new StringBuilder();

        string currentHeading = Path.GetFileNameWithoutExtension(fileName);
        var currentContent = new StringBuilder();
        int headingCount = 0;

        foreach (var para in body.Descendants<Paragraph>())
        {
            ct.ThrowIfCancellationRequested();

            var styleId = para.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? string.Empty;
            var text = ExtractParagraphText(para);
            if (string.IsNullOrWhiteSpace(text)) continue;

            plainText.AppendLine(text);

            bool isHeading = styleId.StartsWith("Heading", StringComparison.OrdinalIgnoreCase)
                          || styleId.StartsWith("1", StringComparison.Ordinal)  // "1" = Heading1 in some locales
                          || (text.Length < 120 && IsLikelyHeading(para));

            if (isHeading)
            {
                // Flush previous section
                if (currentContent.Length > 0)
                {
                    sections.Add(new ParsedSection(currentHeading, currentContent.ToString().Trim(), null));
                    currentContent.Clear();
                }
                currentHeading = text;
                headingCount++;
            }
            else
            {
                currentContent.AppendLine(text);
            }
        }

        // Flush last section
        if (currentContent.Length > 0)
            sections.Add(new ParsedSection(currentHeading, currentContent.ToString().Trim(), null));

        // If no sections were detected, put everything in one section
        if (sections.Count == 0 && plainText.Length > 0)
            sections.Add(new ParsedSection(currentHeading, plainText.ToString().Trim(), null));

        return Task.FromResult(new ParsedDocument(
            PlainText: plainText.ToString(),
            Sections: sections,
            Metadata: new Dictionary<string, string>
            {
                ["title"] = Path.GetFileNameWithoutExtension(fileName),
                ["headingCount"] = headingCount.ToString()
            }));
    }

    private static string ExtractParagraphText(Paragraph para)
    {
        var sb = new StringBuilder();
        foreach (var run in para.Descendants<Run>())
        {
            // Skip deleted text
            if (run.Ancestors<DeletedRun>().Any()) continue;
            foreach (var text in run.Descendants<Text>())
                sb.Append(text.Text);
        }
        return sb.ToString();
    }

    // Heuristic: bold short text is often a heading even without an explicit style
    private static bool IsLikelyHeading(Paragraph para)
    {
        var firstRun = para.Descendants<Run>().FirstOrDefault();
        if (firstRun is null) return false;
        var bold = firstRun.RunProperties?.Bold;
        return bold is not null;
    }
}
