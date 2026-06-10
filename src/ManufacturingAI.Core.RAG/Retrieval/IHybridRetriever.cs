using ManufacturingAI.Core.Models;

namespace ManufacturingAI.Core.RAG.Retrieval;

public record RetrieveRequest(
    Guid TenantId,
    string Query,
    int TopK = 10,
    Dictionary<string, object>? Filters = null
);

public record RetrievedChunk(
    string ChunkId,
    string Content,
    float VectorScore,
    float BM25Score,
    float FusionScore,
    ChunkMetadata Metadata,
    // Relevance on a 0-1 scale, set by the reranker (cosine similarity or Cohere
    // relevance_score). RRF FusionScore is rank-based (max ~0.033) and must not be
    // used for confidence thresholds.
    float RerankScore = 0f
);

public interface IHybridRetriever
{
    Task<IEnumerable<RetrievedChunk>> RetrieveAsync(
        RetrieveRequest request,
        CancellationToken ct = default);
}
