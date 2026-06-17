using ManufacturingAI.API.Extensions;
using ManufacturingAI.Core.Common;
using ManufacturingAI.Core.Interfaces;
using ManufacturingAI.Core.Models;
using ManufacturingAI.Infrastructure.Repositories;
using ManufacturingAI.Infrastructure.Security;
using ManufacturingAI.Services.Ingest;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ManufacturingAI.API.Controllers;

// SyncIntervalMinutes controls how often the connector auto-syncs (0 = manual only).
public record CreateConnectorRequest(
    string ConnectorType, string DisplayName, string SettingsJson, int SyncIntervalMinutes = 60);
// SettingsJson is optional: when null/blank the existing (encrypted) settings are preserved,
// so renaming or enabling/disabling a connector doesn't require re-entering secrets.
public record UpdateConnectorRequest(
    string DisplayName, bool IsEnabled, string? SettingsJson = null, int SyncIntervalMinutes = 60);

[ApiController]
[Route("api/v1/connectors")]
[Authorize(Policy = "CanManageConnectors")]
public class ConnectorsController(
    IConnectorConfigRepository connectorRepository,
    IEnumerable<IKnowledgeConnector> knowledgeConnectors,
    IEncryptionService encryption,
    ConnectorSyncScheduler syncScheduler) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<IEnumerable<ConnectorConfig>>>> GetAll(CancellationToken ct)
    {
        var tenantId = User.GetTenantId();
        var items = await connectorRepository.GetAllAsync(tenantId, ct);
        return Ok(this.ApiOk(items));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<ConnectorConfig>>> Create(
        [FromBody] CreateConnectorRequest request, CancellationToken ct)
    {
        var tenantId = User.GetTenantId();
        var entity = new ConnectorConfig
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ConnectorType = request.ConnectorType,
            DisplayName = request.DisplayName,
            IsEnabled = true,
            SettingsJson = encryption.Encrypt(request.SettingsJson),
            SyncIntervalMinutes = request.SyncIntervalMinutes,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var result = await connectorRepository.AddAsync(entity, ct);
        if (!result.Success)
            return BadRequest(this.ApiFail(result.Error!));

        syncScheduler.Schedule(entity.Id, entity.SyncIntervalMinutes);
        return Ok(this.ApiOk(result.Value));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ApiResponse<ConnectorConfig>>> GetById(Guid id, CancellationToken ct)
    {
        var connector = await connectorRepository.GetByIdAsync(id, ct);
        if (connector is null || connector.TenantId != User.GetTenantId())
            return NotFound(this.ApiFail("Connector not found."));
        return Ok(this.ApiOk(connector));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ApiResponse>> Update(
        Guid id, [FromBody] UpdateConnectorRequest request, CancellationToken ct)
    {
        var connector = await connectorRepository.GetByIdAsync(id, ct);
        if (connector is null || connector.TenantId != User.GetTenantId())
            return NotFound(this.ApiFail("Connector not found."));

        connector.DisplayName = request.DisplayName;
        connector.IsEnabled = request.IsEnabled;
        connector.SyncIntervalMinutes = request.SyncIntervalMinutes;
        if (!string.IsNullOrWhiteSpace(request.SettingsJson))
            connector.SettingsJson = encryption.Encrypt(request.SettingsJson);
        connector.UpdatedAt = DateTime.UtcNow;

        var result = await connectorRepository.UpdateAsync(connector, ct);
        if (!result.Success)
            return BadRequest(this.ApiFail(result.Error!));

        // Re-apply the schedule: a disabled connector or a 0 interval clears it.
        if (connector.IsEnabled)
            syncScheduler.Schedule(connector.Id, connector.SyncIntervalMinutes);
        else
            syncScheduler.Remove(connector.Id);

        return Ok(new ApiResponse(true, null, this.GetTraceId()));
    }

    [HttpDelete("{id:guid}")]
    public async Task<ActionResult<ApiResponse>> Delete(Guid id, CancellationToken ct)
    {
        var connector = await connectorRepository.GetByIdAsync(id, ct);
        if (connector is null || connector.TenantId != User.GetTenantId())
            return NotFound(this.ApiFail("Connector not found."));

        var result = await connectorRepository.DeleteAsync(id, ct);
        if (!result.Success)
            return BadRequest(this.ApiFail(result.Error!));

        syncScheduler.Remove(id);
        return Ok(new ApiResponse(true, null, this.GetTraceId()));
    }

    [HttpPost("{id:guid}/test")]
    public async Task<ActionResult<ApiResponse<ConnectorTestResult>>> TestConnection(Guid id, CancellationToken ct)
    {
        var connector = await connectorRepository.GetByIdAsync(id, ct);
        if (connector is null || connector.TenantId != User.GetTenantId())
            return NotFound(this.ApiFail("Connector not found."));

        var impl = knowledgeConnectors.FirstOrDefault(c =>
            c.ConnectorType.Equals(connector.ConnectorType, StringComparison.OrdinalIgnoreCase));
        if (impl is null)
            return BadRequest(this.ApiFail($"No connector implementation for type '{connector.ConnectorType}'."));

        var testResult = await impl.TestConnectionAsync(connector, ct);
        return Ok(this.ApiOk(testResult));
    }
}
