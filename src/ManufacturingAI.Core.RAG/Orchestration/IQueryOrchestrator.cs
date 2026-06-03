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

public interface IQueryOrchestrator
{
    Task<QueryResult> QueryAsync(QueryRequest request, CancellationToken ct = default);
    IAsyncEnumerable<string> StreamQueryAsync(QueryRequest request, CancellationToken ct = default);
}
