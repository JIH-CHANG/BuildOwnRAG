using ManufacturingAI.Core.RAG.Retrieval;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace ManufacturingAI.Core.RAG.Reranking;

public class CohereReranker(
    HttpClient httpClient,
    CosineSimilarityReranker fallback,
    ILogger<CohereReranker> logger) : IReranker
{
    private const string Endpoint = "v2/rerank";

    // Chunks scoring below this are noise for the prompt; dropping them keeps
    // context precision up. The top chunk is always kept so the LLM has something
    // to ground on (and the low-confidence warning can still fire downstream).
    private const float MinRelevanceScore = 0.2f;

    public async Task<IEnumerable<RetrievedChunk>> RerankAsync(
        string query,
        IEnumerable<RetrievedChunk> chunks,
        int topK = 5,
        CancellationToken ct = default)
    {
        var chunkList = chunks.ToList();
        if (chunkList.Count == 0) return [];

        try
        {
            var payload = new RerankRequest(
                Model: "rerank-v3.5",
                Query: query,
                Documents: chunkList.Select(c => c.Content).ToList(),
                TopN: topK);

            var response = await httpClient.PostAsJsonAsync(Endpoint, payload, ct);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadFromJsonAsync<RerankResponse>(cancellationToken: ct);
            if (body?.Results is not { Count: > 0 })
                return await fallback.RerankAsync(query, chunkList, topK, ct);

            var ordered = body.Results
                .OrderByDescending(r => r.RelevanceScore)
                .Select(r => chunkList[r.Index] with { RerankScore = r.RelevanceScore })
                .ToList();

            var filtered = ordered.Where(c => c.RerankScore >= MinRelevanceScore).ToList();
            return filtered.Count > 0 ? filtered : ordered.Take(1);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Cohere Rerank API failed — degrading to cosine similarity rerank");
            return await fallback.RerankAsync(query, chunkList, topK, ct);
        }
    }

    private record RerankRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("query")] string Query,
        [property: JsonPropertyName("documents")] List<string> Documents,
        [property: JsonPropertyName("top_n")] int TopN);

    private record RerankResponse(
        [property: JsonPropertyName("results")] List<RerankResult>? Results);

    private record RerankResult(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("relevance_score")] float RelevanceScore);
}
