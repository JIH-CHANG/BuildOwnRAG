using System.Diagnostics.Metrics;

namespace ManufacturingAI.Core.Observability;

/// <summary>
/// RAG business metrics, exported through OpenTelemetry — Program.cs registers
/// the meter by name. Static so any layer can record without DI plumbing.
/// Tags stay low-cardinality (mode / source_type / provider / outcome); no
/// tenant IDs here — per-tenant views live in the analytics dashboard.
/// </summary>
public static class AppMetrics
{
    public const string MeterName = "BuildOwnRAG";

    private static readonly Meter Meter = new(MeterName);

    // ── Query pipeline ────────────────────────────────────────────
    // tags: mode = hybrid|lite, outcome = ok|cached|error, low_confidence = true|false
    public static readonly Counter<long> Queries =
        Meter.CreateCounter<long>("rag.queries", unit: "{query}",
            description: "RAG queries answered.");

    // tags: mode — recorded for cache misses only
    public static readonly Histogram<double> QueryDuration =
        Meter.CreateHistogram<double>("rag.query.duration", unit: "ms",
            description: "End-to-end query latency, excluding cache hits.");

    // tags: mode
    public static readonly Histogram<double> QueryConfidence =
        Meter.CreateHistogram<double>("rag.query.confidence", unit: "1",
            description: "Confidence score (0-1) of answered queries.");

    // ── Ingestion ─────────────────────────────────────────────────
    // tags: source_type (upload|folder|googledrive|...), outcome = indexed|skipped|failed|deferred|deleted
    public static readonly Counter<long> DocumentsIngested =
        Meter.CreateCounter<long>("rag.ingest.documents", unit: "{document}",
            description: "Documents processed by the ingestion pipeline.");

    // tags: source_type — recorded for successfully indexed documents
    public static readonly Histogram<double> IngestDuration =
        Meter.CreateHistogram<double>("rag.ingest.duration", unit: "ms",
            description: "Time to parse, chunk, embed and index one document.");

    public static readonly Histogram<long> ChunksPerDocument =
        Meter.CreateHistogram<long>("rag.ingest.chunks", unit: "{chunk}",
            description: "Chunks produced per ingested document.");

    // ── Provider calls ────────────────────────────────────────────
    // tags: kind = llm|embedding, provider (openai|gemini|...),
    //       operation = complete|stream|embed|embed_batch, outcome = ok|error
    public static readonly Counter<long> ProviderCalls =
        Meter.CreateCounter<long>("rag.provider.calls", unit: "{call}",
            description: "LLM and embedding provider calls.");
}
