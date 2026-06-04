using ManufacturingAI.Core.Common;
using ManufacturingAI.Core.Models;

namespace ManufacturingAI.Infrastructure.Repositories;

public interface IDocumentRepository : IRepository<Document>
{
    Task<Document?> GetBySourceIdAsync(Guid tenantId, string sourceType, string sourceId, CancellationToken ct = default);
    Task<IEnumerable<Document>> GetByStatusAsync(Guid tenantId, DocumentStatus status, CancellationToken ct = default);
    Task<Result> UpdateStatusAsync(Guid id, DocumentStatus status, CancellationToken ct = default);
}

public interface IDocumentChunkRepository : IRepository<DocumentChunk>
{
    Task<IEnumerable<DocumentChunk>> GetByDocumentIdAsync(Guid documentId, CancellationToken ct = default);
    Task<IEnumerable<DocumentChunk>> GetByIdsAsync(Guid tenantId, IEnumerable<string> vectorIds, CancellationToken ct = default);
    // Coarse keyword prefilter (ILIKE on any term) for Lite mode — candidates for in-memory BM25.
    Task<IReadOnlyList<DocumentChunk>> SearchByKeywordAsync(Guid tenantId, string query, int limit, CancellationToken ct = default);
    // Bulk-delete all chunks for a document (used when deleting/re-ingesting a document).
    Task<int> DeleteByDocumentIdAsync(Guid documentId, CancellationToken ct = default);
}

public interface ISyncStateRepository : IRepository<SyncState>
{
    Task<IEnumerable<SyncState>> GetByConnectorAsync(Guid connectorId, CancellationToken ct = default);
    Task<Result<SyncState>> UpsertAsync(SyncState entity, CancellationToken ct = default);
    // Remove sync bookkeeping for a source so a later re-upload is treated as a fresh ingest
    // (otherwise the version-hash dedup would skip re-processing the recreated document).
    Task<int> DeleteBySourceAsync(Guid tenantId, string sourceId, CancellationToken ct = default);
}

public interface IQueryLogRepository : IRepository<QueryLog>
{
    Task<(IEnumerable<QueryLog> Items, int Total)> GetByTenantAsync(Guid tenantId, int page, int pageSize, CancellationToken ct = default);
    Task<IEnumerable<QueryLog>> GetByRangeAsync(Guid tenantId, DateTime from, DateTime to, CancellationToken ct = default);
    Task<Result> UpdateFeedbackAsync(Guid id, QueryFeedback feedback, CancellationToken ct = default);
}

public interface IConnectorConfigRepository : IRepository<ConnectorConfig>
{
    Task<IEnumerable<ConnectorConfig>> GetEnabledByTenantAsync(Guid tenantId, CancellationToken ct = default);
    Task<IEnumerable<ConnectorConfig>> GetAllEnabledAsync(CancellationToken ct = default);
}

public interface IRefreshTokenRepository : IRepository<AppRefreshToken>
{
    Task<AppRefreshToken?> GetByTokenHashAsync(string tokenHash, CancellationToken ct = default);
    Task<List<AppRefreshToken>> GetActiveByUserAsync(Guid userId, CancellationToken ct = default);
}

public interface IApiKeyRepository : IRepository<AppApiKey>
{
    Task<AppApiKey?> GetByKeyHashAsync(string keyHash, CancellationToken ct = default);
    Task UpdateLastUsedAtAsync(Guid id, CancellationToken ct = default);
}

public interface IUserRepository : IRepository<AppUser>
{
    Task<AppUser?> FindByEmailAsync(string email, CancellationToken ct = default);
}
