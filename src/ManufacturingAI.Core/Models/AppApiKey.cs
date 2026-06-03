namespace ManufacturingAI.Core.Models;

public class AppApiKey
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string KeyHash { get; set; } = string.Empty;     // SHA-256 of raw key
    public string KeyPrefix { get; set; } = string.Empty;   // first 8 chars of raw key, for display
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}
