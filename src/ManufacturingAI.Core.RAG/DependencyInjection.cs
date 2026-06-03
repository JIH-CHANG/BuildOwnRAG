using ManufacturingAI.Core.RAG.Chunking;
using ManufacturingAI.Core.RAG.Orchestration;
using ManufacturingAI.Core.RAG.Reranking;
using ManufacturingAI.Core.RAG.Retrieval;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ManufacturingAI.Core.RAG;

public static class DependencyInjection
{
    public static IServiceCollection AddCoreRAG(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<IDocumentChunker, SemanticChunker>();
        services.AddScoped<IHybridRetriever, HybridRetriever>();
        services.AddScoped<IMarkdownRetriever, MarkdownRetriever>();
        services.AddScoped<IQueryOrchestrator, QueryOrchestrator>();

        // Free tier: cosine similarity rerank (sorts by VectorScore already returned by Qdrant)
        services.AddSingleton<CosineSimilarityReranker>();

        // Paid tier: Cohere Rerank API (one call for all chunks); falls back to cosine on error
        var cohereApiKey = config["Cohere:ApiKey"] ?? string.Empty;
        services.AddHttpClient<CohereReranker>(client =>
        {
            client.BaseAddress = new Uri("https://api.cohere.com/");
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {cohereApiKey}");
        });

        services.AddScoped<IRerankerFactory, RerankerFactory>();

        return services;
    }
}
