using ManufacturingAI.Core.Parser.Parsers;
using System.Text;

namespace ManufacturingAI.Unit.Tests.Parsing;

public class HtmlParserTests
{
    private readonly HtmlParser _parser = new();

    private async Task<Core.Interfaces.ParsedDocument> ParseAsync(string html, string fileName = "page.html")
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(html));
        return await _parser.ParseAsync(stream, fileName);
    }

    [Theory]
    [InlineData("text/html", true)]
    [InlineData("application/xhtml+xml", true)]
    [InlineData("text/markdown", false)]
    [InlineData("application/pdf", false)]
    public void CanParse_MatchesOnlyHtmlMimeTypes(string mimeType, bool expected) =>
        _parser.CanParse(mimeType).Should().Be(expected);

    [Fact]
    public async Task Headings_BecomeSections_WithBreadcrumbTitles()
    {
        var doc = await ParseAsync("""
            <html><body>
              <h1>Assembly SOP</h1>
              <p>Overview text.</p>
              <h2>Torque Settings</h2>
              <p>Use 12 Nm on all M6 bolts.</p>
            </body></html>
            """);

        doc.Sections.Should().HaveCount(2);
        doc.Sections[0].Title.Should().Be("Assembly SOP");
        doc.Sections[0].Content.Should().Contain("Overview text.");
        doc.Sections[1].Title.Should().Be("Assembly SOP > Torque Settings");
        doc.Sections[1].Content.Should().Contain("12 Nm");
    }

    [Fact]
    public async Task ScriptStyleAndComments_AreStripped()
    {
        var doc = await ParseAsync("""
            <html><head><style>p { color: red; }</style></head><body>
              <script>alert("evil");</script>
              <!-- hidden comment -->
              <p>Visible content.</p>
            </body></html>
            """);

        doc.PlainText.Should().Contain("Visible content.");
        doc.PlainText.Should().NotContain("alert");
        doc.PlainText.Should().NotContain("color: red");
        doc.PlainText.Should().NotContain("hidden comment");
    }

    [Fact]
    public async Task Lists_ConvertToMarkdownBullets_IncludingNesting()
    {
        var doc = await ParseAsync("""
            <ul>
              <li>Check pressure
                <ul><li>Gauge A</li></ul>
              </li>
              <li>Check temperature</li>
            </ul>
            <ol><li>First step</li><li>Second step</li></ol>
            """);

        doc.PlainText.Should().Contain("- Check pressure");
        doc.PlainText.Should().Contain("  - Gauge A");
        doc.PlainText.Should().Contain("- Check temperature");
        doc.PlainText.Should().Contain("1. First step");
        doc.PlainText.Should().Contain("2. Second step");
    }

    [Fact]
    public async Task Tables_ConvertToPipeRows()
    {
        var doc = await ParseAsync("""
            <table>
              <tr><th>Part</th><th>Torque</th></tr>
              <tr><td>M6 bolt</td><td>12 Nm</td></tr>
            </table>
            """);

        doc.PlainText.Should().Contain("| Part | Torque |");
        doc.PlainText.Should().Contain("| M6 bolt | 12 Nm |");
    }

    [Fact]
    public async Task PreBlocks_BecomeFencedCode_AndInnerHashIsNotAHeading()
    {
        var doc = await ParseAsync("<pre># not a heading\nline2</pre>");

        doc.PlainText.Should().Contain("```");
        doc.PlainText.Should().Contain("# not a heading");
        // MarkdownParser must not treat the '#' inside the fence as a section heading.
        doc.Sections.Should().NotContain(s => s.Title == "not a heading");
    }

    [Fact]
    public async Task InlineElements_KeepTextAndWordBoundaries()
    {
        var doc = await ParseAsync("<p><strong>Bold</strong> <em>and italic</em> text with <a href=\"/x\">a link</a>.</p>");

        doc.PlainText.Should().Contain("Bold and italic text with a link.");
    }

    [Fact]
    public async Task HtmlEntities_AreDecoded()
    {
        var doc = await ParseAsync("<p>Temp &gt; 40&deg;C &amp; humidity &lt; 60%</p>");

        doc.PlainText.Should().Contain("Temp > 40°C & humidity < 60%");
    }

    [Fact]
    public async Task EmptyHtml_YieldsEmptyDocumentWithoutThrowing()
    {
        var doc = await ParseAsync("<html><body>   </body></html>");

        doc.PlainText.Should().BeEmpty();
        doc.Sections.Should().BeEmpty();
    }

    [Fact]
    public async Task Metadata_UsesFileNameAsTitle()
    {
        var doc = await ParseAsync("<p>x</p>", "Pump Maintenance.html");

        doc.Metadata["title"].Should().Be("Pump Maintenance");
    }
}
