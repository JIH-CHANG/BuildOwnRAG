using ManufacturingAI.Core.Interfaces;
using ManufacturingAI.Core.RAG.Chunking;
using Microsoft.ML.Tokenizers;
using Xunit.Abstractions;

namespace ManufacturingAI.Unit.Tests.Chunking;

public class SemanticChunkerTests
{
    private readonly SemanticChunker _chunker = new();
    private readonly ITestOutputHelper _output;
    private static readonly Tokenizer _tokenizer = TiktokenTokenizer.CreateForModel("gpt-4");

    public SemanticChunkerTests(ITestOutputHelper output) => _output = output;

    // ── Constants ─────────────────────────────────────────────────────────────

    private const string SampleLongText =
        "The production line must be inspected daily. " +
        "All equipment requires calibration before each shift. " +
        "Safety helmets are mandatory in the assembly area. " +
        "Operators must log any abnormalities in the maintenance book. " +
        "Every component should be scanned upon receipt at the warehouse. " +
        "Temperature and humidity levels must stay within specified ranges. " +
        "Defective parts must be quarantined immediately. " +
        "Shift supervisors are responsible for end-of-day quality checks. " +
        "All welding work requires certification from an authorized engineer. " +
        "Emergency stop procedures must be tested monthly.";

    // Short real-world user query — no sentence terminator, fits in one chunk.
    private const string SampleCjkText = "";

    // Multi-sentence CJK document — contains 。 terminators and enough tokens
    // to force splitting at MaxTokens=30, simulating a structured CJK document.
    private const string SampleCjkLongText ="";

    // ── Document builders ─────────────────────────────────────────────────────

    private static ParsedDocument Flat(string text, string title = "test-doc", string sourceType = "folder")
        => new(text, [], new Dictionary<string, string>
        {
            ["title"]      = title,
            ["sourceType"] = sourceType,
        });

    private static ParsedDocument WithSections(params (string Title, string Content, int Page)[] sections)
    {
        var parsedSections = sections
            .Select(s => new ParsedSection(s.Title, s.Content, s.Page))
            .ToList();
        return new ParsedDocument(string.Empty, parsedSections, new Dictionary<string, string>
        {
            ["title"]      = "multi-section-doc",
            ["sourceType"] = "folder",
        });
    }

    // ── Output helpers ────────────────────────────────────────────────────────

    private void PrintScenarioHeader(int index, int total, string name)
    {
        _output.WriteLine("");
        _output.WriteLine($"  ┌── [{index}/{total}] {name}");
        _output.WriteLine($"  │");
    }

    private void PrintPass(string message) =>
        _output.WriteLine($"  │  ✓ {message}");

    private void PrintChunks(string label, IReadOnlyList<TextChunk> chunks)
    {
        const string sep   = "  │  ──────────────────────────────────────────────────";
        const string thick = "  │  ══════════════════════════════════════════════════";

        _output.WriteLine($"  │  CHUNKING REPORT  ▸  {label}");
        _output.WriteLine(thick);

        foreach (var chunk in chunks)
        {
            int    tokens = _tokenizer.CountTokens(chunk.Content);
            string page   = chunk.Metadata.PageNumber.HasValue
                            ? chunk.Metadata.PageNumber.Value.ToString()
                            : "(none)";
            string sec    = string.IsNullOrEmpty(chunk.Metadata.SectionTitle)
                            ? "(none)"
                            : chunk.Metadata.SectionTitle;

            _output.WriteLine(sep);
            _output.WriteLine($"  │  Chunk #      {chunk.ChunkIndex}");
            _output.WriteLine($"  │  Chars        {chunk.Content.Length}");
            _output.WriteLine($"  │  Tokens       {tokens}");
            _output.WriteLine($"  │  Section      {sec}");
            _output.WriteLine($"  │  Page         {page}");
            _output.WriteLine($"  │  SourceTitle  {chunk.Metadata.SourceTitle}");
            _output.WriteLine($"  │  SourceType   {chunk.Metadata.SourceType}");
            _output.WriteLine(sep);
            _output.WriteLine($"  │  {chunk.Content}");
        }

        int    totalChars = chunks.Sum(c => c.Content.Length);
        double avgLen     = chunks.Count > 0 ? (double)totalChars / chunks.Count : 0;

        bool overlapFound = false;
        for (int i = 0; i < chunks.Count - 1 && !overlapFound; i++)
        {
            var prevSentences = chunks[i].Content
                .Split(["。", ". "], StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).Where(s => s.Length > 5);
            overlapFound = prevSentences.Any(s => chunks[i + 1].Content.Contains(s));
        }

        _output.WriteLine(thick);
        _output.WriteLine($"  │  chunks={chunks.Count}  totalChars={totalChars}  avg={avgLen:F1}  overlap={( overlapFound ? "YES" : "NO")}");
        _output.WriteLine(thick);
        _output.WriteLine($"  │");
    }

    private void PrintFinalSummary(IReadOnlyList<(string Name, bool Passed, string? Error)> results)
    {
        const string thick = "  ══════════════════════════════════════════════════════";
        int passed = results.Count(r => r.Passed);

        _output.WriteLine("");
        _output.WriteLine(thick);
        _output.WriteLine($"  FINAL RESULTS  —  {passed}/{results.Count} scenarios passed");
        _output.WriteLine(thick);
        foreach (var (name, ok, err) in results)
        {
            _output.WriteLine($"  {(ok ? "✓" : "✗")}  {name}");
            if (err is not null)
                _output.WriteLine($"       → {err[..Math.Min(120, err.Length)]}");
        }
        _output.WriteLine(thick);
    }

    // ── Private scenarios ─────────────────────────────────────────────────────

    private void Scenario_EmptyAndTrivial()
    {
        var empty = _chunker.Chunk(Flat(string.Empty), new ChunkOptions()).ToList();
        empty.Should().BeEmpty();
        PrintPass("empty text → 0 chunks");

        var ws = _chunker.Chunk(Flat("   \n\n   "), new ChunkOptions()).ToList();
        ws.Should().BeEmpty();
        PrintPass("whitespace-only → 0 chunks");

        const string input = "This is a short document without sentence terminators";
        var single = _chunker.Chunk(Flat(input), new ChunkOptions(MaxTokens: 512)).ToList();
        single.Should().HaveCount(1);
        single[0].Content.Should().Be(input);
        single[0].ChunkIndex.Should().Be(0);
        PrintPass("short text → 1 chunk, content preserved, index=0");
    }

    private void Scenario_LongText()
    {
        var chunks = _chunker.Chunk(Flat(SampleLongText), new ChunkOptions(MaxTokens: 50)).ToList();
        PrintChunks("LongText / MaxTokens=50", chunks);

        chunks.Should().HaveCountGreaterThan(1, "ten sentences at ~10 tokens each exceed MaxTokens=50");
        PrintPass($"produces {chunks.Count} chunks (> 1)");

        chunks.Should().AllSatisfy(c => c.Content.Should().NotBeNullOrWhiteSpace());
        PrintPass("all chunks non-empty");

        for (int i = 0; i < chunks.Count; i++)
            chunks[i].ChunkIndex.Should().Be(i);
        PrintPass("ChunkIndex sequential from 0");

        var allContent = string.Join(" ", chunks.Select(c => c.Content));
        foreach (var s in SampleLongText.Split(". ", StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = s.Trim(' ', '.');
            allContent.Should().Contain(trimmed);
        }
        PrintPass("all original sentences covered across chunks");
    }

    private void Scenario_Overlap()
    {
        const string text =
            "Step one: open the valve. " +
            "Step two: check the gauge. " +
            "Step three: record the pressure. " +
            "Step four: close the intake pipe. " +
            "Step five: start the pump motor. " +
            "Step six: monitor flow rate. " +
            "Step seven: adjust the regulator. " +
            "Step eight: log the final reading.";

        var chunks = _chunker.Chunk(Flat(text), new ChunkOptions(MaxTokens: 50, Overlap: 25)).ToList();
        PrintChunks("Overlap / MaxTokens=50 Overlap=25", chunks);

        if (chunks.Count < 2) { PrintPass("single chunk — overlap vacuously satisfied"); return; }

        var sentences0 = chunks[0].Content
            .Split(". ", StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(s => s.Length > 5).ToList();
        sentences0.Should().Contain(s => chunks[1].Content.Contains(s),
            "at least one sentence from chunk[0] should appear in chunk[1] as overlap");
        PrintPass("sentence from chunk[0] found in chunk[1] — overlap confirmed");
    }

    private void Scenario_Sections()
    {
        var doc1 = WithSections(
            ("Introduction",    "This section introduces the manufacturing process.", 1),
            ("Quality Control", "This section covers quality control procedures.",    2));
        var chunks1 = _chunker.Chunk(doc1, new ChunkOptions()).ToList();
        PrintChunks("Sections / SectionTitle + PageNumber", chunks1);

        chunks1.Should().Contain(c => c.Metadata.SectionTitle == "Introduction");
        chunks1.Should().Contain(c => c.Metadata.SectionTitle == "Quality Control");
        PrintPass("section titles propagated to chunk metadata");

        chunks1.Should().Contain(c => c.Metadata.PageNumber == 1);
        chunks1.Should().Contain(c => c.Metadata.PageNumber == 2);
        PrintPass("page numbers propagated to chunk metadata");

        var doc2   = WithSections(("Sec1", SampleLongText, 1), ("Sec2", SampleLongText, 2));
        var chunks2 = _chunker.Chunk(doc2, new ChunkOptions(MaxTokens: 50)).ToList();
        PrintChunks("Sections / GlobalChunkIndex MaxTokens=50", chunks2);

        for (int i = 0; i < chunks2.Count; i++)
            chunks2[i].ChunkIndex.Should().Be(i, "ChunkIndex must be globally sequential across sections");
        PrintPass($"ChunkIndex globally sequential across 2 sections ({chunks2.Count} total chunks)");
    }

    private void Scenario_Metadata()
    {
        const string docTitle = "ISO-9001-Manual";
        var chunks1 = _chunker.Chunk(
            new ParsedDocument(SampleLongText, [],
                new Dictionary<string, string> { ["title"] = docTitle, ["sourceType"] = "folder" }),
            new ChunkOptions(MaxTokens: 50)).ToList();
        chunks1.Should().AllSatisfy(c => c.Metadata.SourceTitle.Should().Be(docTitle));
        PrintPass($"SourceTitle='{docTitle}' propagated to all {chunks1.Count} chunks");

        var chunks2 = _chunker.Chunk(
            new ParsedDocument(SampleLongText, [],
                new Dictionary<string, string> { ["title"] = "doc", ["sourceType"] = "sharepoint" }),
            new ChunkOptions(MaxTokens: 50)).ToList();
        chunks2.Should().AllSatisfy(c => c.Metadata.SourceType.Should().Be("sharepoint"));
        PrintPass($"SourceType='sharepoint' propagated to all {chunks2.Count} chunks");
    }

    private void Scenario_CJK()
    {
        // Short user query — no terminator, tokens well under MaxTokens → must not split
        var shortChunks = _chunker.Chunk(Flat(SampleCjkText), new ChunkOptions(MaxTokens: 30)).ToList();
        PrintChunks("CJK / Short query (no terminator)", shortChunks);
        shortChunks.Should().HaveCount(1, "single CJK sentence with no terminator must not be split");
        shortChunks[0].Content.Should().Be(SampleCjkText);
        PrintPass("short CJK query → 1 chunk, content preserved");

        // Multi-sentence document — must split on 。
        var longChunks = _chunker.Chunk(Flat(SampleCjkLongText), new ChunkOptions(MaxTokens: 30)).ToList();
        PrintChunks("CJK / Long document (multi-sentence)", longChunks);
        longChunks.Should().HaveCountGreaterThan(1, "CJK multi-sentence with 。 must split at MaxTokens=30");
        PrintPass($"CJK long document → {longChunks.Count} chunks (> 1)");

        var allContent = string.Join("", longChunks.Select(c => c.Content));
        foreach (var sentence in SampleCjkLongText.Split('。', StringSplitOptions.RemoveEmptyEntries).Where(s => s.Length > 0))
            allContent.Should().Contain(sentence);
        PrintPass("all CJK sentences covered in output");
    }

    private void Scenario_OversizedSentence()
    {
        var longSentence = string.Join(" ", Enumerable.Range(1, 200).Select(i => $"word{i}"));
        var chunks = _chunker.Chunk(Flat(longSentence), new ChunkOptions(MaxTokens: 5)).ToList();
        PrintChunks("OversizedSentence / MaxTokens=5", chunks);

        chunks.Should().HaveCount(1, "a single unsplit sentence must not be dropped");
        chunks[0].Content.Should().Be(longSentence);
        PrintPass("200-word sentence with MaxTokens=5 → 1 chunk, content unchanged");
    }

    private void Scenario_Snapshot()
    {
        const string input =
            "Maintenance procedure for hydraulic press unit HY-400.\n\n" +
            "Section 1 — Daily Checks.\n\n" +
            "Inspect the hydraulic fluid level and top up if below the minimum mark. " +
            "Check all hose connections for leaks before powering on. " +
            "Verify that the pressure gauge reads zero before starting the pump. " +
            "Ensure the emergency stop button is functional by pressing and releasing it. " +
            "Clean the work area around the press and remove any debris.\n\n" +
            "Section 2 — Weekly Maintenance.\n\n" +
            "Replace the hydraulic filter element if the pressure differential exceeds 5 bar. " +
            "Lubricate all exposed sliding surfaces with food-grade grease. " +
            "Inspect the ram seal for any signs of wear or extrusion. " +
            "Test the pressure relief valve by briefly exceeding the set-point. " +
            "Record all maintenance activities in the equipment logbook.\n\n" +
            "Section 3 — Fault Codes.\n\n" +
            "E01: Low fluid level — check reservoir and refill. " +
            "E02: Overtemperature — allow cooldown and verify fan operation. " +
            "E03: Pressure fault — inspect relief valve and bypass circuit. " +
            "E04: Sensor error — check wiring harness and replace sensor if necessary. " +
            "E05: Motor overload — reduce duty cycle and inspect motor windings.";

        var doc       = Flat(input, title: "HY-400-Maintenance-Manual", sourceType: "folder");
        var chunks128 = _chunker.Chunk(doc, new ChunkOptions(MaxTokens: 128, Overlap: 0)).ToList();
        var chunks64o = _chunker.Chunk(doc, new ChunkOptions(MaxTokens: 64,  Overlap: 20)).ToList();

        PrintChunks("Snapshot / MaxTokens=128 Overlap=0",  chunks128);
        PrintChunks("Snapshot / MaxTokens=64  Overlap=20", chunks64o);

        chunks128.Should().HaveCountGreaterThanOrEqualTo(1);
        chunks64o.Should().HaveCountGreaterThanOrEqualTo(chunks128.Count,
            "halving MaxTokens should produce at least as many chunks");
        PrintPass($"128-token → {chunks128.Count} chunks,  64-token → {chunks64o.Count} chunks");
    }

    // ── Single public test ────────────────────────────────────────────────────

    [Fact]
    public void SemanticChunker_FullScenarioReport()
    {
        const string thick = "  ══════════════════════════════════════════════════════";
        _output.WriteLine(thick);
        _output.WriteLine($"  SEMANTIC CHUNKER — FULL SCENARIO REPORT");
        _output.WriteLine($"  {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        _output.WriteLine(thick);

        var scenarios = new (string Name, Action Run)[]
        {
            ("Empty / Trivial Input",          Scenario_EmptyAndTrivial),
            ("Long Text Splitting",             Scenario_LongText),
            ("Overlap",                         Scenario_Overlap),
            ("Section-Based Chunking",          Scenario_Sections),
            ("Document Metadata Propagation",   Scenario_Metadata),
            ("CJK — Short & Long Input",        Scenario_CJK),
            ("Oversized Single Sentence",        Scenario_OversizedSentence),
            ("Snapshot — Boundary Review",       Scenario_Snapshot),
        };

        var results = new List<(string Name, bool Passed, string? Error)>();

        for (int i = 0; i < scenarios.Length; i++)
        {
            var (name, run) = scenarios[i];
            PrintScenarioHeader(i + 1, scenarios.Length, name);
            try
            {
                run();
                results.Add((name, true, null));
            }
            catch (Exception ex)
            {
                results.Add((name, false, ex.Message));
                _output.WriteLine($"  │  ✗ FAILED: {ex.Message[..Math.Min(120, ex.Message.Length)]}");
            }
        }

        PrintFinalSummary(results);

        var failed = results.Where(r => !r.Passed).ToList();
        failed.Should().BeEmpty(
            $"{failed.Count} scenario(s) failed: {string.Join(", ", failed.Select(f => f.Name))}");
    }
}
