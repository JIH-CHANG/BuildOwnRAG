using ManufacturingAI.Core.Common;
using ManufacturingAI.Core.Interfaces;
using ManufacturingAI.Core.Models;

namespace ManufacturingAI.Services.Ingest;

public record SyncStatusResult(
    Guid TenantId,
    int TotalConnectors,
    int RunningJobs,
    IEnumerable<ConnectorSyncStatus> Connectors
);

public record ConnectorSyncStatus(
    Guid ConnectorId,
    string DisplayName,
    string ConnectorType,
    SyncStatus Status,
    DateTime? LastSyncedAt,
    string? ErrorMessage,
    int SyncIntervalMinutes
);

public interface IIngestService
{
    Task<Result> TriggerSyncAsync(Guid tenantId, Guid? connectorId = null, CancellationToken ct = default);
    Task<SyncStatusResult> GetSyncStatusAsync(Guid tenantId, CancellationToken ct = default);
    Task<Result> IngestDocumentAsync(Guid tenantId, SourceDocument source, ConnectorConfig config, CancellationToken ct = default);

    /// <summary>
    /// Removes everything tied to a source document that was deleted at its origin:
    /// Qdrant vectors, PostgreSQL chunks, the Document row, and the SyncState bookkeeping.
    /// A no-op (still Ok) when the SourceId was never indexed.
    /// </summary>
    Task<Result> DeleteSourceDocumentAsync(Guid tenantId, ConnectorConfig config, string sourceId, CancellationToken ct = default);

    /// <summary>
    /// Creates a Pending document record immediately (so the UI shows it),
    /// then enqueues a background job to parse, embed, and index the file.
    /// </summary>
    Task<Result> IngestUploadedFileAsync(Guid tenantId, string filePath, string fileName, string mimeType, CancellationToken ct = default);
}
