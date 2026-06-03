using System.Text.Json;
using ManufacturingAI.Setup.Models;
using Microsoft.AspNetCore.Mvc;

namespace ManufacturingAI.Setup.Controllers;

[ApiController]
[Route("api/setup")]
internal sealed class SetupController(SetupService setupService) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // GET /api/setup/status
    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var completed = Environment.GetEnvironmentVariable("IS_SETUP_COMPLETED") == "true";
        return Ok(new SetupStatusResponse(completed, 0));
    }

    // POST /api/setup/test-connections
    [HttpPost("test-connections")]
    public async Task<IActionResult> TestConnections(
        [FromBody] TestConnectionsRequest request,
        CancellationToken ct)
    {
        var results = await setupService.TestConnectionsAsync(request);
        return Ok(new TestConnectionsResponse(results));
    }

    // POST /api/setup/test-llm
    [HttpPost("test-llm")]
    public async Task<IActionResult> TestLlm(
        [FromBody] TestLlmRequest request,
        CancellationToken ct)
    {
        var result = await setupService.TestLlmAsync(request);
        return Ok(result);
    }

    // POST /api/setup/install  (SSE stream)
    [HttpPost("install")]
    public async Task Install(
        [FromBody] InstallRequest request,
        CancellationToken ct)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";
        Response.Headers["Connection"] = "keep-alive";

        try
        {
            await foreach (var progress in setupService.InstallAsync(request, ct))
            {
                var json = JsonSerializer.Serialize(progress, JsonOpts);
                await Response.WriteAsync($"data: {json}\n\n", ct);
                await Response.Body.FlushAsync(ct);

                if (progress.Status == "failed")
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — normal
        }
        catch (Exception ex)
        {
            var errJson = JsonSerializer.Serialize(
                new InstallProgress("error", "failed", ex.Message), JsonOpts);
            await Response.WriteAsync($"data: {errJson}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }
    }
}
