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

public interface IKnowledgeConnector
{
    string ConnectorType { get; }
    Task<ConnectorTestResult> TestConnectionAsync(ConnectorConfig config, CancellationToken ct = default);
    Task<IEnumerable<SourceDocument>> FetchDeltaAsync(ConnectorConfig config, DateTimeOffset? since, CancellationToken ct = default);
}
