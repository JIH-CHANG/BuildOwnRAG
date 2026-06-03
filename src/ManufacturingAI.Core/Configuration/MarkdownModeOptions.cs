namespace ManufacturingAI.Core.Configuration;

// Settings for the Markdown (BM25-only) retrieval mode. Bound from the "MarkdownMode"
// config section and registered as a singleton POCO.
public class MarkdownModeOptions
{
    public int TopK { get; set; } = 5;             // chunks passed to the LLM
    public int PrefilterLimit { get; set; } = 200; // max candidate chunks pulled from Postgres before BM25
}
