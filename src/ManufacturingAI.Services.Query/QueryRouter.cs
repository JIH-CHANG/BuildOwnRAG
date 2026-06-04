using System.Runtime.CompilerServices;
using ManufacturingAI.Core.Common;
using ManufacturingAI.Core.Models;
using ManufacturingAI.Core.RAG.Orchestration;
using ManufacturingAI.Infrastructure.Repositories;

namespace ManufacturingAI.Services.Query;

// Routes each query to the tenant's configured retrieval pipeline:
// Hybrid (QueryService) or Lite/BM25-only (LiteQueryService).
public class QueryRouter(
    QueryService hybrid,
    ILiteQueryService lite,
    IRepository<Tenant> tenantRepository) : IQueryService
{
    public async Task<Result<QueryResult>> QueryAsync(QueryRequest request, CancellationToken ct = default)
        => await IsLiteAsync(request.TenantId, ct)
            ? await lite.QueryAsync(request, ct)
            : await hybrid.QueryAsync(request, ct);

    public async IAsyncEnumerable<QueryStreamEvent> StreamQueryAsync(
        QueryRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var stream = await IsLiteAsync(request.TenantId, ct)
            ? lite.StreamQueryAsync(request, ct)
            : hybrid.StreamQueryAsync(request, ct);

        await foreach (var evt in stream.WithCancellation(ct))
            yield return evt;
    }

    // Feedback updates a QueryLog by id; both Hybrid and Lite modes persist logs.
    public Task<Result> UpdateFeedbackAsync(Guid queryLogId, QueryFeedback feedback, CancellationToken ct = default)
        => hybrid.UpdateFeedbackAsync(queryLogId, feedback, ct);

    private async Task<bool> IsLiteAsync(Guid tenantId, CancellationToken ct)
    {
        var tenant = await tenantRepository.GetByIdAsync(tenantId, ct);
        return tenant?.Settings.RetrievalMode == RetrievalMode.Lite;
    }
}
