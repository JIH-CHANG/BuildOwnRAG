namespace ManufacturingAI.Connectors.Folder;

public class FolderConnectorSettings
{
    public string FolderPath { get; set; } = string.Empty;
    public string[] IncludeExtensions { get; set; } = [".pdf", ".docx", ".xlsx", ".csv", ".txt", ".md", ".markdown"];
    public bool IncludeSubfolders { get; set; } = true;
    public int MaxFileSizeMB { get; set; } = 50;

    // When true, a FileSystemWatcher monitors the folder and triggers immediate ingestion on change
    public bool WatchMode { get; set; } = false;
}
