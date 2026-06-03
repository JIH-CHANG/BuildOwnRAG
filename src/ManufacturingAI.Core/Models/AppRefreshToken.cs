namespace ManufacturingAI.Core.Models;

public class AppRefreshToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid TenantId { get; set; }
    public string TokenHash { get; set; } = string.Empty;   // SHA-256 of raw token
    public DateTime ExpiresAt { get; set; }
    public bool IsRevoked { get; set; }
    public string DeviceInfo { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
