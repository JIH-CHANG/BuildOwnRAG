using ManufacturingAI.Core.RAG.Retrieval;

namespace ManufacturingAI.Core.RAG.Reranking;

// VectorScore returned by Qdrant is already cosine similarity between the query embedding
// and each chunk embedding, so re-sorting by it is a true cosine similarity rerank with
// zero additional API calls.
public class CosineSimilarityReranker : IReranker
{
    public Task<IEnumerable<RetrievedChunk>> RerankAsync(
        string query,
        IEnumerable<RetrievedChunk> chunks,
        int topK = 5,
        CancellationToken ct = default)
    {
        var result = chunks
            .OrderByDescending(c => c.VectorScore)
            .Take(topK);

        return Task.FromResult(result);
    }
}
