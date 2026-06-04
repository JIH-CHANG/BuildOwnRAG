using ManufacturingAI.Core.Interfaces;

namespace ManufacturingAI.Core.RAG.Orchestration;

public record QueryRequest(
    Guid TenantId,
    Guid UserId,
    string Question,
    List<LLMMessage>? History = null
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
    string RelevantExcerpt
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
