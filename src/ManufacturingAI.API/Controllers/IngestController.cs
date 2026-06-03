using ManufacturingAI.API.Extensions;
using ManufacturingAI.Core.Common;
using ManufacturingAI.Infrastructure.Repositories;
using ManufacturingAI.Services.Ingest;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ManufacturingAI.API.Controllers;

public record TriggerSyncRequest(Guid? ConnectorId = null);

[ApiController]
[Route("api/v1/ingest")]
[Authorize(Policy = "CanIngest")]
public class IngestController(
    IIngestService ingestService,
    IQueryLogRepository queryLogRepository,
    IConfiguration config) : ControllerBase
{
    [HttpPost("trigger")]
    public async Task<ActionResult<ApiResponse>> Trigger(
        [FromBody] TriggerSyncRequest request,
        CancellationToken ct)
    {
        var tenantId = User.GetTenantId();
        var result = await ingestService.TriggerSyncAsync(tenantId, request.ConnectorId, ct);
        return result.Success
            ? Ok(new ApiResponse(true, null, this.GetTraceId()))
            : BadRequest(this.ApiFail(result.Error!));
    }

    [HttpGet("status")]
    [Authorize(Policy = "CanViewIngest")]
    public async Task<ActionResult<ApiResponse<SyncStatusResult>>> GetStatus(CancellationToken ct)
    {
        var tenantId = User.GetTenantId();
        var result = await ingestService.GetSyncStatusAsync(tenantId, ct);
        return Ok(this.ApiOk(result));
    }

    [HttpPost("upload")]
    [RequestSizeLimit(100 * 1024 * 1024)] // 100 MB
    public async Task<ActionResult<ApiResponse>> Upload(
        IFormFileCollection files, CancellationToken ct)
    {
        if (files.Count == 0)
            return BadRequest(this.ApiFail("No files provided."));

        var tenantId = User.GetTenantId();

        var uploadFolder = config["Ingest:UploadFolder"] is { Length: > 0 } f
            ? f
            : Path.Combine(AppContext.BaseDirectory, "uploads");
        Directory.CreateDirectory(uploadFolder);

        foreach (var file in files)
        {
            if (file.Length == 0) continue;
            var safeName = Path.GetFileName(file.FileName);
            var dest = Path.Combine(uploadFolder, safeName);
            await using var stream = System.IO.File.Create(dest);
            await file.CopyToAsync(stream, ct);

            var mimeType = file.ContentType is { Length: > 0 } ct2
                ? ct2
                : "application/octet-stream";
            // Browsers report .md inconsistently; normalize by extension so it routes to the text parser.
            if (Path.GetExtension(safeName).ToLowerInvariant() is ".md" or ".markdown")
                mimeType = "text/markdown";
            await ingestService.IngestUploadedFileAsync(tenantId, dest, safeName, mimeType, ct);
        }

        return Ok(new ApiResponse(true, null, this.GetTraceId()));
    }

    [HttpGet("history")]
    [Authorize(Policy = "CanViewIngest")]
    public async Task<ActionResult<ApiResponse<object>>> GetHistory(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var tenantId = User.GetTenantId();
        var (items, total) = await queryLogRepository.GetByTenantAsync(tenantId, page, pageSize, ct);
        return Ok(this.ApiOk(new { items, total, page, pageSize }));
    }
}
