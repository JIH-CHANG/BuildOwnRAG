namespace ManufacturingAI.Infrastructure.Caching;

public interface ICacheService
{
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default);
    Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken ct = default);
    Task RemoveAsync(string key, CancellationToken ct = default);
    Task RemoveByPatternAsync(string pattern, CancellationToken ct = default);
}

public static class CacheKeys
{
    public static string QueryResult(Guid tenantId, string queryHash) => $"query:{tenantId}:{queryHash}";
    public static string TenantSettings(Guid tenantId) => $"tenant:{tenantId}:settings";
    public static string DocumentList(Guid tenantId) => $"tenant:{tenantId}:documents";

    public static readonly TimeSpan QueryResultTtl = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan TenantSettingsTtl = TimeSpan.FromMinutes(60);
    public static readonly TimeSpan DocumentListTtl = TimeSpan.FromMinutes(5);
}
