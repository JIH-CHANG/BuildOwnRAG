using ManufacturingAI.Core.Interfaces;
using ManufacturingAI.Core.Parser.Parsers;
using Microsoft.Extensions.DependencyInjection;

namespace ManufacturingAI.Core.Parser;

public static class DependencyInjection
{
    public static IServiceCollection AddCoreParsers(this IServiceCollection services)
    {
        // Register each parser as IDocumentParser so they are injected as IEnumerable<IDocumentParser>
        services.AddSingleton<IDocumentParser, TxtParser>();
        services.AddSingleton<IDocumentParser, MarkdownParser>();
        services.AddSingleton<IDocumentParser, HtmlParser>();
        services.AddSingleton<IDocumentParser, CsvParser>();
        services.AddSingleton<IDocumentParser, WordParser>();
        services.AddSingleton<IDocumentParser, ExcelParser>();
        services.AddSingleton<IDocumentParser, PdfParser>();

        services.AddSingleton<IParserFactory, ParserFactory>();

        return services;
    }
}
