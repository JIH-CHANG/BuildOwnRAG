namespace ManufacturingAI.Core.Models;

public class DocumentChunk
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public Guid TenantId { get; set; }
    public string Content { get; set; } = string.Empty;
    public int ChunkIndex { get; set; }
    public string VectorId { get; set; } = string.Empty;    // Qdrant 的 point ID
    public ChunkMetadata Metadata { get; set; } = new();
}

public class ChunkMetadata
{
    public string SourceTitle { get; set; } = string.Empty;
    public string SectionTitle { get; set; } = string.Empty;
    public int? PageNumber { get; set; }
    public string SourceType { get; set; } = string.Empty;
    public DateTime DocumentUpdatedAt { get; set; }
}
