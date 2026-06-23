namespace ManufacturingAI.Connectors.SharePoint;

public class SharePointConnectorSettings
{
    /// <summary>Microsoft Entra ID (Azure AD) tenant ID — GUID or "contoso.onmicrosoft.com".</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Application (client) ID of the Entra app registration.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Client secret of the Entra app. Stored AES-encrypted at rest.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// Full SharePoint site URL, e.g. "https://contoso.sharepoint.com/sites/marketing".
    /// The connector resolves this to a Graph site ID at sync time.
    /// </summary>
    public string SiteUrl { get; set; } = string.Empty;

    /// <summary>
    /// Optional document library display name (e.g. "Documents", "Shared Documents").
    /// Empty means use the site's default drive (typically the main document library).
    /// </summary>
    public string DriveName { get; set; } = string.Empty;

    /// <summary>Files larger than this are skipped.</summary>
    public int MaxFileSizeMB { get; set; } = 50;
}
