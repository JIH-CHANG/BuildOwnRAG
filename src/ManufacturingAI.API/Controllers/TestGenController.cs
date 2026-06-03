using ManufacturingAI.API.Extensions;
using ManufacturingAI.Core.Common;
using ManufacturingAI.Core.Models;
using ManufacturingAI.Services.TestGen;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ManufacturingAI.API.Controllers;

public record GenerateTestScriptRequest(Guid? DocumentId, string ScriptType);

[ApiController]
[Route("api/v1/testgen")]
[Authorize(Policy = "CanIngest")]
public class TestGenController(ITestGenService testGenService) : ControllerBase
{
    [HttpPost("generate")]
    public async Task<ActionResult<ApiResponse<Guid>>> Generate(
        [FromBody] GenerateTestScriptRequest request, CancellationToken ct)
    {
        var tenantId = User.GetTenantId();
        var result = await testGenService.TriggerGenerationAsync(tenantId, request.DocumentId, request.ScriptType, ct);
        return result.Success
            ? Ok(this.ApiOk(result.Value))
            : BadRequest(this.ApiFail(result.Error!));
    }

    [HttpGet("{id:guid}/status")]
    public async Task<ActionResult<ApiResponse<GeneratedTestScript>>> GetStatus(Guid id, CancellationToken ct)
    {
        var result = await testGenService.GetStatusAsync(id, ct);
        return result.Success
            ? Ok(this.ApiOk(result.Value))
            : NotFound(this.ApiFail(result.Error!));
    }

    [HttpGet("{id:guid}/download")]
    public async Task<IActionResult> Download(Guid id, CancellationToken ct)
    {
        var result = await testGenService.DownloadAsync(id, ct);
        if (!result.Success)
            return NotFound(this.ApiFail(result.Error!));

        return File(result.Value!, "application/octet-stream", $"testscript_{id}");
    }
}
