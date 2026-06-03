using ManufacturingAI.API.Extensions;
using ManufacturingAI.Core.Common;
using ManufacturingAI.Core.Models;
using ManufacturingAI.Core.RAG.Orchestration;
using ManufacturingAI.Services.Query;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace ManufacturingAI.API.Controllers;

public record FeedbackRequest(QueryFeedback Feedback);

[ApiController]
[Route("api/v1/query")]
[Authorize(Policy = "CanQuery")]
public class QueryController(IQueryService queryService) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<ApiResponse<QueryResult>>> Query(
        [FromBody] QueryRequest request,
        CancellationToken ct)
    {
        var tenantId = User.GetTenantId();
        var userId = User.GetUserId();
        var req = request with { TenantId = tenantId, UserId = userId };

        var result = await queryService.QueryAsync(req, ct);
        return result.Success
            ? Ok(this.ApiOk(result.Value))
            : BadRequest(this.ApiFail(result.Error!));
    }

    // SSE endpoint — yields LLM tokens as text/event-stream.
    // Frontend reads with: fetch(..., { method: 'POST' }) + ReadableStream
    [HttpPost("stream")]
    public async Task StreamQuery([FromBody] QueryRequest request, CancellationToken ct)
    {
        var tenantId = User.GetTenantId();
        var userId = User.GetUserId();
        var req = request with { TenantId = tenantId, UserId = userId };

        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Append("X-Accel-Buffering", "no");

        await foreach (var chunk in queryService.StreamQueryAsync(req, ct))
        {
            var line = $"data: {JsonSerializer.Serialize(chunk)}\n\n";
            await Response.WriteAsync(line, ct);
            await Response.Body.FlushAsync(ct);
        }

        await Response.WriteAsync("data: [DONE]\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }

    [HttpPost("{queryId:guid}/feedback")]
    public async Task<ActionResult<ApiResponse>> Feedback(
        Guid queryId,
        [FromBody] FeedbackRequest request,
        CancellationToken ct)
    {
        var result = await queryService.UpdateFeedbackAsync(queryId, request.Feedback, ct);
        return result.Success
            ? Ok(new ApiResponse(true, null, this.GetTraceId()))
            : BadRequest(this.ApiFail(result.Error!));
    }
}
