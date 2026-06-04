using ManufacturingAI.Core.Interfaces;
using System.Text;
using System.Text.RegularExpressions;

namespace ManufacturingAI.Core.Parser.Parsers;

// Markdown-aware parser: splits on ATX headings (#, ##, …) so each section maps to a
// heading and its body, with parent headings kept as a breadcrumb ("Parent > Child")
// for context. Fenced code blocks are preserved verbatim — a '#' inside ``` is not a heading.
public class MarkdownParser : IDocumentParser
{
    // ATX heading: up to 3 leading spaces, 1–6 '#', whitespace, text, optional closing '#'s.
    private static readonly Regex HeadingRegex = new(
        @"^[ ]{0,3}(#{1,6})[ \t]+(.+?)[ \t]*#*[ \t]*$", RegexOptions.Compiled);

    public bool CanParse(string mimeType) =>
        mimeType is "text/markdown" or "text/x-markdown";

    public async Task<ParsedDocument> ParseAsync(Stream stream, string fileName, CancellationToken ct = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var fullText = await reader.ReadToEndAsync(ct);

        var sections = SplitIntoSections(fullText);

        return new ParsedDocument(
            PlainText: fullText,
            Sections: sections,
            Metadata: new Dictionary<string, string>
            {
                ["title"] = Path.GetFileNameWithoutExtension(fileName),
                ["sectionCount"] = sections.Count.ToString()
            });
    }

    private static List<ParsedSection> SplitIntoSections(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var sections = new List<ParsedSection>();

        // headingStack[level-1] = the current ancestor heading title at that depth.
        var headingStack = new string?[6];
        string? currentTitle = null;
        var body = new StringBuilder();
        bool inFence = false;
        string? fenceMarker = null;

        void Flush()
        {
            var content = body.ToString().Trim();
            body.Clear();
            // Drop empty sections (e.g. a parent heading with no body of its own);
            // its title still lives on in the breadcrumb of child sections.
            if (content.Length == 0) return;
            sections.Add(new ParsedSection(currentTitle ?? "Introduction", content, null));
        }

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();

            // Toggle fenced code blocks so headings inside them are ignored.
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                var marker = trimmed[..3];
                if (!inFence) { inFence = true; fenceMarker = marker; }
                else if (fenceMarker == marker) { inFence = false; fenceMarker = null; }
                body.AppendLine(line);
                continue;
            }

            var heading = inFence ? Match.Empty : HeadingRegex.Match(line);
            if (heading.Success)
            {
                Flush(); // a new heading closes the previous section

                int level = heading.Groups[1].Value.Length;
                string title = heading.Groups[2].Value.Trim();

                headingStack[level - 1] = title;
                for (int i = level; i < headingStack.Length; i++) headingStack[i] = null;

                currentTitle = string.Join(
                    " > ", headingStack.Take(level).Where(h => !string.IsNullOrEmpty(h)));
            }
            else
            {
                body.AppendLine(line);
            }
        }

        Flush();
        return sections;
    }
}
