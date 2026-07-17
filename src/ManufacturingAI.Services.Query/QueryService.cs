using ManufacturingAI.Core.Common;
using ManufacturingAI.Core.Models;
using ManufacturingAI.Core.RAG.Memory;
using ManufacturingAI.Core.RAG.Orchestration;
using ManufacturingAI.Infrastructure.Caching;
using ManufacturingAI.Infrastructure.Repositories;

namespace ManufacturingAI.Services.Query;

public interface IQueryService
{
    Task<Result<QueryResult>> QueryAsync(QueryRequest request, CancellationToken ct = default);
    IAsyncEnumerable<QueryStreamEvent> StreamQueryAsync(QueryRequest request, CancellationToken ct = default);
    Task<Result> UpdateFeedbackAsync(Guid queryLogId, QueryFeedback feedback, CancellationToken ct = default);
}

public class QueryService(
    IQueryOrchestrator orchestrator,
    IQueryLogRepository queryLogRepository,
    IQaMemoryService qaMemory,
    ICacheService cache) : IQueryService
{
    public async Task<Result<QueryResult>> QueryAsync(QueryRequest request, CancellationToken ct = default)
    {
        try
        {
            var result = await orchestrator.QueryAsync(request, ct);
            return Result<QueryResult>.Ok(result);
        }
        catch (Exception ex)
        {
            return Result<QueryResult>.Fail(ex.Message);
        }
    }

    public IAsyncEnumerable<QueryStreamEvent> StreamQueryAsync(QueryRequest request, CancellationToken ct = default)
        => orchestrator.StreamQueryAsync(request, ct);

    public async Task<Result> UpdateFeedbackAsync(Guid queryLogId, QueryFeedback feedback, CancellationToken ct = default)
    {
        var result = await queryLogRepository.UpdateFeedbackAsync(queryLogId, feedback, ct);
        if (!result.Success) return result;

        // Feed the vote into QA memory so the entry's accuracy tracks user
        // feedback, and drop the cached result for a downvoted question so
        // re-asking regenerates instead of replaying the rejected answer.
        var log = await queryLogRepository.GetByIdAsync(queryLogId, ct);
        if (log is not null)
        {
            await qaMemory.ApplyFeedbackAsync(log.TenantId, log.Question, log.Answer, feedback, ct);
            if (feedback == QueryFeedback.Negative)
            {
                await cache.RemoveAsync(
                    CacheKeys.QueryResult(log.TenantId, QueryHashing.Compute(log.Question)), ct);
            }
        }

        return result;
    }
}
