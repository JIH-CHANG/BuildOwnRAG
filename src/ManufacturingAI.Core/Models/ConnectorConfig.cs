namespace ManufacturingAI.Core.Models;

public class ConnectorConfig
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string ConnectorType { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public string SettingsJson { get; set; } = string.Empty; // AES-256 加密的 JSON
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
