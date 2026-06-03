using Hangfire;
using ManufacturingAI.Core.Interfaces;
using ManufacturingAI.Core.Models;
using ManufacturingAI.Infrastructure.Repositories;
using Microsoft.Extensions.Logging;

namespace ManufacturingAI.Services.Ingest;

public class ReingestJob(
    IRepository<Tenant> tenantRepository,
    IDocumentChunkRepository chunkRepository,
    IEmbeddingService embeddingService,
    IVectorStore vectorStore,
    ITenantVectorService tenantVectorService,
    ILogger<ReingestJob> logger)
{
    [AutomaticRetry(Attempts = 2, DelaysInSeconds = [300, 600])]
    public async Task MigrateCollectionAsync(Guid tenantId, int newDimensions, CancellationToken ct)
    {
        var tenant = await tenantRepository.GetByIdAsync(tenantId, ct);
        if (tenant is null)
        {
            logger.LogError("ReingestJob: tenant {TenantId} not found — aborting", tenantId);
            return;
        }

        var newVersion = tenant.CollectionVersion + 1;
        var oldCollectionName = tenantVectorService.GetCollectionName(tenantId, tenant.CollectionVersion);
        var newCollectionName = tenantVectorService.GetCollectionName(tenantId, newVersion);

        logger.LogInformation(
            "Starting collection migration for tenant {TenantId}: '{Old}' → '{New}' ({Dims} dims)",
            tenantId, oldCollectionName, newCollectionName, newDimensions);

        // 1. Create new versioned collection
        await vectorStore.EnsureCollectionAsync(newCollectionName, newDimensions, ct);

        // 2. Load all chunks and re-embed into new collection
        var allDocs = await chunkRepository.GetByIdsAsync(tenantId, [], ct);  // all chunks: empty filter = all
        var chunks = allDocs.ToList();

        logger.LogInformation(
            "Re-embedding {Count} chunks for tenant {TenantId}", chunks.Count, tenantId);

        const int batchSize = 32;
        for (int i = 0; i < chunks.Count; i += batchSize)
        {
            var batch = chunks.Skip(i).Take(batchSize).ToList();
            var contents = batch.Select(c => c.Content).ToList();
            var vectors = (await embeddingService.EmbedBatchAsync(contents, ct)).ToList();

            for (int j = 0; j < batch.Count; j++)
            {
                var chunk = batch[j];
                var vDoc = new VectorDocument(
                    Id: chunk.VectorId,
                    Vector: vectors[j],
                    Payload: new Dictionary<string, object>
                    {
                        ["documentId"] = chunk.DocumentId.ToString(),
                        ["tenantId"] = tenantId.ToString(),
                        ["chunkIndex"] = chunk.ChunkIndex,
                        ["sourceType"] = chunk.Metadata.SourceType
                    });
                await vectorStore.UpsertAsync(newCollectionName, vDoc, ct);
            }
        }

        // 3. Atomically switch the active version in PostgreSQL
        tenant.CollectionVersion = newVersion;
        tenant.Settings.EmbeddingDimensions = newDimensions;
        await tenantRepository.UpdateAsync(tenant, ct);

        logger.LogInformation(
            "Tenant {TenantId}: switched to collection '{New}' (v{Version})",
            tenantId, newCollectionName, newVersion);

        // 4. Delete old collection
        await vectorStore.DeleteCollectionAsync(oldCollectionName, ct);

        logger.LogInformation(
            "Collection migration completed for tenant {TenantId}", tenantId);
    }
}
