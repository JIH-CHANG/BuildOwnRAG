using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace ManufacturingAI.Services.Ingest;

/// <summary>
/// Runs once at startup to ensure the Redis Stream consumer group exists.
/// Must be registered before IngestWorker so the group is ready before consumption begins.
/// </summary>
internal sealed class RedisStreamConsumerGroupInitializer(
    IConnectionMultiplexer redis,
    ILogger<RedisStreamConsumerGroupInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var db = redis.GetDatabase();
        try
        {
            // XGROUP CREATE docs:ingest ingest-workers $ MKSTREAM
            await db.StreamCreateConsumerGroupAsync(
                RedisStreamIngestQueue.StreamKey,
                RedisStreamIngestQueue.GroupName,
                position: StreamPosition.NewMessages,
                createStream: true);

            logger.LogInformation(
                "Redis Stream consumer group '{Group}' created on stream '{Stream}'.",
                RedisStreamIngestQueue.GroupName,
                RedisStreamIngestQueue.StreamKey);
        }
        catch (RedisServerException ex) when (ex.Message.StartsWith("BUSYGROUP"))
        {
            // Group already exists — this is the normal case on restart
            logger.LogDebug(
                "Redis Stream consumer group '{Group}' already exists — skipping creation.",
                RedisStreamIngestQueue.GroupName);
        }
        catch (Exception ex)
        {
            // Log but don't crash — IngestWorker will handle NOGROUP gracefully
            logger.LogError(ex,
                "Failed to create Redis Stream consumer group '{Group}' on stream '{Stream}'.",
                RedisStreamIngestQueue.GroupName,
                RedisStreamIngestQueue.StreamKey);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
