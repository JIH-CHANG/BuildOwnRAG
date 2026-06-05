    using ManufacturingAI.Core.Models;
using ManufacturingAI.Infrastructure.Caching;
using ManufacturingAI.Infrastructure.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace ManufacturingAI.Services.Analytics;

// ── DTOs ────────────────────────────────────────────────────────────────────

public enum DateRangeType { Today, Week, Month, Custom }

public record DateRange
{
    public DateRangeType Type { get; init; }
    public DateTime From { get; init; }
    public DateTime To { get; init; }

    public static DateRange Today() => new()
    {
        Type = DateRangeType.Today,
        From = DateTime.UtcNow.Date,
        To = DateTime.UtcNow.Date.AddDays(1).AddTicks(-1)
    };

    public static DateRange ThisWeek()
    {
        var today = DateTime.UtcNow.Date;
        var startOfWeek = today.AddDays(-(int)today.DayOfWeek);
        return new() { Type = DateRangeType.Week, From = startOfWeek, To = DateTime.UtcNow };
    }

    public static DateRange ThisMonth() => new()
    {
        Type = DateRangeType.Month,
        From = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc),
        To = DateTime.UtcNow
    };

    public static DateRange Custom(DateTime from, DateTime to) => new()
    {
        Type = DateRangeType.Custom,
        From = from,
        To = to
    };
}

public record OverviewResult(
    int TotalQueries,
    double PositiveFeedbackRate,
    double NegativeFeedbackRate,
    int TotalDocuments,
    int IndexedDocuments,
    int FailedDocuments,
    double AverageLatencyMs,
    double AverageConfidenceScore);

public record TopQueryResult(
    string Question,
    int Count,
    double AverageConfidenceScore,
    double PositiveRate);

public record ConfidenceBucket(string Range, int Count, double Percentage);

public record ConfidenceDistributionResult(List<ConfidenceBucket> Buckets);

public record DailyQueryCount(DateOnly Date, int Count);

// ── Interface ────────────────────────────────────────────────────────────────

public interface IAnalyticsService
{
    Task<OverviewResult> GetOverviewAsync(Guid tenantId, DateRange range, CancellationToken ct = default);
    Task<IEnumerable<TopQueryResult>> GetTopQueriesAsync(Guid tenantId, int top, DateRange range, CancellationToken ct = default);
    Task<ConfidenceDistributionResult> GetConfidenceDistributionAsync(Guid tenantId, DateRange range, CancellationToken ct = default);
    Task<IEnumerable<DailyQueryCount>> GetQueryTrendAsync(Guid tenantId, DateRange range, CancellationToken ct = default);
}

// ── Implementation ───────────────────────────────────────────────────────────

public class AnalyticsService(
    IQueryLogRepository queryLogRepository,
    IDocumentRepository documentRepository,
    ICacheService cache) : IAnalyticsService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    private static string CacheKey(Guid tenantId, string metric, DateRange range, string? extra = null)
        => $"analytics:{tenantId}:{metric}:{range.From:yyyyMMddHH}:{range.To:yyyyMMddHH}" +
           (extra is null ? "" : $":{extra}");

    public async Task<OverviewResult> GetOverviewAsync(
        Guid tenantId, DateRange range, CancellationToken ct = default)
    {
        var key = CacheKey(tenantId, "overview", range);
        var cached = await cache.GetAsync<OverviewResult>(key, ct);
        if (cached is not null) return cached;

        var logs = (await queryLogRepository.GetByRangeAsync(tenantId, range.From, range.To, ct)).ToList();
        var docs = (await documentRepository.GetAllAsync(tenantId, ct)).ToList();

        var feedbackLogs = logs.Where(l => l.Feedback.HasValue).ToList();
        var posRate = feedbackLogs.Count > 0
            ? (double)feedbackLogs.Count(l => l.Feedback == QueryFeedback.Positive) / feedbackLogs.Count
            : 0;
        var negRate = feedbackLogs.Count > 0
            ? (double)feedbackLogs.Count(l => l.Feedback == QueryFeedback.Negative) / feedbackLogs.Count
            : 0;

        var result = new OverviewResult(
            TotalQueries: logs.Count,
            PositiveFeedbackRate: Math.Round(posRate, 4),
            NegativeFeedbackRate: Math.Round(negRate, 4),
            TotalDocuments: docs.Count,
            IndexedDocuments: docs.Count(d => d.Status == DocumentStatus.Indexed),
            FailedDocuments: docs.Count(d => d.Status == DocumentStatus.Failed),
            AverageLatencyMs: logs.Count > 0 ? Math.Round(logs.Average(l => (double)l.LatencyMs), 2) : 0,
            AverageConfidenceScore: logs.Count > 0 ? Math.Round(logs.Average(l => l.ConfidenceScore), 4) : 0);

        await cache.SetAsync(key, result, CacheTtl, ct);
        return result;
    }

    public async Task<IEnumerable<TopQueryResult>> GetTopQueriesAsync(
        Guid tenantId, int top, DateRange range, CancellationToken ct = default)
    {
        var key = CacheKey(tenantId, "topqueries", range, top.ToString());
        var cached = await cache.GetAsync<List<TopQueryResult>>(key, ct);
        if (cached is not null) return cached;

        var logs = (await queryLogRepository.GetByRangeAsync(tenantId, range.From, range.To, ct)).ToList();

        var results = logs
            .GroupBy(l => l.Question)
            .OrderByDescending(g => g.Count())
            .Take(top)
            .Select(g =>
            {
                var group = g.ToList();
                var withFeedback = group.Where(l => l.Feedback.HasValue).ToList();
                var posRate = withFeedback.Count > 0
                    ? (double)withFeedback.Count(l => l.Feedback == QueryFeedback.Positive) / withFeedback.Count
                    : 0;
                return new TopQueryResult(
                    Question: g.Key,
                    Count: group.Count,
                    AverageConfidenceScore: Math.Round(group.Average(l => l.ConfidenceScore), 4),
                    PositiveRate: Math.Round(posRate, 4));
            })
            .ToList();

        await cache.SetAsync(key, results, CacheTtl, ct);
        return results;
    }

    public async Task<ConfidenceDistributionResult> GetConfidenceDistributionAsync(
        Guid tenantId, DateRange range, CancellationToken ct = default)
    {
        var key = CacheKey(tenantId, "confdist", range);
        var cached = await cache.GetAsync<ConfidenceDistributionResult>(key, ct);
        if (cached is not null) return cached;

        var logs = (await queryLogRepository.GetByRangeAsync(tenantId, range.From, range.To, ct)).ToList();

        var bucketDefs = new[]
        {
            ("0.0–0.2", 0.0, 0.2),
            ("0.2–0.4", 0.2, 0.4),
            ("0.4–0.6", 0.4, 0.6),
            ("0.6–0.8", 0.6, 0.8),
            ("0.8–1.0", 0.8, 1.001)  // inclusive upper bound for 1.0
        };

        var total = logs.Count;
        var buckets = bucketDefs
            .Select(b =>
            {
                var count = logs.Count(l => l.ConfidenceScore >= b.Item2 && l.ConfidenceScore < b.Item3);
                var pct = total > 0 ? Math.Round((double)count / total * 100, 2) : 0;
                return new ConfidenceBucket(b.Item1, count, pct);
            })
            .ToList();

        var result = new ConfidenceDistributionResult(buckets);
        await cache.SetAsync(key, result, CacheTtl, ct);
        return result;
    }

    public async Task<IEnumerable<DailyQueryCount>> GetQueryTrendAsync(
        Guid tenantId, DateRange range, CancellationToken ct = default)
    {
        var key = CacheKey(tenantId, "trend", range);
        var cached = await cache.GetAsync<List<DailyQueryCount>>(key, ct);
        if (cached is not null) return cached;

        var logs = await queryLogRepository.GetByRangeAsync(tenantId, range.From, range.To, ct);

        // Build a map of all dates in range so days with 0 queries are included.
        var countByDay = logs
            .GroupBy(l => DateOnly.FromDateTime(l.CreatedAt))
            .ToDictionary(g => g.Key, g => g.Count());

        var results = new List<DailyQueryCount>();
        for (var d = DateOnly.FromDateTime(range.From); d <= DateOnly.FromDateTime(range.To); d = d.AddDays(1))
            results.Add(new DailyQueryCount(d, countByDay.GetValueOrDefault(d, 0)));

        await cache.SetAsync(key, results, CacheTtl, ct);
        return results;
    }
}

// ── DI ───────────────────────────────────────────────────────────────────────

public static class DependencyInjection
{
    public static IServiceCollection AddAnalyticsServices(this IServiceCollection services)
    {
        services.AddScoped<IAnalyticsService, AnalyticsService>();
        return services;
    }
}
