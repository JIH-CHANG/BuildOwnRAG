namespace ManufacturingAI.Core.Models;

public class SyncState
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConnectorId { get; set; }
    public string SourceId { get; set; } = string.Empty;
    public DateTime? LastSyncedAt { get; set; }
    public string VersionHash { get; set; } = string.Empty;
    public SyncStatus Status { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
}

public enum SyncStatus { Pending, Running, Completed, Failed }
