namespace ManufacturingAI.Core.Models;

public class TenantLlmConfig
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
}
