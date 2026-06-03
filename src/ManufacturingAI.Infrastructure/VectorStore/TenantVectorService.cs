using ManufacturingAI.Core.Interfaces;
using ManufacturingAI.Core.Models;
using ManufacturingAI.Infrastructure.Repositories;
using Microsoft.Extensions.Logging;

namespace ManufacturingAI.Infrastructure.VectorStore;

public class TenantVectorService(
    IRepository<Tenant> tenantRepository,
    IVectorStore vectorStore,
    ILogger<TenantVectorService> logger) : ITenantVectorService
{
    // v1 uses the legacy bare name for backward compat with existing collections
    public string GetCollectionName(Guid tenantId, int version)
        => version <= 1 ? $"tenant_{tenantId}" : $"tenant_{tenantId}_v{version}";

    public async Task<string> GetActiveCollectionNameAsync(Guid tenantId, CancellationToken ct = default)
    {
        var tenant = await tenantRepository.GetByIdAsync(tenantId, ct);
        return GetCollectionName(tenantId, tenant?.CollectionVersion ?? 1);
    }

    public async Task InitializeCollectionAsync(Guid tenantId, int dimensions, CancellationToken ct = default)
    {
        var tenant = await tenantRepository.GetByIdAsync(tenantId, ct);
        if (tenant is null)
        {
            logger.LogWarning("InitializeCollection: tenant {TenantId} not found", tenantId);
            return;
        }

        var collectionName = GetCollectionName(tenantId, tenant.CollectionVersion);
        await vectorStore.EnsureCollectionAsync(collectionName, dimensions, ct);

        if (tenant.Settings.EmbeddingDimensions == dimensions) return;

        tenant.Settings.EmbeddingDimensions = dimensions;
        await tenantRepository.UpdateAsync(tenant, ct);
        logger.LogInformation(
            "Tenant {TenantId}: recorded EmbeddingDimensions={Dims}", tenantId, dimensions);
    }

    public async Task DeleteAllCollectionsAsync(Guid tenantId, CancellationToken ct = default)
    {
        var tenant = await tenantRepository.GetByIdAsync(tenantId, ct);
        var maxVersion = tenant?.CollectionVersion ?? 1;

        for (int v = 1; v <= maxVersion; v++)
        {
            var name = GetCollectionName(tenantId, v);
            await vectorStore.DeleteCollectionAsync(name, ct);
        }

        logger.LogInformation(
            "Deleted {Count} Qdrant collection(s) for tenant {TenantId}", maxVersion, tenantId);
    }

    public async Task<(string CollectionName, bool MigrationRequired)> EnsureDimensionsCompatibleAsync(
        Guid tenantId, int dimensions, CancellationToken ct = default)
    {
        var tenant = await tenantRepository.GetByIdAsync(tenantId, ct);
        if (tenant is null)
        {
            // Tenant not found: return bare name; caller will fail on DB ops anyway
            return (GetCollectionName(tenantId, 1), false);
        }

        var collectionName = GetCollectionName(tenantId, tenant.CollectionVersion);

        // First ingest for this tenant: record dimensions and ensure collection
        if (tenant.Settings.EmbeddingDimensions == 0)
        {
            tenant.Settings.EmbeddingDimensions = dimensions;
            await tenantRepository.UpdateAsync(tenant, ct);
            await vectorStore.EnsureCollectionAsync(collectionName, dimensions, ct);
            logger.LogInformation(
                "Tenant {TenantId}: initialized collection '{Name}' with {Dims} dims",
                tenantId, collectionName, dimensions);
            return (collectionName, false);
        }

        // Dimensions unchanged: idempotent check
        if (tenant.Settings.EmbeddingDimensions == dimensions)
        {
            await vectorStore.EnsureCollectionAsync(collectionName, dimensions, ct);
            return (collectionName, false);
        }

        // Dimensions changed: caller must enqueue a migration job
        logger.LogWarning(
            "Tenant {TenantId}: embedding dimension mismatch — stored={Old}, current={New}. " +
            "Migration required.",
            tenantId, tenant.Settings.EmbeddingDimensions, dimensions);

        return (collectionName, true);
    }
}
