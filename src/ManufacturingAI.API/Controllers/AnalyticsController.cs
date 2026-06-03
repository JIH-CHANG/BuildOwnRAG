using ManufacturingAI.API.Extensions;
using ManufacturingAI.Core.Common;
using ManufacturingAI.Services.Analytics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ManufacturingAI.API.Controllers;

[ApiController]
[Route("api/v1/analytics")]
[Authorize(Policy = "CanViewAnalytics")]
public class AnalyticsController(IAnalyticsService analyticsService) : ControllerBase
{
    // GET api/v1/analytics/overview?rangeType=Week
    [HttpGet("overview")]
    public async Task<ActionResult<ApiResponse<OverviewResult>>> GetOverview(
        [FromQuery] string rangeType = "Week",
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        CancellationToken ct = default)
    {
        var range = ParseDateRange(rangeType, from, to);
        var result = await analyticsService.GetOverviewAsync(User.GetTenantId(), range, ct);
        return Ok(this.ApiOk(result));
    }

    // GET api/v1/analytics/top-queries?top=10&rangeType=Month
    [HttpGet("top-queries")]
    public async Task<ActionResult<ApiResponse<IEnumerable<TopQueryResult>>>> GetTopQueries(
        [FromQuery] int top = 10,
        [FromQuery] string rangeType = "Week",
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        CancellationToken ct = default)
    {
        var range = ParseDateRange(rangeType, from, to);
        var result = await analyticsService.GetTopQueriesAsync(User.GetTenantId(), top, range, ct);
        return Ok(this.ApiOk(result));
    }

    // GET api/v1/analytics/confidence-distribution?rangeType=Month
    [HttpGet("confidence-distribution")]
    public async Task<ActionResult<ApiResponse<ConfidenceDistributionResult>>> GetConfidenceDistribution(
        [FromQuery] string rangeType = "Week",
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        CancellationToken ct = default)
    {
        var range = ParseDateRange(rangeType, from, to);
        var result = await analyticsService.GetConfidenceDistributionAsync(User.GetTenantId(), range, ct);
        return Ok(this.ApiOk(result));
    }

    // GET api/v1/analytics/query-trend?rangeType=Month
    [HttpGet("query-trend")]
    public async Task<ActionResult<ApiResponse<IEnumerable<DailyQueryCount>>>> GetQueryTrend(
        [FromQuery] string rangeType = "Week",
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        CancellationToken ct = default)
    {
        var range = ParseDateRange(rangeType, from, to);
        var result = await analyticsService.GetQueryTrendAsync(User.GetTenantId(), range, ct);
        return Ok(this.ApiOk(result));
    }

    private static DateRange ParseDateRange(string rangeType, DateTime? from, DateTime? to) =>
        rangeType.ToLowerInvariant() switch
        {
            "today" => DateRange.Today(),
            "month" => DateRange.ThisMonth(),
            "custom" when from.HasValue && to.HasValue => DateRange.Custom(from.Value, to.Value),
            _ => DateRange.ThisWeek()
        };
}
