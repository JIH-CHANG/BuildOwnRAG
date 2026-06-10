using ManufacturingAI.Core.RAG.Retrieval;

namespace ManufacturingAI.Core.RAG.Reranking;

// Zero-API-call reranker for the Free plan. Selection keeps the hybrid RRF order
// (FusionScore) — re-sorting by vector score alone would throw away the BM25 signal
// the retriever just fused in. VectorScore (Qdrant cosine, 0-1) is exposed as
// RerankScore so downstream confidence checks have an absolute relevance measure.
public class CosineSimilarityReranker : IReranker
{
    public Task<IEnumerable<RetrievedChunk>> RerankAsync(
        string query,
        IEnumerable<RetrievedChunk> chunks,
        int topK = 5,
        CancellationToken ct = default)
    {
        var result = chunks
            .OrderByDescending(c => c.FusionScore)
            .Take(topK)
            .Select(c => c with { RerankScore = c.VectorScore });

        return Task.FromResult(result);
    }
}
