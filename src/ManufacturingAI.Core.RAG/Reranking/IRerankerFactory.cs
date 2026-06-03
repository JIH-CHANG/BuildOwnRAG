using ManufacturingAI.Core.Models;

namespace ManufacturingAI.Core.RAG.Reranking;

public interface IRerankerFactory
{
    IReranker GetReranker(TenantPlan plan);
}

public class RerankerFactory(
    CosineSimilarityReranker cosineReranker,
    CohereReranker cohereReranker) : IRerankerFactory
{
    public IReranker GetReranker(TenantPlan plan) =>
        plan >= TenantPlan.Professional ? cohereReranker : cosineReranker;
}
