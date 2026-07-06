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
using Change = Google.Apis.Drive.v3.Data.Change;
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

    // Reserved SyncState row (per connector) that stores the Drive changes cursor in its
    // VersionHash. The "__" prefix marks it as internal bookkeeping so status aggregation
    // (IngestService.GetSyncStatusAsync) skips it; the value never collides with a real file ID.
    private const string PageTokenSourceId = "__drive_page_token__";

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

    public async Task<ConnectorDelta> FetchDeltaAsync(
        ConnectorConfig config,
        DateTimeOffset? since,
        CancellationToken ct = default)
    {
        // `since` is intentionally ignored: the persisted Drive changes page token (below) is the
        // authoritative incremental cursor, so a wall-clock timestamp would only be redundant.
        var settings = Deserialize(config);
        var service = CreateService(settings);
        var maxBytes = (long)settings.MaxFileSizeMB * 1_048_576L;

        var syncStates = (await syncStateRepository.GetByConnectorAsync(config.Id, ct)).ToList();
        var tokenRow = syncStates.FirstOrDefault(s => s.SourceId == PageTokenSourceId);

        // No stored cursor → first run: capture a start token, persist it, then seed via a full crawl.
        if (string.IsNullOrWhiteSpace(tokenRow?.VersionHash))
            return await SeedAsync(service, settings, config, syncStates, maxBytes, ct);

        // Stored cursor present → ask Drive only for what changed since, and advance the cursor.
        return await FetchChangesAsync(service, settings, config, tokenRow.VersionHash, maxBytes, ct);
    }

    // First sync: record where "now" is in Drive's change stream, then index everything currently
    // in the folder. The start token is captured and persisted *before* the crawl so edits made
    // during the (potentially long) seed are still caught by the next incremental run.
    private async Task<ConnectorDelta> SeedAsync(
        DriveService service, GoogleDriveConnectorSettings settings, ConnectorConfig config,
        IReadOnlyList<SyncState> syncStates, long maxBytes, CancellationToken ct)
    {
        try
        {
            var tokenReq = service.Changes.GetStartPageToken();
            tokenReq.SupportsAllDrives = true;
            var startToken = (await tokenReq.ExecuteAsync(ct)).StartPageTokenValue;
            await PersistPageTokenAsync(config, startToken, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GoogleDrive: could not obtain start page token for connector {ConnectorId}", config.Id);
            return ConnectorDelta.Empty;
        }

        List<DriveFile> files;
        try
        {
            files = await EnumerateFilesAsync(service, settings, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GoogleDrive enumeration failed for folder {RootFolderId}", settings.RootFolderId);
            return ConnectorDelta.Empty;
        }

        // Skip files already indexed at the same hash. Irrelevant on a genuine first run (no rows),
        // but lets a connector upgraded from the old full-crawl behaviour seed without re-downloading
        // everything it already has. md5Checksum / version come from listing metadata, so unchanged
        // files are skipped without any content download.
        var knownHashes = syncStates
            .Where(s => s.Status == SyncStatus.Completed)
            .ToDictionary(s => s.SourceId, s => s.VersionHash, StringComparer.Ordinal);

        var changed = files
            .Where(f => !(knownHashes.TryGetValue(f.Id, out var stored) && stored == ComputeVersionHash(f)))
            .ToList();

        // Files we indexed under an older cursor that no longer show up in the crawl were deleted
        // (or moved out of scope) while no valid change cursor was tracking them.
        var presentIds = files.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        var deleted = syncStates
            .Where(s => !s.SourceId.StartsWith("__", StringComparison.Ordinal)
                        && !presentIds.Contains(s.SourceId))
            .Select(s => s.SourceId)
            .ToList();

        logger.LogInformation(
            "GoogleDriveConnector [{ConnectorId}] seed: {Changed} new, {Deleted} deleted / {Total} file(s); start token stored.",
            config.Id, changed.Count, deleted.Count, files.Count);

        // Lazily materialize each document: content is downloaded only as the consumer pulls it
        // (SyncSchedulerJob disposes each stream before moving on), so memory stays at ~one file.
        return new ConnectorDelta(BuildDocuments(service, changed, maxBytes, ct), deleted);
    }

    // Incremental sync: pull Drive's change feed from the stored cursor and map the added/modified
    // files in scope plus the IDs of removed/trashed files. Removed changes carry no file (and
    // trashed ones may lack parents), so deletions skip the scope check — the ingest side ignores
    // SourceIds it never indexed.
    private async Task<ConnectorDelta> FetchChangesAsync(
        DriveService service, GoogleDriveConnectorSettings settings, ConnectorConfig config,
        string pageToken, long maxBytes, CancellationToken ct)
    {
        var changedFiles = new List<DriveFile>();
        var deletedIds = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var scopeCache = new Dictionary<string, bool>(StringComparer.Ordinal);
        string? newStartToken;

        try
        {
            var token = pageToken;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var req = service.Changes.List(token);
                req.Fields =
                    $"nextPageToken, newStartPageToken, changes(fileId, removed, file({FileFields}, trashed, parents))";
                req.PageSize = 1000;
                req.SupportsAllDrives = true;
                req.IncludeItemsFromAllDrives = true;
                req.Spaces = "drive";

                var resp = await req.ExecuteAsync(ct);

                foreach (var change in resp.Changes ?? Enumerable.Empty<Change>())
                {
                    var file = change.File;
                    if (change.Removed == true || file?.Trashed == true)
                    {
                        var id = change.FileId ?? file?.Id;
                        if (!string.IsNullOrEmpty(id))
                            deletedIds.Add(id);
                        continue;
                    }
                    if (file is null || file.MimeType == FolderMime)
                        continue;
                    if (!seen.Add(file.Id))               // a file can appear in several change records
                        continue;
                    if (!await IsWithinRootAsync(service, file, settings, scopeCache, ct))
                        continue;

                    changedFiles.Add(file);
                }

                // Drive returns NextPageToken until the last page, then NewStartPageToken (the
                // cursor to store for next time).
                if (!string.IsNullOrEmpty(resp.NextPageToken))
                {
                    token = resp.NextPageToken;
                    continue;
                }
                newStartToken = resp.NewStartPageToken;
                break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GoogleDrive changes.list failed for connector {ConnectorId}", config.Id);
            return ConnectorDelta.Empty;
        }

        // A file trashed then restored inside the same window surfaces in both sets — the live
        // record wins.
        deletedIds.ExceptWith(changedFiles.Select(f => f.Id));

        // Advance the cursor once the whole change set is collected. Downloads still happen lazily
        // below; a per-file download failure is skipped (as in the seed crawl), so we deliberately
        // do not gate the cursor on download success.
        if (!string.IsNullOrEmpty(newStartToken))
            await PersistPageTokenAsync(config, newStartToken, ct);

        logger.LogInformation(
            "GoogleDriveConnector [{ConnectorId}] delta: {Count} changed, {Deleted} deleted file(s); cursor advanced.",
            config.Id, changedFiles.Count, deletedIds.Count);

        return new ConnectorDelta(BuildDocuments(service, changedFiles, maxBytes, ct), deletedIds);
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

    // ── Change cursor + scope helpers ────────────────────────

    // Persists (upserts) the Drive changes cursor into the reserved sentinel SyncState row.
    private async Task PersistPageTokenAsync(ConnectorConfig config, string token, CancellationToken ct)
    {
        var result = await syncStateRepository.UpsertAsync(new SyncState
        {
            Id = Guid.NewGuid(),
            TenantId = config.TenantId,
            ConnectorId = config.Id,
            SourceId = PageTokenSourceId,
            VersionHash = token,
            Status = SyncStatus.Completed,
            LastSyncedAt = DateTime.UtcNow,
            ErrorMessage = string.Empty,
        }, ct);

        if (!result.Success)
            logger.LogWarning("GoogleDrive: failed to persist page token for connector {ConnectorId}: {Error}",
                config.Id, result.Error);
    }

    // True when the file lives inside the configured root folder (or, when IncludeSubfolders is on,
    // anywhere beneath it). The change feed spans the whole drive, so this filters out files the
    // service account can see that aren't part of this connector's folder.
    private async Task<bool> IsWithinRootAsync(
        DriveService service, DriveFile file, GoogleDriveConnectorSettings settings,
        Dictionary<string, bool> scopeCache, CancellationToken ct)
    {
        foreach (var parent in file.Parents ?? Enumerable.Empty<string>())
        {
            if (await IsFolderWithinRootAsync(service, parent, settings, scopeCache, ct))
                return true;
        }
        return false;
    }

    // Walks a folder's ancestry (following the first parent each step) until it reaches the root
    // folder or runs out. Results for every folder on the path are memoized in scopeCache so a
    // deep tree is resolved at most once per sync.
    private async Task<bool> IsFolderWithinRootAsync(
        DriveService service, string folderId, GoogleDriveConnectorSettings settings,
        Dictionary<string, bool> scopeCache, CancellationToken ct)
    {
        if (folderId == settings.RootFolderId) return true;
        if (!settings.IncludeSubfolders) return false;
        if (scopeCache.TryGetValue(folderId, out var cached)) return cached;

        var path = new List<string>();
        var current = folderId;
        bool result = false;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (current == settings.RootFolderId) { result = true; break; }
            if (scopeCache.TryGetValue(current, out var c)) { result = c; break; }
            path.Add(current);

            DriveFile folder;
            try
            {
                var req = service.Files.Get(current);
                req.Fields = "id, parents";
                req.SupportsAllDrives = true;
                folder = await req.ExecuteAsync(ct);
            }
            catch
            {
                break; // unreachable parent → treat as out of scope
            }

            var parents = folder.Parents;
            if (parents is null || parents.Count == 0) break; // reached a root we don't own
            current = parents[0];
        }

        foreach (var f in path) scopeCache[f] = result;
        return result;
    }

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
