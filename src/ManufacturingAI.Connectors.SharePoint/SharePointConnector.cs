using Azure.Core;
using Azure.Identity;
using ManufacturingAI.Core.Interfaces;
using ManufacturingAI.Core.Models;
using ManufacturingAI.Infrastructure.Repositories;
using ManufacturingAI.Infrastructure.Security;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ManufacturingAI.Connectors.SharePoint;

public sealed class SharePointConnector(
    ISyncStateRepository syncStateRepository,
    IEncryptionService encryption,
    IHttpClientFactory httpClientFactory,
    ILogger<SharePointConnector> logger) : IKnowledgeConnector
{
    public string ConnectorType => "sharepoint";

    private const string GraphBase = "https://graph.microsoft.com/v1.0";
    private const string GraphScope = "https://graph.microsoft.com/.default";

    // Reserved SyncState row (per connector) that stores the Graph delta token in VersionHash.
    // Same pattern as Google Drive's __drive_page_token__ — the "__" prefix marks it internal
    // so IngestService.GetSyncStatusAsync filters it out of user-facing aggregates.
    private const string DeltaTokenSourceId = "__sharepoint_delta_token__";

    // SharePoint Online stores Office files natively; no export needed (unlike Google Workspace).
    // Mirror the extension set the rest of the pipeline knows how to parse.
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

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    // ── TestConnectionAsync ──────────────────────────────────

    public async Task<ConnectorTestResult> TestConnectionAsync(ConnectorConfig config, CancellationToken ct = default)
    {
        SharePointConnectorSettings settings;
        try
        {
            settings = Deserialize(config);
        }
        catch (Exception ex)
        {
            return new ConnectorTestResult(false, $"Invalid connector settings: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(settings.TenantId))
            return new ConnectorTestResult(false, "TenantId is not configured.");
        if (string.IsNullOrWhiteSpace(settings.ClientId))
            return new ConnectorTestResult(false, "ClientId is not configured.");
        if (string.IsNullOrWhiteSpace(settings.ClientSecret))
            return new ConnectorTestResult(false, "ClientSecret is not configured.");
        if (string.IsNullOrWhiteSpace(settings.SiteUrl))
            return new ConnectorTestResult(false, "SiteUrl is not configured.");

        HttpClient http;
        try
        {
            http = await CreateAuthorizedClientAsync(settings, ct);
        }
        catch (Exception ex)
        {
            return new ConnectorTestResult(false, $"Could not acquire token: {ex.Message}");
        }

        try
        {
            var site = await ResolveSiteAsync(http, settings.SiteUrl, ct);
            if (site is null)
                return new ConnectorTestResult(false,
                    $"Site not found at '{settings.SiteUrl}'. Confirm the URL and that the app has Sites.Read.All.");

            var drive = await ResolveDriveAsync(http, site.Id, settings.DriveName, ct);
            if (drive is null)
                return new ConnectorTestResult(false,
                    string.IsNullOrWhiteSpace(settings.DriveName)
                        ? $"Could not resolve a default drive for site '{site.DisplayName ?? site.Id}'."
                        : $"Drive '{settings.DriveName}' not found on site '{site.DisplayName ?? site.Id}'.");

            // Quick reachability/quota sanity check — count root children.
            var children = await http.GetFromJsonAsync<GraphCollection<GraphDriveItem>>(
                $"{GraphBase}/drives/{drive.Id}/root/children?$select=id&$top=200",
                JsonOpts, ct);
            var count = children?.Value?.Count ?? 0;

            return new ConnectorTestResult(true,
                $"Connection OK. Site '{site.DisplayName ?? site.Id}', drive '{drive.Name ?? drive.Id}', {count} item(s) at root.");
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            return new ConnectorTestResult(false,
                "Forbidden by Graph. Ensure the app has Sites.Read.All and Files.Read.All (Application) granted with admin consent.");
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
        // `since` ignored — the persisted Graph delta token below is the authoritative cursor.
        var settings = Deserialize(config);
        var maxBytes = (long)settings.MaxFileSizeMB * 1_048_576L;

        HttpClient http;
        try
        {
            http = await CreateAuthorizedClientAsync(settings, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SharePoint: token acquisition failed for connector {ConnectorId}", config.Id);
            return ConnectorDelta.Empty;
        }

        GraphSite? site;
        GraphDrive? drive;
        try
        {
            site = await ResolveSiteAsync(http, settings.SiteUrl, ct);
            if (site is null) { logger.LogError("SharePoint: site '{SiteUrl}' not found", settings.SiteUrl); return ConnectorDelta.Empty; }
            drive = await ResolveDriveAsync(http, site.Id, settings.DriveName, ct);
            if (drive is null) { logger.LogError("SharePoint: drive could not be resolved for site {SiteId}", site.Id); return ConnectorDelta.Empty; }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SharePoint: site/drive resolution failed for connector {ConnectorId}", config.Id);
            return ConnectorDelta.Empty;
        }

        var syncStates = (await syncStateRepository.GetByConnectorAsync(config.Id, ct)).ToList();
        var tokenRow = syncStates.FirstOrDefault(s => s.SourceId == DeltaTokenSourceId);
        var startUrl = string.IsNullOrWhiteSpace(tokenRow?.VersionHash)
            ? $"{GraphBase}/drives/{drive.Id}/root/delta"
            : tokenRow!.VersionHash;       // saved deltaLink — full URL with token

        var changedFiles = new List<GraphDriveItem>();
        var deletedIds = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? newDeltaLink = null;

        try
        {
            var url = startUrl;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var page = await http.GetFromJsonAsync<GraphDeltaResponse>(url, JsonOpts, ct)
                    ?? throw new InvalidOperationException("Empty delta response.");

                foreach (var item in page.Value ?? [])
                {
                    if (string.IsNullOrEmpty(item.Id))
                        continue;
                    if (item.Deleted is not null)
                    {
                        // Deleted folders are reported too; harmless — the ingest side ignores
                        // SourceIds it never indexed.
                        deletedIds.Add(item.Id);
                        continue;
                    }
                    if (item.Folder is not null || item.File is null)
                        continue;
                    if (!seen.Add(item.Id))
                        continue;
                    changedFiles.Add(item);
                }

                if (!string.IsNullOrEmpty(page.NextLink))
                {
                    url = page.NextLink;
                    continue;
                }
                newDeltaLink = page.DeltaLink;
                break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SharePoint: delta query failed for connector {ConnectorId}", config.Id);
            return ConnectorDelta.Empty;
        }

        // An item deleted then restored inside the same window surfaces in both sets — the live
        // record wins.
        deletedIds.ExceptWith(seen);

        if (!string.IsNullOrEmpty(newDeltaLink))
            await PersistDeltaLinkAsync(config, newDeltaLink, ct);

        // First-run de-dup: skip files already indexed at the same content tag. Saves bandwidth when
        // a connector seeded under an old cursor is reconnected to a fresh one without losing state.
        var knownHashes = syncStates
            .Where(s => s.Status == SyncStatus.Completed)
            .ToDictionary(s => s.SourceId, s => s.VersionHash, StringComparer.Ordinal);

        var toFetch = changedFiles
            .Where(f => !(knownHashes.TryGetValue(f.Id!, out var stored) && stored == ComputeVersionHash(f)))
            .ToList();

        logger.LogInformation(
            "SharePointConnector [{ConnectorId}] delta: {Changed} changed, {Deleted} deleted / {Total} reported; cursor advanced.",
            config.Id, toFetch.Count, deletedIds.Count, changedFiles.Count);

        return new ConnectorDelta(BuildDocuments(http, drive.Id, toFetch, maxBytes, ct), deletedIds);
    }

    // ── Document materialization (lazy: stream pulled per file as consumer enumerates) ──

    private IEnumerable<SourceDocument> BuildDocuments(
        HttpClient http, string driveId, IReadOnlyList<GraphDriveItem> files, long maxBytes, CancellationToken ct)
    {
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var doc = TryBuildDocument(http, driveId, file, maxBytes);
            if (doc is not null)
                yield return doc;
        }
    }

    private SourceDocument? TryBuildDocument(HttpClient http, string driveId, GraphDriveItem item, long maxBytes)
    {
        try
        {
            var ext = Path.GetExtension(item.Name ?? string.Empty);
            if (!ExtensionMime.TryGetValue(ext, out var parserMime))
            {
                logger.LogDebug("Skipping unsupported file {Name} ({Mime})", item.Name, item.File?.MimeType);
                return null;
            }
            if (item.Size is long size && size > maxBytes)
            {
                logger.LogDebug("Skipping {Name}: {MB:F1} MB exceeds limit", item.Name, size / 1_048_576.0);
                return null;
            }

            var content = DownloadContent(http, driveId, item.Id!);
            return new SourceDocument(
                SourceId: item.Id!,
                Title: item.Name ?? item.Id!,
                Content: content,
                MimeType: parserMime,
                VersionHash: ComputeVersionHash(item),
                LastModified: item.LastModifiedDateTime ?? DateTimeOffset.UtcNow,
                Metadata: new Dictionary<string, string>
                {
                    ["sharePointItemId"] = item.Id!,
                    ["sourceMimeType"] = item.File?.MimeType ?? string.Empty,
                    ["webUrl"] = item.WebUrl ?? string.Empty,
                });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Skipping SharePoint item {Id} ({Name}) — could not download", item.Id, item.Name);
            return null;
        }
    }

    private static MemoryStream DownloadContent(HttpClient http, string driveId, string itemId)
    {
        // Use sync-over-async deliberately: matches Google Drive's lazy-download shape (caller pulls
        // one stream at a time via the IEnumerable, SyncSchedulerJob disposes it before the next).
        using var resp = http.GetAsync(
            $"{GraphBase}/drives/{driveId}/items/{itemId}/content",
            HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        resp.EnsureSuccessStatusCode();
        var ms = new MemoryStream();
        resp.Content.CopyToAsync(ms).GetAwaiter().GetResult();
        ms.Position = 0;
        return ms;
    }

    // cTag changes on every content change; eTag also covers metadata. Hashes (quickXorHash on
    // SharePoint, sha256 on OneDrive) are most stable but not always populated for older files.
    private static string ComputeVersionHash(GraphDriveItem item) =>
        item.File?.Hashes?.QuickXorHash
        ?? item.File?.Hashes?.Sha256Hash
        ?? item.File?.Hashes?.Sha1Hash
        ?? item.CTag
        ?? item.ETag
        ?? string.Empty;

    // ── Site / drive resolution ──────────────────────────────

    private async Task<GraphSite?> ResolveSiteAsync(HttpClient http, string siteUrl, CancellationToken ct)
    {
        // Graph site lookup uses "{hostname}:/{path}" as the locator.
        if (!Uri.TryCreate(siteUrl.Trim(), UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"SiteUrl '{siteUrl}' is not a valid absolute URL.");

        var locator = $"{uri.Host}:{uri.AbsolutePath.TrimEnd('/')}";
        var resp = await http.GetAsync($"{GraphBase}/sites/{locator}", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<GraphSite>(JsonOpts, ct);
    }

    private async Task<GraphDrive?> ResolveDriveAsync(HttpClient http, string siteId, string driveName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(driveName))
        {
            // Default drive (typically the main "Documents" library).
            var resp = await http.GetAsync($"{GraphBase}/sites/{siteId}/drive", ct);
            if (resp.StatusCode == HttpStatusCode.NotFound) return null;
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadFromJsonAsync<GraphDrive>(JsonOpts, ct);
        }

        var drives = await http.GetFromJsonAsync<GraphCollection<GraphDrive>>(
            $"{GraphBase}/sites/{siteId}/drives", JsonOpts, ct);
        return drives?.Value?.FirstOrDefault(d =>
            string.Equals(d.Name, driveName, StringComparison.OrdinalIgnoreCase));
    }

    // ── Cursor persistence ───────────────────────────────────

    private async Task PersistDeltaLinkAsync(ConnectorConfig config, string deltaLink, CancellationToken ct)
    {
        var result = await syncStateRepository.UpsertAsync(new SyncState
        {
            Id = Guid.NewGuid(),
            TenantId = config.TenantId,
            ConnectorId = config.Id,
            SourceId = DeltaTokenSourceId,
            VersionHash = deltaLink,        // store the full @odata.deltaLink URL
            Status = SyncStatus.Completed,
            LastSyncedAt = DateTime.UtcNow,
            ErrorMessage = string.Empty,
        }, ct);

        if (!result.Success)
            logger.LogWarning("SharePoint: failed to persist delta link for connector {ConnectorId}: {Error}",
                config.Id, result.Error);
    }

    // ── Auth + HTTP client ───────────────────────────────────

    private async Task<HttpClient> CreateAuthorizedClientAsync(SharePointConnectorSettings settings, CancellationToken ct)
    {
        var credential = new ClientSecretCredential(
            settings.TenantId.Trim(), settings.ClientId.Trim(), settings.ClientSecret);

        var token = await credential.GetTokenAsync(
            new TokenRequestContext(new[] { GraphScope }), ct);

        var http = httpClientFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }

    // ── Settings load ────────────────────────────────────────

    private SharePointConnectorSettings Deserialize(ConnectorConfig config)
    {
        var json = encryption.Decrypt(config.SettingsJson);
        return JsonSerializer.Deserialize<SharePointConnectorSettings>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("SharePointConnectorSettings JSON is invalid.");
    }

    // ── Graph response DTOs (minimal — only the fields we read) ──

    private sealed class GraphDeltaResponse
    {
        [JsonPropertyName("value")] public List<GraphDriveItem>? Value { get; set; }
        [JsonPropertyName("@odata.nextLink")] public string? NextLink { get; set; }
        [JsonPropertyName("@odata.deltaLink")] public string? DeltaLink { get; set; }
    }

    private sealed class GraphCollection<T>
    {
        [JsonPropertyName("value")] public List<T>? Value { get; set; }
    }

    private sealed class GraphSite
    {
        public string Id { get; set; } = string.Empty;
        public string? DisplayName { get; set; }
    }

    private sealed class GraphDrive
    {
        public string Id { get; set; } = string.Empty;
        public string? Name { get; set; }
    }

    private sealed class GraphDriveItem
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public long? Size { get; set; }
        public DateTimeOffset? LastModifiedDateTime { get; set; }
        public string? WebUrl { get; set; }
        public string? ETag { get; set; }
        public string? CTag { get; set; }
        public GraphFile? File { get; set; }
        public GraphFolder? Folder { get; set; }
        public GraphDeleted? Deleted { get; set; }
    }

    private sealed class GraphFile
    {
        public string? MimeType { get; set; }
        public GraphHashes? Hashes { get; set; }
    }

    private sealed class GraphHashes
    {
        public string? QuickXorHash { get; set; }
        public string? Sha1Hash { get; set; }
        public string? Sha256Hash { get; set; }
    }

    private sealed class GraphFolder
    {
        public int? ChildCount { get; set; }
    }

    private sealed class GraphDeleted
    {
        public string? State { get; set; }
    }
}
