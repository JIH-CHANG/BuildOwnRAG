using System.Runtime.CompilerServices;
using ManufacturingAI.Core.Common;
using ManufacturingAI.Core.Models;
using ManufacturingAI.Core.RAG.Orchestration;
using ManufacturingAI.Infrastructure.Repositories;

namespace ManufacturingAI.Services.Query;

// Routes each query to the tenant's configured retrieval pipeline:
// Hybrid (QueryService) or Markdown/BM25-only (MarkdownQueryService).
public class QueryRouter(
    QueryService hybrid,
    IMarkdownQueryService markdown,
    IRepository<Tenant> tenantRepository) : IQueryService
{
    public async Task<Result<QueryResult>> QueryAsync(QueryRequest request, CancellationToken ct = default)
        => await IsMarkdownAsync(request.TenantId, ct)
            ? await markdown.QueryAsync(request, ct)
            : await hybrid.QueryAsync(request, ct);

    public async IAsyncEnumerable<QueryStreamEvent> StreamQueryAsync(
        QueryRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var stream = await IsMarkdownAsync(request.TenantId, ct)
            ? markdown.StreamQueryAsync(request, ct)
            : hybrid.StreamQueryAsync(request, ct);

        await foreach (var evt in stream.WithCancellation(ct))
            yield return evt;
    }

    // Feedback applies to logged (Hybrid) queries only.
    public Task<Result> UpdateFeedbackAsync(Guid queryLogId, QueryFeedback feedback, CancellationToken ct = default)
        => hybrid.UpdateFeedbackAsync(queryLogId, feedback, ct);

    private async Task<bool> IsMarkdownAsync(Guid tenantId, CancellationToken ct)
    {
        var tenant = await tenantRepository.GetByIdAsync(tenantId, ct);
        return tenant?.Settings.RetrievalMode == RetrievalMode.Markdown;
    }
}
