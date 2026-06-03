using StackExchange.Redis;
using System.Text.Json;

namespace ManufacturingAI.Infrastructure.Caching;

public class RedisCacheService(IConnectionMultiplexer redis) : ICacheService
{
    private readonly IDatabase _db = redis.GetDatabase();

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        var value = await _db.StringGetAsync(key);
        if (!value.HasValue) return default;
        return JsonSerializer.Deserialize<T>((string)value!);
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(value);
        if (ttl.HasValue)
            await _db.StringSetAsync(key, json, ttl.Value);
        else
            await _db.StringSetAsync(key, json);
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
        => await _db.KeyDeleteAsync(key);

    public async Task RemoveByPatternAsync(string pattern, CancellationToken ct = default)
    {
        var server = redis.GetServers().FirstOrDefault()
            ?? throw new InvalidOperationException("No Redis server available.");

        var keys = server.Keys(pattern: pattern).ToArray();
        if (keys.Length > 0)
            await _db.KeyDeleteAsync(keys);
    }
}
