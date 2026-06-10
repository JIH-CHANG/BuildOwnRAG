using ManufacturingAI.API.Extensions;
using ManufacturingAI.Core.Common;
using ManufacturingAI.Core.Interfaces;
using ManufacturingAI.Core.Models;
using ManufacturingAI.Infrastructure.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
#pragma warning disable CS9113

namespace ManufacturingAI.API.Controllers;

[ApiController]
[Route("api/v1/documents")]
[Authorize(Policy = "CanIngest")]
public class DocumentsController(
    IDocumentRepository documentRepository,
    IDocumentChunkRepository chunkRepository,
    ISyncStateRepository syncStateRepository,
    IVectorStore vectorStore,
    ITenantVectorService tenantVectorService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<object>>> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? status = null,
        CancellationToken ct = default)
    {
        var tenantId = User.GetTenantId();
        var docs = await documentRepository.GetAllAsync(tenantId, ct);

        if (!string.IsNullOrEmpty(status) && Enum.TryParse<DocumentStatus>(status, true, out var docStatus))
            docs = docs.Where(d => d.Status == docStatus);

        var total = docs.Count();
        var paged = docs.Skip((page - 1) * pageSize).Take(pageSize);
        return Ok(this.ApiOk(new { items = paged, total, page, pageSize }));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ApiResponse<Document>>> GetById(Guid id, CancellationToken ct)
    {
        var doc = await documentRepository.GetByIdAsync(id, ct);
        if (doc is null || doc.TenantId != User.GetTenantId())
            return NotFound(this.ApiFail("Document not found."));
        return Ok(this.ApiOk(doc));
    }

    [HttpDelete("{id:guid}")]
    public async Task<ActionResult<ApiResponse>> Delete(Guid id, CancellationToken ct)
    {
        var tenantId = User.GetTenantId();
        var doc = await documentRepository.GetByIdAsync(id, ct);
        if (doc is null || doc.TenantId != tenantId)
            return NotFound(this.ApiFail("Document not found."));

        // Clean up everything tied to the document so a later re-upload starts fresh:
        // Qdrant vectors, Postgres chunks (BM25/Lite source), and the version-hash sync
        // bookkeeping (otherwise dedup would skip re-ingesting the recreated document).
        var collectionName = await tenantVectorService.GetActiveCollectionNameAsync(tenantId, ct);
        await vectorStore.DeleteByDocumentIdAsync(collectionName, id, ct);
        await chunkRepository.DeleteByDocumentIdAsync(id, ct);
        await syncStateRepository.DeleteBySourceAsync(tenantId, doc.SourceId, ct);
        var result = await documentRepository.DeleteAsync(id, ct);
        return result.Success
            ? Ok(new ApiResponse(true, null, this.GetTraceId()))
            : BadRequest(this.ApiFail(result.Error!));
    }
}
