using ManufacturingAI.Core.Interfaces;

namespace ManufacturingAI.Core.RAG.Orchestration;

public record QueryRequest(
    Guid TenantId,
    Guid UserId,
    string Question,
    List<LLMMessage>? History = null,
    // Evaluation opt-in: when true, sources carry the full chunk content
    // (SourceReference.FullContent) and the Redis cache is bypassed so the
    // result reflects real retrieval. Defaults to false for normal traffic.
    bool IncludeFullContext = false
);

public record QueryResult(
    string Answer,
    double ConfidenceScore,
    List<SourceReference> Sources,
    bool IsFromCache,
    bool IsFromFallback,
    long LatencyMs
);

public record SourceReference(
    Guid DocumentId,
    string Title,
    string SourceType,
    int? PageNumber,
    string RelevantExcerpt,
    // Full chunk content, populated only when QueryRequest.IncludeFullContext
    // is set (used by the offline Ragas evaluation harness). Null otherwise.
    string? FullContent = null
);

// One element of a streamed query response: either an incremental answer
// token, or the terminal payload (cited sources + queryId + confidence)
// emitted once generation finishes.
public record QueryStreamEvent(
    string? Token = null,
    List<SourceReference>? Sources = null,
    Guid? QueryId = null,
    double? ConfidenceScore = null)
{
    public static QueryStreamEvent OfToken(string token) => new(Token: token);

    public static QueryStreamEvent Completed(
        List<SourceReference> sources, Guid queryId, double confidenceScore)
        => new(Sources: sources, QueryId: queryId, ConfidenceScore: confidenceScore);
}

public interface IQueryOrchestrator
{
    Task<QueryResult> QueryAsync(QueryRequest request, CancellationToken ct = default);
    IAsyncEnumerable<QueryStreamEvent> StreamQueryAsync(QueryRequest request, CancellationToken ct = default);
}
