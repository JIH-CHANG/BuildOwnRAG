using Google.Apis.Auth.OAuth2;
using Google.Apis.Download;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using ManufacturingAI.Core.Interfaces;
using ManufacturingAI.Core.Models;
using ManufacturingAI.Infrastructure.Repositories;
using ManufacturingAI.Infrastructure.Security;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace ManufacturingAI.Connectors.GoogleDrive;

public sealed class GoogleDriveConnector(
    ISyncStateRepository syncStateRepository,
    IEncryptionService encryption,
    ILogger<GoogleDriveConnector> logger) : IKnowledgeConnector
{
    public string ConnectorType => "googledrive";

    private const string ApplicationName = "ManufacturingAI RAG";
    private const string FolderMime = "application/vnd.google-apps.folder";

    // Drive query fragment: exclude trashed items and folders themselves.
    private const string NotTrashedFiles = "trashed = false and mimeType != '" + FolderMime + "'";

    // Fields requested for every listed file (keep in sync with what TryBuildDocument reads).
    private const string FileFields =
        "id, name, mimeType, modifiedTime, size, version, md5Checksum, webViewLink";

    // Binary (downloadable) files we can parse, keyed by extension → the MIME the parser expects.
    private static readonly Dictionary<string, string> ExtensionMime = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".csv"] = "text/csv",
        [".txt"] = "text/plain",
        [".md"] = "text/markdown",
        [".markdown"] = "text/markdown",
    };

    // Google-native types → (export MIME requested from Drive, MIME the parser expects).
    private static readonly Dictionary<string, (string ExportMime, string ParserMime)> NativeExport = new()
    {
        ["application/vnd.google-apps.document"] =
            ("application/vnd.openxmlformats-officedocument.wordprocessingml.document",
             "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
        ["application/vnd.google-apps.spreadsheet"] =
            ("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
             "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
        ["application/vnd.google-apps.presentation"] =
            ("application/pdf", "application/pdf"),
    };

    // ── TestConnectionAsync ──────────────────────────────────

    public async Task<ConnectorTestResult> TestConnectionAsync(ConnectorConfig config, CancellationToken ct = default)
    {
        GoogleDriveConnectorSettings settings;
        try
        {
            settings = Deserialize(config);
        }
        catch (Exception ex)
        {
            return new ConnectorTestResult(false, $"Invalid connector settings: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(settings.ServiceAccountJson))
            return new ConnectorTestResult(false, "ServiceAccountJson is not configured.");
        if (string.IsNullOrWhiteSpace(settings.RootFolderId))
            return new ConnectorTestResult(false, "RootFolderId is not configured.");

        DriveService service;
        try
        {
            service = CreateService(settings);
        }
        catch (Exception ex)
        {
            return new ConnectorTestResult(false, $"Could not load service account credentials: {ex.Message}");
        }

        try
        {
            // 1. Confirm the shared folder is reachable (and really is a folder).
            var folderReq = service.Files.Get(settings.RootFolderId);
            folderReq.Fields = "id, name, mimeType";
            folderReq.SupportsAllDrives = true;
            var folder = await folderReq.ExecuteAsync(ct);

            if (folder.MimeType != FolderMime)
                return new ConnectorTestResult(false,
                    $"RootFolderId '{settings.RootFolderId}' is not a folder (mimeType: {folder.MimeType}).");

            // 2. Count matching files directly in the folder as a quick reachability/quota check.
            var listReq = service.Files.List();
            listReq.Q = $"'{settings.RootFolderId}' in parents and {NotTrashedFiles}";
            listReq.Fields = "files(id)";
            listReq.PageSize = 1000;
            listReq.SupportsAllDrives = true;
            listReq.IncludeItemsFromAllDrives = true;
            var list = await listReq.ExecuteAsync(ct);

            var count = list.Files?.Count ?? 0;
            return new ConnectorTestResult(true,
                $"Connection OK. Folder '{folder.Name}' reachable, {count} file(s) found at top level"
                + (settings.IncludeSubfolders ? " (subfolders included on sync)." : "."));
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new ConnectorTestResult(false,
                $"Folder '{settings.RootFolderId}' not found. Confirm the ID and that it is shared with the "
                + "service account email.");
        }
        catch (Exception ex)
        {
            return new ConnectorTestResult(false, ex.Message);
        }
    }

    // ── FetchDeltaAsync ──────────────────────────────────────

    public async Task<IEnumerable<SourceDocument>> FetchDeltaAsync(
        ConnectorConfig config,
        DateTimeOffset? since,
        CancellationToken ct = default)
    {
        // Full metadata crawl of the folder (`since` is not used — the Drive changes API +
        // stored page token for true incremental listing is future work). Listing returns only
        // metadata, so the crawl is cheap; the expensive part is downloading content, which the
        // hash filter below avoids for unchanged files.
        var settings = Deserialize(config);
        var service = CreateService(settings);
        var maxBytes = (long)settings.MaxFileSizeMB * 1_048_576L;

        List<DriveFile> files;
        try
        {
            files = await EnumerateFilesAsync(service, settings, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GoogleDrive enumeration failed for folder {RootFolderId}", settings.RootFolderId);
            return [];
        }

        // Hash lookup of already-indexed files (ConnectorId-scoped). md5Checksum / version come
        // straight from the listing metadata, so an unchanged file is skipped without ever
        // downloading its content.
        var syncStates = await syncStateRepository.GetByConnectorAsync(config.Id, ct);
        var knownHashes = syncStates
            .Where(s => s.Status == SyncStatus.Completed)
            .ToDictionary(s => s.SourceId, s => s.VersionHash, StringComparer.Ordinal);

        var changed = files
            .Where(f => !(knownHashes.TryGetValue(f.Id, out var stored) && stored == ComputeVersionHash(f)))
            .ToList();

        logger.LogInformation(
            "GoogleDriveConnector [{ConnectorId}]: {Changed} changed / {Total} candidate file(s)",
            config.Id, changed.Count, files.Count);

        // Lazily materialize each changed document: content is downloaded only as the consumer
        // pulls it (SyncSchedulerJob disposes each stream before moving on), so memory stays at
        // ~one file.
        return BuildDocuments(service, changed, maxBytes, ct);
    }

    // ── Crawl ────────────────────────────────────────────────

    private async Task<List<DriveFile>> EnumerateFilesAsync(
        DriveService service, GoogleDriveConnectorSettings settings, CancellationToken ct)
    {
        var results = new List<DriveFile>();
        var folderQueue = new Queue<string>();
        folderQueue.Enqueue(settings.RootFolderId);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        while (folderQueue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var folderId = folderQueue.Dequeue();
            if (!visited.Add(folderId))
                continue;

            string? pageToken = null;
            do
            {
                var req = service.Files.List();
                req.Q = $"'{folderId}' in parents and trashed = false";
                req.Fields = $"nextPageToken, files({FileFields})";
                req.PageSize = 1000;
                req.PageToken = pageToken;
                req.SupportsAllDrives = true;
                req.IncludeItemsFromAllDrives = true;

                var resp = await req.ExecuteAsync(ct);
                foreach (var f in resp.Files ?? Enumerable.Empty<DriveFile>())
                {
                    if (f.MimeType == FolderMime)
                    {
                        if (settings.IncludeSubfolders)
                            folderQueue.Enqueue(f.Id);
                    }
                    else
                    {
                        results.Add(f);
                    }
                }
                pageToken = resp.NextPageToken;
            }
            while (pageToken is not null);
        }

        return results;
    }

    private IEnumerable<SourceDocument> BuildDocuments(
        DriveService service, IReadOnlyList<DriveFile> files, long maxBytes, CancellationToken ct)
    {
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var doc = TryBuildDocument(service, file, maxBytes);
            if (doc is not null)
                yield return doc;
        }
    }

    private SourceDocument? TryBuildDocument(DriveService service, DriveFile file, long maxBytes)
    {
        try
        {
            bool isNative = file.MimeType?.StartsWith("application/vnd.google-apps.", StringComparison.Ordinal) == true;
            string parserMime;
            Stream content;

            if (isNative)
            {
                if (!NativeExport.TryGetValue(file.MimeType!, out var export))
                {
                    logger.LogDebug("Skipping unsupported Google-native file {Name} ({Mime})", file.Name, file.MimeType);
                    return null;
                }
                parserMime = export.ParserMime;
                content = DownloadExport(service, file.Id, export.ExportMime);
            }
            else
            {
                var ext = Path.GetExtension(file.Name ?? string.Empty);
                if (!ExtensionMime.TryGetValue(ext, out var mime))
                {
                    logger.LogDebug("Skipping unsupported file {Name} ({Mime})", file.Name, file.MimeType);
                    return null;
                }
                if (file.Size is long size && size > maxBytes)
                {
                    logger.LogDebug("Skipping {Name}: {MB:F1} MB exceeds limit", file.Name, size / 1_048_576.0);
                    return null;
                }
                parserMime = mime;
                content = DownloadMedia(service, file.Id);
            }

            return new SourceDocument(
                SourceId: file.Id,
                Title: file.Name ?? file.Id,
                Content: content,
                MimeType: parserMime,
                VersionHash: ComputeVersionHash(file),
                LastModified: file.ModifiedTimeDateTimeOffset ?? DateTimeOffset.UtcNow,
                Metadata: new Dictionary<string, string>
                {
                    ["driveFileId"] = file.Id,
                    ["sourceMimeType"] = file.MimeType ?? string.Empty,
                    ["webViewLink"] = file.WebViewLink ?? string.Empty
                });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Skipping Drive file {Id} ({Name}) — could not download", file.Id, file.Name);
            return null;
        }
    }

    // md5Checksum is content-stable for binary files; Google-native files have no md5,
    // so fall back to the monotonic `version` (changes on every server-side edit). Computed
    // from listing metadata alone — no content download required.
    private static string ComputeVersionHash(DriveFile file) =>
        !string.IsNullOrEmpty(file.Md5Checksum)
            ? file.Md5Checksum
            : file.Version?.ToString() ?? string.Empty;

    // ── Download helpers (synchronous: invoked lazily during consumer enumeration) ──

    private static MemoryStream DownloadMedia(DriveService service, string fileId)
    {
        var req = service.Files.Get(fileId);
        req.SupportsAllDrives = true;
        return Drain(ms => req.DownloadWithStatus(ms));
    }

    private static MemoryStream DownloadExport(DriveService service, string fileId, string exportMime)
    {
        var req = service.Files.Export(fileId, exportMime);
        return Drain(ms => req.DownloadWithStatus(ms));
    }

    private static MemoryStream Drain(Func<Stream, IDownloadProgress> download)
    {
        var ms = new MemoryStream();
        var progress = download(ms);
        if (progress.Status != DownloadStatus.Completed)
            throw progress.Exception ?? new IOException("Drive download did not complete.");
        ms.Position = 0;
        return ms;
    }

    // ── Helpers ──────────────────────────────────────────────

    private GoogleDriveConnectorSettings Deserialize(ConnectorConfig config)
    {
        var json = encryption.Decrypt(config.SettingsJson);
        var settings = JsonSerializer.Deserialize<GoogleDriveConnectorSettings>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("GoogleDriveConnectorSettings JSON is invalid.");

        // Tolerate a pasted folder URL (e.g. ".../folders/<id>?usp=sharing") in addition to a bare ID.
        settings.RootFolderId = NormalizeFolderId(settings.RootFolderId);
        return settings;
    }

    /// <summary>Extracts the folder ID from a full Drive URL, or returns the trimmed input as-is.</summary>
    private static string NormalizeFolderId(string raw)
    {
        var value = (raw ?? string.Empty).Trim();
        const string marker = "/folders/";
        var idx = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return value;

        var rest = value[(idx + marker.Length)..];
        var end = rest.IndexOfAny(['/', '?', '&', '#']);
        return end >= 0 ? rest[..end] : rest;
    }

    private static DriveService CreateService(GoogleDriveConnectorSettings settings)
    {
        var credential = GoogleCredential
            .FromJson(settings.ServiceAccountJson)
            .CreateScoped(DriveService.Scope.DriveReadonly);

        return new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = ApplicationName
        });
    }
}
