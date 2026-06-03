using ManufacturingAI.Services.Ingest;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ManufacturingAI.API.Workers;

public sealed class IngestWorker(
    IIngestQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<IngestWorker> logger) : BackgroundService
{
    private static readonly string ConsumerName = $"worker-{Environment.MachineName}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Environment.GetEnvironmentVariable("WORKER_ENABLED") == "false")
        {
            logger.LogInformation("IngestWorker disabled via WORKER_ENABLED=false.");
            return;
        }

        logger.LogInformation("IngestWorker started as consumer '{Consumer}'.", ConsumerName);

        while (!stoppingToken.IsCancellationRequested)
        {
            var result = await queue.DequeueAsync(ConsumerName, stoppingToken);
            if (result is null) break; // stoppingToken cancelled

            var (message, entryId) = result.Value;

            logger.LogInformation(
                "Processing ingest job for connector {ConnectorId} (triggered by {TriggeredBy}).",
                message.ConnectorId, message.TriggeredBy);

            await using var scope = scopeFactory.CreateAsyncScope();
            var job = scope.ServiceProvider.GetRequiredService<SyncSchedulerJob>();

            try
            {
                await job.RunConnectorAsync(message.ConnectorId, message.Since);
                await queue.AckAsync(entryId, stoppingToken);

                logger.LogInformation(
                    "Connector {ConnectorId} ingested successfully — entry {EntryId} ACKed.",
                    message.ConnectorId, entryId);
            }
            catch (Exception ex)
            {
                // Do NOT ACK — entry stays in PEL for manual inspection / future reclaim
                logger.LogError(ex,
                    "Ingest failed for connector {ConnectorId} (entry {EntryId}) — not ACKed.",
                    message.ConnectorId, entryId);
            }
        }

        logger.LogInformation("IngestWorker stopped.");
    }
}
