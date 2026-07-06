using Hangfire;
using ManufacturingAI.Core.Interfaces;
using ManufacturingAI.Infrastructure.Repositories;
using Microsoft.Extensions.Logging;

namespace ManufacturingAI.Services.Ingest;

public class SyncSchedulerJob(
    IConnectorConfigRepository connectorConfigRepository,
    IEnumerable<IKnowledgeConnector> connectors,
    IIngestService ingestService,
    IIngestQueue ingestQueue,
    ILogger<SyncSchedulerJob> logger)
{
    /// <summary>
    /// Entry point for a connector's recurring schedule: enqueues a single delta sync.
    /// Each connector has its own Hangfire recurring job (see <see cref="ConnectorSyncScheduler"/>),
    /// so the cadence is per-connector rather than one global sweep.
    /// </summary>
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = [300, 300, 300])]
    public async Task SyncConnectorAsync(Guid connectorId)
    {
        var config = await connectorConfigRepository.GetByIdAsync(connectorId);
        if (config is null || !config.IsEnabled)
        {
            logger.LogInformation(
                "Scheduled sync skipped — connector {ConnectorId} not found or disabled.", connectorId);
            return;
        }

        logger.LogInformation(
            "Enqueueing scheduled delta sync for connector {ConnectorId} ({Type}).",
            connectorId, config.ConnectorType);

        await ingestQueue.EnqueueAsync(
            new IngestJobMessage(config.TenantId, config.Id, config.ConnectorType, null, "scheduler"));
    }

    [AutomaticRetry(Attempts = 3, DelaysInSeconds = [300, 300, 300])]
    public async Task RunConnectorAsync(Guid connectorId, DateTimeOffset? since)
    {
        var config = await connectorConfigRepository.GetByIdAsync(connectorId);
        if (config is null || !config.IsEnabled)
        {
            logger.LogWarning("Connector {ConnectorId} not found or disabled.", connectorId);
            return;
        }

        var connector = connectors.FirstOrDefault(c =>
            c.ConnectorType.Equals(config.ConnectorType, StringComparison.OrdinalIgnoreCase));

        if (connector is null)
        {
            logger.LogWarning("No IKnowledgeConnector registered for type '{Type}'.", config.ConnectorType);
            return;
        }

        logger.LogInformation(
            "Starting delta fetch for connector {ConnectorId} ({Type})", connectorId, config.ConnectorType);

        ConnectorDelta delta;
        try
        {
            delta = await connector.FetchDeltaAsync(config, since);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "FetchDeltaAsync failed for connector {ConnectorId}", connectorId);
            throw; // let Hangfire retry
        }

        // Process each document inline — SourceDocument.Content is a Stream and cannot
        // be serialized into a child Hangfire job.  Documents are processed sequentially;
        // the enclosing job itself provides retry resilience at the connector level.
        int succeeded = 0, failed = 0;
        foreach (var doc in delta.Documents)
        {
            try
            {
                var result = await ingestService.IngestDocumentAsync(config.TenantId, doc, config);
                if (result.Success) succeeded++;
                else
                {
                    failed++;
                    logger.LogWarning(
                        "Ingest failed for {SourceId}: {Error}", doc.SourceId, result.Error);
                }
            }
            catch (Exception ex)
            {
                failed++;
                logger.LogError(ex, "Unhandled error ingesting {SourceId}", doc.SourceId);
            }
            finally
            {
                // Always release the underlying file stream
                await doc.Content.DisposeAsync();
            }
        }

        // Propagate source-side deletions after the ingest pass so a rename (delete + create of
        // the same content) never leaves a window where the document is missing entirely.
        int deleted = 0;
        foreach (var sourceId in delta.DeletedSourceIds)
        {
            try
            {
                var result = await ingestService.DeleteSourceDocumentAsync(config.TenantId, config, sourceId);
                if (result.Success) deleted++;
                else
                    logger.LogWarning(
                        "Deletion failed for {SourceId}: {Error}", sourceId, result.Error);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled error deleting {SourceId}", sourceId);
            }
        }

        logger.LogInformation(
            "Connector {ConnectorId}: {OK} ingested, {Fail} failed, {Deleted} deleted",
            connectorId, succeeded, failed, deleted);
    }
}
