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
    ChunkMetadata Metadata
);

public interface IHybridRetriever
{
    Task<IEnumerable<RetrievedChunk>> RetrieveAsync(
        RetrieveRequest request,
        CancellationToken ct = default);
}
