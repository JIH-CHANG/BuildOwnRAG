namespace ManufacturingAI.Core.Interfaces;

public interface ITenantVectorService
{
    /// <summary>Returns the Qdrant collection name for a given tenant and version.</summary>
    /// <remarks>v1 uses the legacy bare name for backward compat; v2+ appends _v{n}.</remarks>
    string GetCollectionName(Guid tenantId, int version);

    /// <summary>Loads CollectionVersion from DB and returns the active collection name.</summary>
    Task<string> GetActiveCollectionNameAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Creates the Qdrant collection (if missing) and records dimensions in TenantSettings.
    /// Safe to call multiple times (idempotent).
    /// </summary>
    Task InitializeCollectionAsync(Guid tenantId, int dimensions, CancellationToken ct = default);

    /// <summary>Deletes every versioned collection for the tenant from Qdrant.</summary>
    Task DeleteAllCollectionsAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Checks whether the current collection dimensions match the requested dimensions.
    /// Returns (activeCollectionName, migrationRequired).
    /// When migrationRequired=true the caller is responsible for enqueueing a migration job.
    /// </summary>
    Task<(string CollectionName, bool MigrationRequired)> EnsureDimensionsCompatibleAsync(
        Guid tenantId, int dimensions, CancellationToken ct = default);
}
