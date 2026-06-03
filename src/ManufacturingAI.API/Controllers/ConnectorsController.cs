using ManufacturingAI.API.Extensions;
using ManufacturingAI.Core.Common;
using ManufacturingAI.Core.Interfaces;
using ManufacturingAI.Core.Models;
using ManufacturingAI.Infrastructure.Repositories;
using ManufacturingAI.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ManufacturingAI.API.Controllers;

public record CreateConnectorRequest(string ConnectorType, string DisplayName, string SettingsJson);
public record UpdateConnectorRequest(string DisplayName, bool IsEnabled, string SettingsJson);

[ApiController]
[Route("api/v1/connectors")]
[Authorize(Policy = "CanManageConnectors")]
public class ConnectorsController(
    IConnectorConfigRepository connectorRepository,
    IEnumerable<IKnowledgeConnector> knowledgeConnectors,
    IEncryptionService encryption) : ControllerBase
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
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var result = await connectorRepository.AddAsync(entity, ct);
        return result.Success
            ? Ok(this.ApiOk(result.Value))
            : BadRequest(this.ApiFail(result.Error!));
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
        connector.SettingsJson = encryption.Encrypt(request.SettingsJson);
        connector.UpdatedAt = DateTime.UtcNow;

        var result = await connectorRepository.UpdateAsync(connector, ct);
        return result.Success
            ? Ok(new ApiResponse(true, null, this.GetTraceId()))
            : BadRequest(this.ApiFail(result.Error!));
    }

    [HttpDelete("{id:guid}")]
    public async Task<ActionResult<ApiResponse>> Delete(Guid id, CancellationToken ct)
    {
        var connector = await connectorRepository.GetByIdAsync(id, ct);
        if (connector is null || connector.TenantId != User.GetTenantId())
            return NotFound(this.ApiFail("Connector not found."));

        var result = await connectorRepository.DeleteAsync(id, ct);
        return result.Success
            ? Ok(new ApiResponse(true, null, this.GetTraceId()))
            : BadRequest(this.ApiFail(result.Error!));
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
