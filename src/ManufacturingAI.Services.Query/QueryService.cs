using ManufacturingAI.Core.Common;
using ManufacturingAI.Core.Models;
using ManufacturingAI.Core.RAG.Orchestration;
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
    IQueryLogRepository queryLogRepository) : IQueryService
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
        => await queryLogRepository.UpdateFeedbackAsync(queryLogId, feedback, ct);
}
