using ManufacturingAI.Core.Interfaces;
using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace ManufacturingAI.Core.Parser.Parsers;

public class PdfParser : IDocumentParser
{
    public bool CanParse(string mimeType) => mimeType == "application/pdf";

    public Task<ParsedDocument> ParseAsync(Stream stream, string fileName, CancellationToken ct = default)
    {
        // PdfPig needs a seekable stream; copy if necessary
        Stream seekable = stream.CanSeek ? stream : CopyToMemory(stream);
        try
        {
            return Task.FromResult(ParseInternal(seekable, fileName, ct));
        }
        finally
        {
            if (!ReferenceEquals(seekable, stream)) seekable.Dispose();
        }
    }

    private static ParsedDocument ParseInternal(Stream stream, string fileName, CancellationToken ct)
    {
        using var pdf = PdfDocument.Open(stream);

        var sections = new List<ParsedSection>();
        var plainText = new StringBuilder();

        // First pass: collect all font sizes to find body-text threshold
        var allFontSizes = new List<double>();
        foreach (var page in pdf.GetPages())
        {
            ct.ThrowIfCancellationRequested();
            foreach (var word in page.GetWords(NearestNeighbourWordExtractor.Instance))
                foreach (var letter in word.Letters)
                    if (letter.FontSize > 0)
                        allFontSizes.Add(letter.FontSize);
        }

        double bodyFontSize = allFontSizes.Count > 0 ? Percentile(allFontSizes, 50) : 10.0;
        double headingThreshold = bodyFontSize * 1.25;

        string currentTitle = Path.GetFileNameWithoutExtension(fileName);
        var currentContent = new StringBuilder();

        foreach (var page in pdf.GetPages())
        {
            ct.ThrowIfCancellationRequested();

            // Group words into lines by rounding Y coordinate to nearest point
            var lines = page
                .GetWords(NearestNeighbourWordExtractor.Instance)
                .GroupBy(w => (int)Math.Round(w.BoundingBox.Top))
                .OrderByDescending(g => g.Key)
                .Select(g => g.OrderBy(w => w.BoundingBox.Left).ToList())
                .ToList();

            foreach (var lineWords in lines)
            {
                if (lineWords.Count == 0) continue;

                var lineText = string.Join(" ", lineWords.Select(w => w.Text));
                if (string.IsNullOrWhiteSpace(lineText)) continue;

                double avgSize = lineWords
                    .SelectMany(w => w.Letters)
                    .Where(l => l.FontSize > 0)
                    .Select(l => l.FontSize)
                    .DefaultIfEmpty(bodyFontSize)
                    .Average();

                bool isHeading = avgSize >= headingThreshold && lineWords.Count <= 15;

                plainText.AppendLine(lineText);

                if (isHeading)
                {
                    if (currentContent.Length > 0)
                    {
                        sections.Add(new ParsedSection(currentTitle, currentContent.ToString().Trim(), page.Number - 1));
                        currentContent.Clear();
                    }
                    currentTitle = lineText;
                }
                else
                {
                    currentContent.AppendLine(lineText);
                }
            }

            // Flush content at end of each page so page numbers are recorded
            if (currentContent.Length > 0)
            {
                sections.Add(new ParsedSection(currentTitle, currentContent.ToString().Trim(), page.Number));
                currentContent.Clear();
                // Next page uses the same heading until a new one is found
            }
        }

        return new ParsedDocument(
            PlainText: plainText.ToString(),
            Sections: sections,
            Metadata: new Dictionary<string, string>
            {
                ["title"] = Path.GetFileNameWithoutExtension(fileName),
                ["pageCount"] = pdf.NumberOfPages.ToString()
            });
    }

    private static double Percentile(List<double> sorted, int p)
    {
        sorted.Sort();
        int idx = (int)Math.Ceiling(sorted.Count * p / 100.0) - 1;
        return sorted[Math.Clamp(idx, 0, sorted.Count - 1)];
    }

    private static MemoryStream CopyToMemory(Stream source)
    {
        var ms = new MemoryStream();
        source.CopyTo(ms);
        ms.Position = 0;
        return ms;
    }
}
