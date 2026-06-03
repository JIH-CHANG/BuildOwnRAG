using ManufacturingAI.API.Auth;
using ManufacturingAI.API.Extensions;
using ManufacturingAI.Core.Common;
using ManufacturingAI.Core.Models;
using ManufacturingAI.Infrastructure.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Cryptography;

namespace ManufacturingAI.API.Controllers;

public record ApiKeyResponse(Guid Id, string Name, string KeyPrefix, DateTime CreatedAt, DateTime? LastUsedAt);
public record CreateApiKeyRequest(string Name);
public record CreateApiKeyResponse(Guid Id, string Name, string Key);

[ApiController]
[Route("api/v1/api-keys")]
[Authorize(Policy = "CanManageUsers")]
public class ApiKeysController(IApiKeyRepository apiKeyRepository) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<IEnumerable<ApiKeyResponse>>>> GetAll(CancellationToken ct)
    {
        var tenantId = User.GetTenantId();
        var keys = await apiKeyRepository.GetAllAsync(tenantId, ct);
        return Ok(this.ApiOk(keys
            .Where(k => k.IsActive)
            .Select(ToResponse)));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<CreateApiKeyResponse>>> Create(
        [FromBody] CreateApiKeyRequest request, CancellationToken ct)
    {
        var tenantId = User.GetTenantId();

        var rawKey = "buildownrag_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var keyHash = TokenService.HashToken(rawKey);
        var keyPrefix = rawKey[..16]; // "buildownrag_xxxx"

        var entity = new AppApiKey
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = request.Name,
            KeyHash = keyHash,
            KeyPrefix = keyPrefix,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        var result = await apiKeyRepository.AddAsync(entity, ct);
        return result.Success
            ? Ok(this.ApiOk(new CreateApiKeyResponse(entity.Id, entity.Name, rawKey)))
            : StatusCode(500, this.ApiFail(result.Error!));
    }

    [HttpDelete("{id:guid}")]
    public async Task<ActionResult<ApiResponse>> Revoke(Guid id, CancellationToken ct)
    {
        var tenantId = User.GetTenantId();
        var key = await apiKeyRepository.GetByIdAsync(id, ct);

        if (key is null || key.TenantId != tenantId)
            return NotFound(this.ApiFail("API key not found."));

        key.IsActive = false;
        var result = await apiKeyRepository.UpdateAsync(key, ct);
        return result.Success
            ? Ok(new ApiResponse(true, null, this.GetTraceId()))
            : StatusCode(500, this.ApiFail(result.Error!));
    }

    private static ApiKeyResponse ToResponse(AppApiKey k) =>
        new(k.Id, k.Name, k.KeyPrefix, k.CreatedAt, k.LastUsedAt);
}
