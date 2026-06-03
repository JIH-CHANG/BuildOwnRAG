namespace ManufacturingAI.Services.Ingest;

public record IngestJobMessage(
    Guid TenantId,
    Guid ConnectorId,
    string ConnectorType,
    DateTimeOffset? Since,
    string TriggeredBy);
