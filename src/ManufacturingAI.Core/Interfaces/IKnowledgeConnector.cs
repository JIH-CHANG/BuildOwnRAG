namespace ManufacturingAI.Core.Interfaces;

using ManufacturingAI.Core.Models;

public record SourceDocument(
    string SourceId,
    string Title,
    Stream Content,
    string MimeType,
    string VersionHash,
    DateTimeOffset LastModified,
    Dictionary<string, string> Metadata
);

public record ConnectorTestResult(bool Success, string? ErrorMessage = null);

/// <summary>
/// One sync's worth of changes from a connector: documents to (re-)ingest plus the
/// SourceIds of items that no longer exist at the source (deleted/trashed/moved out
/// of scope). Deleted IDs the pipeline has never indexed are ignored downstream, so
/// connectors may over-report rather than track what was actually ingested.
/// </summary>
public record ConnectorDelta(
    IEnumerable<SourceDocument> Documents,
    IReadOnlyCollection<string> DeletedSourceIds)
{
    public static readonly ConnectorDelta Empty = new([], []);
}

public interface IKnowledgeConnector
{
    string ConnectorType { get; }
    Task<ConnectorTestResult> TestConnectionAsync(ConnectorConfig config, CancellationToken ct = default);
    Task<ConnectorDelta> FetchDeltaAsync(ConnectorConfig config, DateTimeOffset? since, CancellationToken ct = default);
}
