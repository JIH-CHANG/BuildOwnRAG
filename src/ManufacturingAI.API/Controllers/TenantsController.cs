using ManufacturingAI.API.Extensions;
using ManufacturingAI.Core.Common;
using ManufacturingAI.Core.Interfaces;
using ManufacturingAI.Core.Models;
using ManufacturingAI.Infrastructure.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ManufacturingAI.API.Controllers;

public record CreateTenantRequest(
    string Name,
    TenantPlan Plan,
    // Optional: provide dimensions to pre-create the Qdrant collection immediately.
    // Leave 0 to defer creation to first document ingest.
    int EmbeddingDimensions = 0);

public record TenantResponse(
    Guid Id, string Name, TenantPlan Plan, int CollectionVersion,
    int EmbeddingDimensions, DateTime CreatedAt);

[ApiController]
[Route("api/v1/tenants")]
[Authorize(Policy = "CanManageUsers")]   // TenantAdmin only
public class TenantsController(
    IRepository<Tenant> tenantRepository,
    ITenantVectorService tenantVectorService) : ControllerBase
{
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ApiResponse<TenantResponse>>> GetById(Guid id, CancellationToken ct)
    {
        var tenant = await tenantRepository.GetByIdAsync(id, ct);
        return tenant is null
            ? NotFound(this.ApiFail("Tenant not found."))
            : Ok(this.ApiOk(ToResponse(tenant)));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<TenantResponse>>> Create(
        [FromBody] CreateTenantRequest request, CancellationToken ct)
    {
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Plan = request.Plan,
            CollectionVersion = 1,
            Settings = new TenantSettings
            {
                EmbeddingDimensions = request.EmbeddingDimensions
            },
            CreatedAt = DateTime.UtcNow
        };

        var result = await tenantRepository.AddAsync(tenant, ct);
        if (!result.Success)
            return StatusCode(500, this.ApiFail(result.Error!));

        // Pre-create Qdrant collection if dimensions are known
        if (request.EmbeddingDimensions > 0)
            await tenantVectorService.InitializeCollectionAsync(tenant.Id, request.EmbeddingDimensions, ct);

        return CreatedAtAction(nameof(GetById), new { id = tenant.Id }, this.ApiOk(ToResponse(tenant)));
    }

    [HttpDelete("{id:guid}")]
    public async Task<ActionResult<ApiResponse>> Delete(Guid id, CancellationToken ct)
    {
        var tenant = await tenantRepository.GetByIdAsync(id, ct);
        if (tenant is null)
            return NotFound(this.ApiFail("Tenant not found."));

        // Remove all Qdrant collections before deleting from DB
        await tenantVectorService.DeleteAllCollectionsAsync(id, ct);

        var result = await tenantRepository.DeleteAsync(id, ct);
        return result.Success
            ? Ok(new ApiResponse(true, null, this.GetTraceId()))
            : StatusCode(500, this.ApiFail(result.Error!));
    }

    private static TenantResponse ToResponse(Tenant t) => new(
        t.Id, t.Name, t.Plan, t.CollectionVersion,
        t.Settings.EmbeddingDimensions, t.CreatedAt);
}
