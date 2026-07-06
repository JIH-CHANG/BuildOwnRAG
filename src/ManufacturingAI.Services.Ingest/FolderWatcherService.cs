using ManufacturingAI.Connectors.Folder;
using ManufacturingAI.Infrastructure.Persistence;
using ManufacturingAI.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;

namespace ManufacturingAI.Services.Ingest;

/// <summary>
/// IHostedService that keeps FileSystemWatchers alive for every folder connector
/// that has WatchMode = true. On file change, enqueues an ingest job via IIngestQueue
/// rather than waiting for the hourly scheduler.
/// </summary>
public sealed class FolderWatcherService(
    IServiceScopeFactory scopeFactory,
    IIngestQueue ingestQueue,
    ILogger<FolderWatcherService> logger) : IHostedService, IDisposable
{
    // ConnectorId → (TenantId, watcher, last-enqueue timestamp for debounce)
    private readonly ConcurrentDictionary<Guid, WatcherEntry> _watchers = new();

    private record WatcherEntry(Guid TenantId, FileSystemWatcher Watcher)
    {
        public long LastEnqueuedTicks = 0;
    }

    // ── IHostedService ────────────────────────────────────────

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var encryption = scope.ServiceProvider.GetRequiredService<IEncryptionService>();

            var configs = await db.ConnectorConfigs
                .Where(c => c.IsEnabled && c.ConnectorType == "folder")
                .AsNoTracking()
                .ToListAsync(cancellationToken);

            foreach (var cfg in configs)
                TryAddWatcher(cfg.Id, cfg.TenantId, cfg.SettingsJson, encryption);

            logger.LogInformation(
                "FolderWatcherService started — {Active} active watcher(s)", _watchers.Count);
        }
        catch (Exception ex)
        {
            // Log but don't crash the host; watcher is non-critical
            logger.LogError(ex, "FolderWatcherService failed to initialise");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var (_, entry) in _watchers)
        {
            entry.Watcher.EnableRaisingEvents = false;
            entry.Watcher.Dispose();
        }
        _watchers.Clear();
        return Task.CompletedTask;
    }

    // ── Internal helpers (also used by future management endpoints) ─

    internal void TryAddWatcher(Guid connectorId, Guid tenantId, string encryptedSettings, IEncryptionService encryption)
    {
        // Remove previous watcher for this connector if any
        RemoveWatcher(connectorId);

        FolderConnectorSettings? settings;
        try
        {
            var json = encryption.Decrypt(encryptedSettings);
            settings = JsonSerializer.Deserialize<FolderConnectorSettings>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Cannot deserialize settings for connector {Id}", connectorId);
            return;
        }

        if (settings is null || !settings.WatchMode)
            return;

        if (!Directory.Exists(settings.FolderPath))
        {
            logger.LogWarning(
                "Connector {Id}: WatchMode=true but folder '{Path}' not found — watcher skipped",
                connectorId, settings.FolderPath);
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(settings.FolderPath)
            {
                IncludeSubdirectories = settings.IncludeSubfolders,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                EnableRaisingEvents = true
            };

            // Multiple extension filters (OR semantics, .NET 6+)
            foreach (var ext in settings.IncludeExtensions)
                watcher.Filters.Add($"*{ext}");

            var entry = new WatcherEntry(tenantId, watcher);
            _watchers[connectorId] = entry;

            void Trigger(object _, FileSystemEventArgs e)
            {
                if (!settings.IncludeExtensions.Contains(
                        Path.GetExtension(e.FullPath), StringComparer.OrdinalIgnoreCase))
                    return;
                EnqueueDebounced(connectorId, entry, e.FullPath);
            }

            watcher.Changed += Trigger;
            watcher.Created += Trigger;
            watcher.Renamed += Trigger;
            watcher.Deleted += Trigger;   // sync run reconciles and removes the indexed copy
            watcher.Error += (_, e) =>
                logger.LogError(e.GetException(),
                    "FileSystemWatcher error for connector {Id}", connectorId);

            logger.LogInformation(
                "Watching '{Path}' for connector {Id}", settings.FolderPath, connectorId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create watcher for connector {Id}", connectorId);
        }
    }

    internal void RemoveWatcher(Guid connectorId)
    {
        if (_watchers.TryRemove(connectorId, out var old))
        {
            old.Watcher.EnableRaisingEvents = false;
            old.Watcher.Dispose();
        }
    }

    // ── Debounced ingest queue enqueue ───────────────────────

    private void EnqueueDebounced(Guid connectorId, WatcherEntry entry, string filePath)
    {
        // Suppress duplicate events within 2 s (FileSystemWatcher can fire several times per save)
        var now = DateTime.UtcNow.Ticks;
        var prev = Interlocked.Read(ref entry.LastEnqueuedTicks);
        if ((now - prev) < TimeSpan.FromSeconds(2).Ticks) return;
        if (Interlocked.CompareExchange(ref entry.LastEnqueuedTicks, now, prev) != prev) return;

        logger.LogDebug("File event → enqueueing sync for connector {Id} ({File})", connectorId, filePath);

        // Fire-and-forget: event handler cannot be async; exceptions are logged in EnqueueAsync
        _ = EnqueueAsync(connectorId, entry.TenantId);
    }

    private async Task EnqueueAsync(Guid connectorId, Guid tenantId)
    {
        try
        {
            await ingestQueue.EnqueueAsync(
                new IngestJobMessage(tenantId, connectorId, "folder", null, "watcher"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Cannot enqueue sync job for connector {Id}", connectorId);
        }
    }

    public void Dispose()
    {
        foreach (var (_, entry) in _watchers)
            entry.Watcher.Dispose();
    }
}
