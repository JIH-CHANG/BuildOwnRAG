using ManufacturingAI.Core.Interfaces;
using ManufacturingAI.Core.Models;
using ManufacturingAI.Infrastructure.Repositories;
using ManufacturingAI.Infrastructure.Security;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ManufacturingAI.Connectors.Confluence;

public sealed class ConfluenceConnector(
    ISyncStateRepository syncStateRepository,
    IEncryptionService encryption,
    IHttpClientFactory httpClientFactory,
    ILogger<ConfluenceConnector> logger) : IKnowledgeConnector
{
    public string ConnectorType => "confluence";

    // Reserved SyncState row (per connector) that stores the last successful sync start time
    // (ISO 8601, UTC) in VersionHash. Confluence has no delta/changes API, so incremental sync
    // is a CQL "lastmodified >=" query against this timestamp. The "__" prefix marks the row
    // internal so IngestService.GetSyncStatusAsync filters it out of user-facing aggregates.
    private const string CursorSourceId = "__confluence_cursor__";

    // CQL date literals are evaluated in the API user's configured timezone, which we cannot
    // see. UTC-12 is the most-behind offset, so padding the cursor back 13 hours guarantees no
    // window is missed regardless of that setting; version-number dedup absorbs the overlap
    // cheaply because re-reported pages are filtered before their bodies are downloaded.
    private static readonly TimeSpan CursorPad = TimeSpan.FromHours(13);

    // Attachment extensions the rest of the pipeline knows how to parse (same set as SharePoint).
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
        ConfluenceConnectorSettings settings;
        try
        {
            settings = Deserialize(config);
        }
        catch (Exception ex)
        {
            return new ConnectorTestResult(false, $"Invalid connector settings: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(settings.BaseUrl))
            return new ConnectorTestResult(false, "BaseUrl is not configured.");
        if (string.IsNullOrWhiteSpace(settings.ApiToken))
            return new ConnectorTestResult(false, "ApiToken is not configured.");
        if (!Uri.TryCreate(settings.BaseUrl.Trim(), UriKind.Absolute, out _))
            return new ConnectorTestResult(false, $"BaseUrl '{settings.BaseUrl}' is not a valid absolute URL.");

        var apiBase = ResolveApiBase(settings);
        var http = CreateAuthorizedClient(settings);

        try
        {
            var spaces = await http.GetFromJsonAsync<ConfluenceCollection<ConfluenceSpace>>(
                $"{apiBase}/space?limit=25", JsonOpts, ct);
            var total = spaces?.Results?.Count ?? 0;

            var requestedKeys = ParseSpaceKeys(settings);
            foreach (var key in requestedKeys)
            {
                var resp = await http.GetAsync($"{apiBase}/space/{Uri.EscapeDataString(key)}", ct);
                if (resp.StatusCode == HttpStatusCode.NotFound)
                    return new ConnectorTestResult(false,
                        $"Space '{key}' was not found or the account has no access to it.");
                resp.EnsureSuccessStatusCode();
            }

            var requestedPages = ParsePageIds(settings);
            foreach (var id in requestedPages)
            {
                var resp = await http.GetAsync($"{apiBase}/content/{id}", ct);
                if (resp.StatusCode == HttpStatusCode.NotFound)
                    return new ConnectorTestResult(false,
                        $"Page {id} was not found or the account has no access to it.");
                resp.EnsureSuccessStatusCode();
            }

            var scope = (requestedKeys.Count, requestedPages.Count) switch
            {
                (> 0, > 0) => $"All {requestedKeys.Count} space(s) and {requestedPages.Count} page(s) are accessible.",
                (> 0, _) => $"All {requestedKeys.Count} configured space(s) are accessible.",
                (_, > 0) => $"All {requestedPages.Count} configured page(s) are accessible.",
                _ => $"{total} space(s) visible to this account.",
            };
            return new ConnectorTestResult(true, $"Connection OK. {scope}");
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new ConnectorTestResult(false,
                "Authentication rejected. For Confluence Cloud provide the account email plus an API token; " +
                "for Server/Data Center leave email empty and use a personal access token.");
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
        // `since` ignored — the persisted cursor row below is the authoritative watermark.
        var settings = Deserialize(config);
        var maxBytes = (long)settings.MaxFileSizeMB * 1_048_576L;
        var apiBase = ResolveApiBase(settings);
        var siteBase = apiBase[..^"/rest/api".Length];
        var http = CreateAuthorizedClient(settings);

        var syncStates = (await syncStateRepository.GetByConnectorAsync(config.Id, ct)).ToList();
        var cursorRow = syncStates.FirstOrDefault(s => s.SourceId == CursorSourceId);
        DateTimeOffset? cursor = null;
        if (DateTimeOffset.TryParse(cursorRow?.VersionHash, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal, out var parsed))
            cursor = parsed;

        // Captured before the queries run so edits landing mid-sync fall inside the next window.
        var syncStart = DateTimeOffset.UtcNow;
        var spaceKeys = ParseSpaceKeys(settings);
        var pageIds = ParsePageIds(settings);

        var changed = new List<ConfluenceContent>();
        try
        {
            await CollectSearchResultsAsync(http, siteBase, apiBase, BuildCql("page", spaceKeys, pageIds, cursor), changed, ct);
            if (settings.IncludeAttachments)
                await CollectSearchResultsAsync(http, siteBase, apiBase, BuildCql("attachment", spaceKeys, pageIds, cursor), changed, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Confluence: CQL search failed for connector {ConnectorId}", config.Id);
            return ConnectorDelta.Empty;
        }

        // Deletion detection: Confluence search simply stops returning deleted/trashed content, so
        // the only signal is a full in-scope ID listing reconciled against what we have indexed.
        // On a first sync (no cursor) the query above already listed everything.
        var deleted = new List<string>();
        try
        {
            var presentIds = new HashSet<string>(StringComparer.Ordinal);
            if (cursor is null)
            {
                presentIds.UnionWith(changed.Where(c => !string.IsNullOrEmpty(c.Id)).Select(c => c.Id!));
            }
            else
            {
                var all = new List<ConfluenceContent>();
                await CollectSearchResultsAsync(http, siteBase, apiBase, BuildCql("page", spaceKeys, pageIds, null), all, ct);
                if (settings.IncludeAttachments)
                    await CollectSearchResultsAsync(http, siteBase, apiBase, BuildCql("attachment", spaceKeys, pageIds, null), all, ct);
                presentIds.UnionWith(all.Where(c => !string.IsNullOrEmpty(c.Id)).Select(c => c.Id!));
            }

            deleted = syncStates
                .Where(s => !s.SourceId.StartsWith("__", StringComparison.Ordinal)
                            && !presentIds.Contains(s.SourceId))
                .Select(s => s.SourceId)
                .ToList();
        }
        catch (Exception ex)
        {
            // A failed listing must not look like "everything vanished" — skip deletions this sync.
            logger.LogWarning(ex, "Confluence: deletion reconciliation failed for connector {ConnectorId}", config.Id);
        }

        await PersistCursorAsync(config, syncStart, ct);

        // Skip content already indexed at the same version — CQL minute granularity plus the
        // timezone pad means unchanged items are re-reported on every sync.
        var knownHashes = syncStates
            .Where(s => s.Status == SyncStatus.Completed)
            .ToDictionary(s => s.SourceId, s => s.VersionHash, StringComparer.Ordinal);

        var toFetch = changed
            .Where(c => !string.IsNullOrEmpty(c.Id))
            .Where(c => !(knownHashes.TryGetValue(c.Id!, out var stored) && stored == ComputeVersionHash(c)))
            .ToList();

        logger.LogInformation(
            "ConfluenceConnector [{ConnectorId}] delta: {Changed} changed, {Deleted} deleted / {Total} reported; cursor advanced.",
            config.Id, toFetch.Count, deleted.Count, changed.Count);

        return new ConnectorDelta(BuildDocuments(http, siteBase, apiBase, toFetch, maxBytes, ct), deleted);
    }

    // ── Search (CQL) ─────────────────────────────────────────

    private static string BuildCql(string type, IReadOnlyList<string> spaceKeys, IReadOnlyList<string> pageIds, DateTimeOffset? cursor)
    {
        var sb = new StringBuilder($"type={type}");
        if (spaceKeys.Count > 0)
            sb.Append(" and space in (").Append(string.Join(",", spaceKeys.Select(k => $"\"{k}\""))).Append(')');
        if (pageIds.Count > 0)
        {
            // Page-tree filter: each listed page itself plus everything beneath it. `ancestor`
            // excludes the root page, so pages need `id in` as well; attachments hang off pages
            // via `container` (direct) while `ancestor` reaches the ones on descendant pages.
            var ids = string.Join(",", pageIds);
            sb.Append(type == "attachment"
                ? $" and (container in ({ids}) or ancestor in ({ids}))"
                : $" and (id in ({ids}) or ancestor in ({ids}))");
        }
        if (cursor is not null)
            sb.Append(" and lastmodified >= \"")
              .Append((cursor.Value - CursorPad).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
              .Append('"');
        sb.Append(" order by lastmodified asc");
        return sb.ToString();
    }

    private async Task CollectSearchResultsAsync(
        HttpClient http, string siteBase, string apiBase, string cql,
        List<ConfluenceContent> results, CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var url = $"{apiBase}/content/search?cql={Uri.EscapeDataString(cql)}&expand=version,space&limit=50";

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await http.GetFromJsonAsync<ConfluenceSearchResponse>(url, JsonOpts, ct)
                ?? throw new InvalidOperationException("Empty search response.");

            foreach (var item in page.Results ?? [])
            {
                if (string.IsNullOrEmpty(item.Id) || !seen.Add(item.Id))
                    continue;
                results.Add(item);
            }

            var next = page.Links?.Next;
            if (string.IsNullOrEmpty(next))
                break;
            // `next` is relative to the site root (Cloud: includes the /wiki context path).
            url = next.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? next : siteBase + next;
        }
    }

    // ── Document materialization (lazy: content pulled per item as consumer enumerates) ──

    private IEnumerable<SourceDocument> BuildDocuments(
        HttpClient http, string siteBase, string apiBase,
        IReadOnlyList<ConfluenceContent> items, long maxBytes, CancellationToken ct)
    {
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var doc = string.Equals(item.Type, "attachment", StringComparison.OrdinalIgnoreCase)
                ? TryBuildAttachmentDocument(http, siteBase, item, maxBytes)
                : TryBuildPageDocument(http, siteBase, apiBase, item);
            if (doc is not null)
                yield return doc;
        }
    }

    private SourceDocument? TryBuildPageDocument(HttpClient http, string siteBase, string apiBase, ConfluenceContent page)
    {
        try
        {
            // export_view is the rendered HTML (macros expanded) — far easier to parse than the
            // ac:-namespaced storage format. Fetched per page so the search phase stays light.
            var detail = http.GetFromJsonAsync<ConfluenceContent>(
                    $"{apiBase}/content/{page.Id}?expand=body.export_view,version,space",
                    JsonOpts).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("Empty content response.");

            var htmlBody = detail.Body?.ExportView?.Value;
            if (string.IsNullOrWhiteSpace(htmlBody))
            {
                logger.LogDebug("Skipping Confluence page {Id} ({Title}) — empty body", page.Id, page.Title);
                return null;
            }

            var stream = new MemoryStream(Encoding.UTF8.GetBytes(htmlBody));
            return new SourceDocument(
                SourceId: page.Id!,
                Title: detail.Title ?? page.Title ?? page.Id!,
                Content: stream,
                MimeType: "text/html",
                VersionHash: ComputeVersionHash(detail),
                LastModified: detail.Version?.When ?? DateTimeOffset.UtcNow,
                Metadata: new Dictionary<string, string>
                {
                    ["confluenceContentType"] = "page",
                    ["spaceKey"] = detail.Space?.Key ?? page.Space?.Key ?? string.Empty,
                    ["webUrl"] = BuildWebUrl(siteBase, detail.Links ?? page.Links),
                    ["pageVersion"] = detail.Version?.Number?.ToString() ?? string.Empty,
                });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Skipping Confluence page {Id} ({Title}) — could not fetch body", page.Id, page.Title);
            return null;
        }
    }

    private SourceDocument? TryBuildAttachmentDocument(HttpClient http, string siteBase, ConfluenceContent att, long maxBytes)
    {
        try
        {
            var ext = Path.GetExtension(att.Title ?? string.Empty);
            if (!ExtensionMime.TryGetValue(ext, out var parserMime))
            {
                logger.LogDebug("Skipping unsupported attachment {Title} ({Mime})", att.Title, att.Extensions?.MediaType);
                return null;
            }
            if (att.Extensions?.FileSize is long size && size > maxBytes)
            {
                logger.LogDebug("Skipping {Title}: {MB:F1} MB exceeds limit", att.Title, size / 1_048_576.0);
                return null;
            }

            var download = att.Links?.Download;
            if (string.IsNullOrEmpty(download))
            {
                logger.LogDebug("Skipping attachment {Id} ({Title}) — no download link", att.Id, att.Title);
                return null;
            }

            var content = DownloadContent(http, download.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? download
                : siteBase + download);
            return new SourceDocument(
                SourceId: att.Id!,
                Title: att.Title ?? att.Id!,
                Content: content,
                MimeType: parserMime,
                VersionHash: ComputeVersionHash(att),
                LastModified: att.Version?.When ?? DateTimeOffset.UtcNow,
                Metadata: new Dictionary<string, string>
                {
                    ["confluenceContentType"] = "attachment",
                    ["spaceKey"] = att.Space?.Key ?? string.Empty,
                    ["webUrl"] = BuildWebUrl(siteBase, att.Links),
                    ["sourceMimeType"] = att.Extensions?.MediaType ?? string.Empty,
                });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Skipping Confluence attachment {Id} ({Title}) — could not download", att.Id, att.Title);
            return null;
        }
    }

    private static MemoryStream DownloadContent(HttpClient http, string url)
    {
        // Sync-over-async deliberately: matches the other connectors' lazy-download shape (caller
        // pulls one stream at a time via the IEnumerable, SyncSchedulerJob disposes it before the next).
        using var resp = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        resp.EnsureSuccessStatusCode();
        var ms = new MemoryStream();
        resp.Content.CopyToAsync(ms).GetAwaiter().GetResult();
        ms.Position = 0;
        return ms;
    }

    // Version number increments on every edit (pages and attachments alike) — the stable
    // change marker Confluence offers in place of a content hash.
    private static string ComputeVersionHash(ConfluenceContent item) =>
        item.Version?.Number?.ToString() ?? string.Empty;

    private static string BuildWebUrl(string siteBase, ConfluenceLinks? links) =>
        string.IsNullOrEmpty(links?.Webui) ? string.Empty : siteBase + links.Webui;

    // ── Cursor persistence ───────────────────────────────────

    private async Task PersistCursorAsync(ConnectorConfig config, DateTimeOffset syncStart, CancellationToken ct)
    {
        var result = await syncStateRepository.UpsertAsync(new SyncState
        {
            Id = Guid.NewGuid(),
            TenantId = config.TenantId,
            ConnectorId = config.Id,
            SourceId = CursorSourceId,
            VersionHash = syncStart.ToString("o", CultureInfo.InvariantCulture),
            Status = SyncStatus.Completed,
            LastSyncedAt = DateTime.UtcNow,
            ErrorMessage = string.Empty,
        }, ct);

        if (!result.Success)
            logger.LogWarning("Confluence: failed to persist sync cursor for connector {ConnectorId}: {Error}",
                config.Id, result.Error);
    }

    // ── Auth + HTTP client ───────────────────────────────────

    private HttpClient CreateAuthorizedClient(ConfluenceConnectorSettings settings)
    {
        var http = httpClientFactory.CreateClient();
        if (!string.IsNullOrWhiteSpace(settings.Email))
        {
            // Cloud: Basic auth with account email + API token.
            var raw = $"{settings.Email.Trim()}:{settings.ApiToken}";
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)));
        }
        else
        {
            // Server/Data Center: personal access token as Bearer.
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiToken);
        }
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }

    /// <summary>REST root, e.g. https://x.atlassian.net/wiki/rest/api or https://host/rest/api.</summary>
    private static string ResolveApiBase(ConfluenceConnectorSettings settings)
    {
        var baseUrl = settings.BaseUrl.Trim().TrimEnd('/');
        var uri = new Uri(baseUrl);
        // Cloud instances serve Confluence under the /wiki context path; add it when omitted.
        if (uri.Host.EndsWith(".atlassian.net", StringComparison.OrdinalIgnoreCase)
            && !uri.AbsolutePath.Contains("/wiki", StringComparison.OrdinalIgnoreCase))
            baseUrl += "/wiki";
        return baseUrl + "/rest/api";
    }

    private static List<string> ParseSpaceKeys(ConfluenceConnectorSettings settings) =>
        settings.SpaceKeys
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    // Only digit-strings survive: page IDs are numeric and are interpolated into CQL unquoted,
    // so this doubles as the injection guard.
    private static List<string> ParsePageIds(ConfluenceConnectorSettings settings) =>
        settings.PageIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(id => id.All(char.IsAsciiDigit))
            .ToList();

    // ── Settings load ────────────────────────────────────────

    private ConfluenceConnectorSettings Deserialize(ConnectorConfig config)
    {
        var json = encryption.Decrypt(config.SettingsJson);
        return JsonSerializer.Deserialize<ConfluenceConnectorSettings>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("ConfluenceConnectorSettings JSON is invalid.");
    }

    // ── REST response DTOs (minimal — only the fields we read) ──

    private sealed class ConfluenceSearchResponse
    {
        [JsonPropertyName("results")] public List<ConfluenceContent>? Results { get; set; }
        [JsonPropertyName("_links")] public ConfluenceLinks? Links { get; set; }
    }

    private sealed class ConfluenceCollection<T>
    {
        [JsonPropertyName("results")] public List<T>? Results { get; set; }
    }

    private sealed class ConfluenceSpace
    {
        public string? Key { get; set; }
        public string? Name { get; set; }
    }

    private sealed class ConfluenceContent
    {
        public string? Id { get; set; }
        public string? Type { get; set; }
        public string? Title { get; set; }
        public ConfluenceVersion? Version { get; set; }
        public ConfluenceSpace? Space { get; set; }
        public ConfluenceBody? Body { get; set; }
        public ConfluenceExtensions? Extensions { get; set; }
        [JsonPropertyName("_links")] public ConfluenceLinks? Links { get; set; }
    }

    private sealed class ConfluenceVersion
    {
        public int? Number { get; set; }
        public DateTimeOffset? When { get; set; }
    }

    private sealed class ConfluenceBody
    {
        [JsonPropertyName("export_view")] public ConfluenceBodyValue? ExportView { get; set; }
    }

    private sealed class ConfluenceBodyValue
    {
        public string? Value { get; set; }
    }

    private sealed class ConfluenceExtensions
    {
        public string? MediaType { get; set; }
        public long? FileSize { get; set; }
    }

    private sealed class ConfluenceLinks
    {
        public string? Next { get; set; }
        public string? Webui { get; set; }
        public string? Download { get; set; }
    }
}
