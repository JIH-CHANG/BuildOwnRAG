using HtmlAgilityPack;
using ManufacturingAI.Core.Interfaces;
using System.Text;
using System.Text.RegularExpressions;

namespace ManufacturingAI.Core.Parser.Parsers;

// HTML parser: converts markup to Markdown-style text (h1–h6 → '#' headings, lists, pipe
// tables, fenced code blocks; script/style/comments stripped), then delegates to
// MarkdownParser so the heading/breadcrumb section logic lives in one place.
// Primary consumer is the Confluence connector (page bodies arrive as rendered HTML),
// but any text/html source goes through here.
public class HtmlParser : IDocumentParser
{
    private readonly MarkdownParser markdownParser = new();

    public bool CanParse(string mimeType) =>
        mimeType is "text/html" or "application/xhtml+xml";

    public async Task<ParsedDocument> ParseAsync(Stream stream, string fileName, CancellationToken ct = default)
    {
        var html = new HtmlDocument();
        html.Load(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        var junk = html.DocumentNode.SelectNodes("//script|//style|//comment()");
        if (junk is not null)
            foreach (var node in junk.ToList())
                node.Remove();

        var root = html.DocumentNode.SelectSingleNode("//body") ?? html.DocumentNode;
        var sb = new StringBuilder();
        AppendBlock(root, sb);

        var markdown = Regex.Replace(sb.ToString(), @"\n{3,}", "\n\n").Trim();

        using var mdStream = new MemoryStream(Encoding.UTF8.GetBytes(markdown));
        return await markdownParser.ParseAsync(mdStream, fileName, ct);
    }

    private static void AppendBlock(HtmlNode node, StringBuilder sb)
    {
        foreach (var child in node.ChildNodes)
        {
            switch (child.Name)
            {
                case "#text":
                    AppendText(child, sb);
                    break;

                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    var title = InlineText(child);
                    if (title.Length > 0)
                        sb.Append("\n\n").Append('#', child.Name[1] - '0').Append(' ').Append(title).Append("\n\n");
                    break;

                case "br":
                    sb.Append('\n');
                    break;

                case "p" or "div" or "section" or "article" or "blockquote" or "figure" or "header" or "footer" or "main":
                    sb.Append('\n');
                    AppendBlock(child, sb);
                    sb.Append('\n');
                    break;

                case "ul" or "ol":
                    AppendList(child, sb, depth: 0);
                    break;

                case "table":
                    AppendTable(child, sb);
                    break;

                case "pre":
                    sb.Append("\n```\n")
                      .Append((HtmlEntity.DeEntitize(child.InnerText) ?? string.Empty).Trim('\n'))
                      .Append("\n```\n");
                    break;

                default:
                    // Inline elements (a, strong, em, span, …): formatting dropped, text kept.
                    AppendBlock(child, sb);
                    break;
            }
        }
    }

    private static void AppendText(HtmlNode textNode, StringBuilder sb)
    {
        var text = Regex.Replace(HtmlEntity.DeEntitize(textNode.InnerText) ?? string.Empty, @"\s+", " ");
        if (text.Length == 0)
            return;
        // Whitespace-only nodes still separate adjacent inline elements ("<b>a</b> <i>b</i>").
        if (text == " ")
        {
            if (sb.Length > 0 && !char.IsWhiteSpace(sb[^1]))
                sb.Append(' ');
            return;
        }
        if (text[0] == ' ' && (sb.Length == 0 || char.IsWhiteSpace(sb[^1])))
            text = text[1..];
        sb.Append(text);
    }

    private static void AppendList(HtmlNode list, StringBuilder sb, int depth)
    {
        var ordered = list.Name == "ol";
        var index = 1;
        if (depth == 0) sb.Append('\n');
        foreach (var li in list.ChildNodes.Where(n => n.Name == "li"))
        {
            var nestedLists = li.ChildNodes.Where(n => n.Name is "ul" or "ol").ToList();
            var itemText = InlineText(li, exclude: nestedLists);
            sb.Append(' ', depth * 2)
              .Append(ordered ? $"{index++}. " : "- ")
              .Append(itemText)
              .Append('\n');
            foreach (var nested in nestedLists)
                AppendList(nested, sb, depth + 1);
        }
        if (depth == 0) sb.Append('\n');
    }

    private static void AppendTable(HtmlNode table, StringBuilder sb)
    {
        var rows = table.SelectNodes(".//tr");
        if (rows is null) return;

        sb.Append('\n');
        var first = true;
        foreach (var row in rows)
        {
            var cells = row.ChildNodes
                .Where(n => n.Name is "td" or "th")
                .Select(c => InlineText(c))
                .ToList();
            if (cells.Count == 0) continue;

            sb.Append("| ").Append(string.Join(" | ", cells)).Append(" |\n");
            if (first)
            {
                sb.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", cells.Count))).Append('\n');
                first = false;
            }
        }
        sb.Append('\n');
    }

    /// <summary>Flattened, whitespace-collapsed text of a node, optionally skipping subtrees.</summary>
    private static string InlineText(HtmlNode node, ICollection<HtmlNode>? exclude = null)
    {
        var sb = new StringBuilder();
        void Walk(HtmlNode n)
        {
            if (exclude is not null && exclude.Contains(n)) return;
            if (n.Name == "#text") sb.Append(HtmlEntity.DeEntitize(n.InnerText));
            else if (n.Name == "br") sb.Append(' ');
            else foreach (var c in n.ChildNodes) Walk(c);
        }
        foreach (var c in node.ChildNodes) Walk(c);
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }
}
