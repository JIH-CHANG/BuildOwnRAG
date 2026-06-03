using ManufacturingAI.Core.RAG.Retrieval;

namespace ManufacturingAI.Core.RAG.Reranking;

public interface IReranker
{
    Task<IEnumerable<RetrievedChunk>> RerankAsync(
        string query,
        IEnumerable<RetrievedChunk> chunks,
        int topK = 5,
        CancellationToken ct = default);
}
