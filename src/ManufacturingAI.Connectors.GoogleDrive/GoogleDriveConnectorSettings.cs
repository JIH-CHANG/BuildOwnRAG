namespace ManufacturingAI.Connectors.GoogleDrive;

public class GoogleDriveConnectorSettings
{
    /// <summary>Raw service-account key file contents (the downloaded JSON). Stored AES-encrypted at rest.</summary>
    public string ServiceAccountJson { get; set; } = string.Empty;

    /// <summary>ID of the Drive folder shared with the service account; crawling is scoped to this folder.</summary>
    public string RootFolderId { get; set; } = string.Empty;

    /// <summary>When true, files in nested subfolders of <see cref="RootFolderId"/> are included.</summary>
    public bool IncludeSubfolders { get; set; } = true;

    /// <summary>Files larger than this are skipped.</summary>
    public int MaxFileSizeMB { get; set; } = 50;
}
