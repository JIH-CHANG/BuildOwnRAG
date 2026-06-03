using ManufacturingAI.Core.Common;
using ManufacturingAI.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace ManufacturingAI.API.Controllers;

public record HealthDetail(bool Database, bool Redis, bool Qdrant, string? Error = null);

[ApiController]
[Route("health")]
public class HealthController(
    ApplicationDbContext db,
    IConnectionMultiplexer redis,
    IConfiguration config) : ControllerBase
{
    [HttpGet]
    public ActionResult<ApiResponse<string>> Basic()
        => Ok(new ApiResponse<string>(true, "Healthy", null, null));

    [HttpGet("detail")]
    public async Task<ActionResult<ApiResponse<HealthDetail>>> Detail(CancellationToken ct)
    {
        bool dbOk = false, redisOk = false, qdrantOk = false;
        string? error = null;

        try { await db.Database.ExecuteSqlRawAsync("SELECT 1", ct); dbOk = true; }
        catch (Exception ex) { error = $"DB: {ex.Message}"; }

        try { await redis.GetDatabase().PingAsync(); redisOk = true; }
        catch (Exception ex) { error += $" | Redis: {ex.Message}"; }

        try
        {
            var host = config["Qdrant:Host"] ?? "localhost";
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var resp = await http.GetAsync($"http://{host}:6333/healthz", ct);
            qdrantOk = resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { error += $" | Qdrant: {ex.Message}"; }

        var detail = new HealthDetail(dbOk, redisOk, qdrantOk, error);
        return Ok(new ApiResponse<HealthDetail>(dbOk && redisOk, detail, error, null));
    }
}
