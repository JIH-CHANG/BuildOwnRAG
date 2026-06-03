using ManufacturingAI.Core.Common;
using ManufacturingAI.Core.Interfaces;

namespace ManufacturingAI.Core.Parser;

public interface IParserFactory
{
    Task<Result<ParsedDocument>> ParseAsync(
        string mimeType, Stream stream, string fileName, CancellationToken ct = default);
}

public class ParserFactory(IEnumerable<IDocumentParser> parsers) : IParserFactory
{
    public async Task<Result<ParsedDocument>> ParseAsync(
        string mimeType, Stream stream, string fileName, CancellationToken ct = default)
    {
        var parser = parsers.FirstOrDefault(p => p.CanParse(mimeType));
        if (parser is null)
            return Result<ParsedDocument>.Fail($"No parser registered for MIME type '{mimeType}'.");

        try
        {
            var doc = await parser.ParseAsync(stream, fileName, ct);
            return Result<ParsedDocument>.Ok(doc);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result<ParsedDocument>.Fail($"Parse error ({mimeType}): {ex.Message}");
        }
    }
}
