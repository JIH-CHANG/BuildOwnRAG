using ManufacturingAI.Core.Interfaces;
using ManufacturingAI.Core.Models;
using ManufacturingAI.Infrastructure.Repositories;
using ManufacturingAI.Infrastructure.Security;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text.Json;

namespace ManufacturingAI.Connectors.Folder;

public sealed class FolderConnector(
    ISyncStateRepository syncStateRepository,
    IEncryptionService encryption,
    ILogger<FolderConnector> logger) : IKnowledgeConnector
{
    public string ConnectorType => "folder";

    // ── TestConnectionAsync ──────────────────────────────────

    public Task<ConnectorTestResult> TestConnectionAsync(ConnectorConfig config, CancellationToken ct = default)
    {
        try
        {
            var settings = Deserialize(config);

            if (string.IsNullOrWhiteSpace(settings.FolderPath))
                return Task.FromResult(new ConnectorTestResult(false, "FolderPath is not configured."));

            if (!Directory.Exists(settings.FolderPath))
                return Task.FromResult(
                    new ConnectorTestResult(false, $"Folder does not exist: {settings.FolderPath}"));

            // Verify read access by attempting a directory listing
            _ = Directory.EnumerateFileSystemEntries(settings.FolderPath).FirstOrDefault();

            var fileCount = Directory.EnumerateFiles(
                settings.FolderPath, "*",
                settings.IncludeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .Count(f => settings.IncludeExtensions.Contains(
                    Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));

            return Task.FromResult(new ConnectorTestResult(true,
                $"Connection OK. {fileCount} matching file(s) found."));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Task.FromResult(new ConnectorTestResult(false, $"Access denied: {ex.Message}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ConnectorTestResult(false, ex.Message));
        }
    }

    // ── FetchDeltaAsync ──────────────────────────────────────

    public async Task<ConnectorDelta> FetchDeltaAsync(
        ConnectorConfig config,
        DateTimeOffset? since,
        CancellationToken ct = default)
    {
        var settings = Deserialize(config);

        // Build a hash lookup of already-indexed files (ConnectorId-scoped, not tenant-scoped,
        // because ConnectorConfig is already unique per tenant)
        var syncStates = (await syncStateRepository.GetByConnectorAsync(config.Id, ct)).ToList();
        var knownHashes = syncStates
            .Where(s => s.Status == SyncStatus.Completed)
            .ToDictionary(s => s.SourceId, s => s.VersionHash, StringComparer.OrdinalIgnoreCase);

        var maxBytes = (long)settings.MaxFileSizeMB * 1_048_576L;
        var searchOption = settings.IncludeSubfolders
            ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly;

        IEnumerable<string> candidateFiles;
        try
        {
            candidateFiles = Directory
                .EnumerateFiles(settings.FolderPath, "*", searchOption)
                .Where(f => settings.IncludeExtensions.Contains(
                    Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Cannot enumerate folder {Path}", settings.FolderPath);
            // Enumeration failure must not look like "every file vanished" — report no deletions.
            return ConnectorDelta.Empty;
        }

        var changed = new List<SourceDocument>();
        // Every matching file still on disk, whether or not it changed — the complement of this
        // set against the stored SyncState rows is what was deleted at the source.
        var presentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var filePath in candidateFiles)
        {
            ct.ThrowIfCancellationRequested();
            var sourceId = ToSourceId(filePath, settings.FolderPath);
            presentIds.Add(sourceId);

            FileInfo info;
            try { info = new FileInfo(filePath); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Cannot stat {File}", filePath);
                continue;
            }

            // ① Size guard
            if (info.Length > maxBytes)
            {
                logger.LogDebug("Skipping {File}: {MB:F1} MB exceeds {Limit} MB limit",
                    filePath, info.Length / 1_048_576.0, settings.MaxFileSizeMB);
                continue;
            }

            // ② Modification-time pre-filter (fast path, avoids hashing unchanged files)
            if (since.HasValue && info.LastWriteTimeUtc <= since.Value.UtcDateTime)
                continue;

            // ③ SHA-256 delta: compute hash, skip if unchanged
            string hash;
            try { hash = await ComputeSha256Async(filePath, ct); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Cannot hash {File}", filePath);
                continue;
            }

            if (knownHashes.TryGetValue(sourceId, out var storedHash) && storedHash == hash)
                continue;

            logger.LogDebug("Queuing changed file {File}", filePath);

            changed.Add(new SourceDocument(
                SourceId: sourceId,
                Title: Path.GetFileName(filePath),
                Content: new FileStream(filePath, FileMode.Open, FileAccess.Read,
                                  FileShare.Read, 81_920, useAsync: true),
                MimeType: ResolveMimeType(info.Extension),
                VersionHash: hash,
                LastModified: new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                Metadata: new Dictionary<string, string>
                {
                    ["filePath"] = filePath,
                    ["extension"] = info.Extension.ToLowerInvariant(),
                    ["sizeBytes"] = info.Length.ToString()
                }
            ));
        }

        var deleted = syncStates
            .Where(s => !s.SourceId.StartsWith("__", StringComparison.Ordinal)
                        && !presentIds.Contains(s.SourceId))
            .Select(s => s.SourceId)
            .ToList();

        logger.LogInformation(
            "FolderConnector [{ConnectorId}]: {Changed} changed, {Deleted} deleted / {Total} matching files",
            config.Id, changed.Count, deleted.Count, presentIds.Count);

        return new ConnectorDelta(changed, deleted);
    }

    // ── Helpers ──────────────────────────────────────────────

    private FolderConnectorSettings Deserialize(ConnectorConfig config)
    {
        var json = encryption.Decrypt(config.SettingsJson);
        return JsonSerializer.Deserialize<FolderConnectorSettings>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("FolderConnectorSettings JSON is invalid.");
    }

    /// <summary>Relative path from root, normalised to forward-slashes, lower-cased.</summary>
    private static string ToSourceId(string filePath, string rootPath)
        => Path.GetRelativePath(rootPath, filePath)
               .Replace('\\', '/')
               .ToLowerInvariant();

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 81_920, useAsync: true);
        var bytes = await SHA256.HashDataAsync(fs, ct);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string ResolveMimeType(string extension) => extension.ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".csv" => "text/csv",
        ".txt" => "text/plain",
        ".md" or ".markdown" => "text/markdown",
        _ => "application/octet-stream"
    };
}
