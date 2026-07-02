namespace ManufacturingAI.Connectors.Confluence;

public class ConfluenceConnectorSettings
{
    /// <summary>
    /// Instance base URL. Cloud: https://yourcompany.atlassian.net (the /wiki context path is
    /// appended automatically). Server/Data Center: https://confluence.yourcompany.com.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Atlassian account email. Required for Cloud (Basic auth: email + API token).
    /// Leave empty for Server/Data Center, where ApiToken is sent as a Bearer PAT.
    /// </summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Cloud API token or Server/Data Center personal access token. Stored encrypted.</summary>
    public string ApiToken { get; set; } = string.Empty;

    /// <summary>Comma-separated space keys to sync (e.g. "ENG,QA"). Empty = all accessible spaces.</summary>
    public string SpaceKeys { get; set; } = string.Empty;

    /// <summary>
    /// Comma-separated page IDs to sync. Each listed page plus all of its descendant pages
    /// (and their attachments) are included. Empty = no page filter. Combines with SpaceKeys
    /// as an intersection when both are set.
    /// </summary>
    public string PageIds { get; set; } = string.Empty;

    /// <summary>Also sync page attachments with parseable extensions (pdf, docx, xlsx, csv, txt, md).</summary>
    public bool IncludeAttachments { get; set; } = true;

    public int MaxFileSizeMB { get; set; } = 50;
}
