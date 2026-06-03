namespace ManufacturingAI.Core.Models;

public class GeneratedTestScript
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? DocumentId { get; set; }
    public string ScriptType { get; set; } = string.Empty;  // python / csv / robotframework
    public string BlobPath { get; set; } = string.Empty;
    public ScriptStatus Status { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public enum ScriptStatus { Pending, Generating, Completed, Failed }
