namespace ManufacturingAI.Core.Models;

public class Document
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string SourceType { get; set; } = string.Empty;  // folder / arena / confluence / gdrive / sharepoint
    public string SourceId { get; set; } = string.Empty;    // 來源系統的原始 ID
    public string Title { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string MimeType { get; set; } = string.Empty;
    public string VersionHash { get; set; } = string.Empty; // SHA256
    public long FileSizeBytes { get; set; }
    public DocumentStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public enum DocumentStatus { Pending, Processing, Indexed, Failed }
