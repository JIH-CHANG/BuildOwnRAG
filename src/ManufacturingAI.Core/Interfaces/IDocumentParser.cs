namespace ManufacturingAI.Core.Interfaces;

public record ParsedSection(string Title, string Content, int? PageNumber);

public record ParsedDocument(
    string PlainText,
    List<ParsedSection> Sections,
    Dictionary<string, string> Metadata
);

public interface IDocumentParser
{
    bool CanParse(string mimeType);
    Task<ParsedDocument> ParseAsync(Stream stream, string fileName, CancellationToken ct = default);
}
