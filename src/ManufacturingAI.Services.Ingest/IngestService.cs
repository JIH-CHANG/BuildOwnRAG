using Hangfire;
using ManufacturingAI.Core.Common;
using ManufacturingAI.Core.Interfaces;
using ManufacturingAI.Core.Models;
using ManufacturingAI.Core.Parser;
using ManufacturingAI.Core.RAG.Chunking;
using ManufacturingAI.Infrastructure.Caching;
using ManufacturingAI.Infrastructure.Repositories;
using System.Security.Cryptography;

namespace ManufacturingAI.Services.Ingest;

public class IngestService(
    IDocumentRepository documentRepository,
    IDocumentChunkRepository chunkRepository,
    ISyncStateRepository syncStateRepository,
    IConnectorConfigRepository connectorConfigRepository,
    IParserFactory parserFactory,
    IDocumentChunker chunker,
    IEmbeddingService embeddingService,
    IVectorStore vectorStore,
    ITenantVectorService tenantVectorService,
    ICacheService cache,
    IBackgroundJobClient jobClient,
    IIngestQueue ingestQueue,
    IRepository<Tenant> tenantRepository) : IIngestService
{
    public async Task<Result> TriggerSyncAsync(Guid tenantId, Guid? connectorId = null, CancellationToken ct = default)
    {
        try
        {
            var connectors = connectorId.HasValue
                ? new[] { await connectorConfigRepository.GetByIdAsync(connectorId.Value, ct) }
                    .Where(c => c is not null).Cast<ConnectorConfig>()
                : await connectorConfigRepository.GetEnabledByTenantAsync(tenantId, ct);

            foreach (var connector in connectors)
            {
                await ingestQueue.EnqueueAsync(
                    new IngestJobMessage(tenantId, connector.Id, connector.ConnectorType, null, "manual"), ct);
            }

            return Result.Ok();
        }
        catch (Exception ex)
        {
            return Result.Fail(ex.Message);
        }
    }

    public async Task<SyncStatusResult> GetSyncStatusAsync(Guid tenantId, CancellationToken ct = default)
    {
        var connectors = await connectorConfigRepository.GetEnabledByTenantAsync(tenantId, ct);
        var connectorList = connectors.ToList();

        var statuses = new List<ConnectorSyncStatus>();
        foreach (var connector in connectorList)
        {
            var syncStates = await syncStateRepository.GetByConnectorAsync(connector.Id, ct);
            var latest = syncStates
                .OrderByDescending(s => s.LastSyncedAt)
                .FirstOrDefault();

            statuses.Add(new ConnectorSyncStatus(
                ConnectorId: connector.Id,
                DisplayName: connector.DisplayName,
                ConnectorType: connector.ConnectorType,
                Status: latest?.Status ?? SyncStatus.Pending,
                LastSyncedAt: latest?.LastSyncedAt,
                ErrorMessage: latest?.ErrorMessage));
        }

        int runningJobs = statuses.Count(s => s.Status == SyncStatus.Running);

        return new SyncStatusResult(
            TenantId: tenantId,
            TotalConnectors: connectorList.Count,
            RunningJobs: runningJobs,
            Connectors: statuses);
    }

    public async Task<Result> IngestDocumentAsync(
        Guid tenantId, SourceDocument source, ConnectorConfig config, CancellationToken ct = default)
    {
        // 1. 計算 VersionHash（SHA256）
        var versionHash = !string.IsNullOrEmpty(source.VersionHash)
            ? source.VersionHash
            : await ComputeHashAsync(source.Content, ct);

        // 2. 查詢 SyncState，VersionHash 相同則跳過
        var existingSyncStates = await syncStateRepository.GetByConnectorAsync(config.Id, ct);
        var existingSync = existingSyncStates.FirstOrDefault(s => s.SourceId == source.SourceId);
        if (existingSync?.VersionHash == versionHash && existingSync.Status == SyncStatus.Completed)
        {
            // Content unchanged — skip re-processing. But reconcile the Document row first:
            // it can lag behind at Pending if the record was recreated after the SyncState was
            // written (e.g. delete + re-upload of an identical file), which would otherwise leave
            // the UI showing Pending forever.
            var existing = await documentRepository.GetBySourceIdAsync(
                tenantId, config.ConnectorType, source.SourceId, ct);
            if (existing is not null && existing.Status != DocumentStatus.Indexed)
                await documentRepository.UpdateStatusAsync(existing.Id, DocumentStatus.Indexed, ct);
            return Result.Ok();
        }

        // 開始更新或建立 SyncState（Running）
        var syncState = existingSync ?? new SyncState
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ConnectorId = config.Id,
            SourceId = source.SourceId
        };
        syncState.Status = SyncStatus.Running;
        syncState.VersionHash = versionHash;
        await syncStateRepository.UpsertAsync(syncState, ct);

        try
        {
            // 3. 依 MimeType 選擇 Parser（不符時回傳 Result.Fail，不拋例外）
            var parseResult = await parserFactory.ParseAsync(source.MimeType, source.Content, source.Title, ct);
            if (!parseResult.Success)
                return Result.Fail(parseResult.Error!);

            // 4. 解析完成
            var parsed = parseResult.Value!;

            // 5. SemanticChunker 切段
            parsed.Metadata["title"] = source.Title;
            parsed.Metadata["sourceType"] = config.ConnectorType;

            var chunks = chunker.Chunk(parsed, new ChunkOptions()).ToList();

            // 儲存或更新 Document（mode-independent）
            var existingDoc = await documentRepository.GetBySourceIdAsync(
                tenantId, config.ConnectorType, source.SourceId, ct);

            var document = existingDoc ?? new Document
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                SourceType = config.ConnectorType,
                SourceId = source.SourceId,
                CreatedAt = DateTime.UtcNow
            };

            document.Title = source.Title;
            document.MimeType = source.MimeType;
            document.VersionHash = versionHash;
            document.Status = DocumentStatus.Processing;
            document.UpdatedAt = DateTime.UtcNow;

            if (existingDoc is null)
                await documentRepository.AddAsync(document, ct);
            else
                await documentRepository.UpdateAsync(document, ct);

            // 6. Retrieval mode decides whether we embed + index into Qdrant.
            //    Markdown mode = BM25-only → skip Embedding model + Qdrant entirely.
            var markdownMode = await IsMarkdownModeAsync(tenantId, ct);

            // VectorId per chunk — empty in Markdown mode (no Qdrant point).
            var chunkVectorIds = chunks.Select(_ => string.Empty).ToList();

            if (!markdownMode)
            {
                // ── Hybrid: batch embed → version-aware Qdrant collection ──
                var contents = chunks.Select(c => c.Content).ToList();
                var vectors = (await embeddingService.EmbedBatchAsync(contents, ct)).ToList();

                // Use the actual vector dimension from the embedding call, not the static property,
                // because different models (e.g. gemini-embedding-2 vs text-embedding-004) return different dims.
                var actualDimensions = vectors.Count > 0 ? vectors[0].Length : embeddingService.Dimensions;
                var (collectionName, migrationRequired) =
                    await tenantVectorService.EnsureDimensionsCompatibleAsync(tenantId, actualDimensions, ct);

                if (migrationRequired)
                {
                    jobClient.Enqueue<ReingestJob>(job =>
                        job.MigrateCollectionAsync(tenantId, actualDimensions, CancellationToken.None));
                    return Result.Fail("Embedding dimensions changed — migration enqueued. Retry after migration completes.");
                }

                // 刪除舊的向量（重新索引）
                if (existingDoc is not null)
                    await vectorStore.DeleteByDocumentIdAsync(collectionName, document.Id, ct);

                var vectorDocs = chunks.Select((chunk, i) => new VectorDocument(
                    Id: Guid.NewGuid().ToString(),
                    Vector: vectors[i],
                    Payload: new Dictionary<string, object>
                    {
                        ["documentId"] = document.Id.ToString(),
                        ["tenantId"] = tenantId.ToString(),
                        ["chunkIndex"] = chunk.ChunkIndex,
                        ["sourceType"] = config.ConnectorType
                    })).ToList();

                foreach (var vDoc in vectorDocs)
                    await vectorStore.UpsertAsync(collectionName, vDoc, ct);

                chunkVectorIds = vectorDocs.Select(v => v.Id).ToList();
            }

            // 7. 儲存 DocumentChunk 到 PostgreSQL（mode-independent — BM25 reads this content）
            //    先刪舊的 chunks
            var oldChunks = await chunkRepository.GetByDocumentIdAsync(document.Id, ct);
            foreach (var oldChunk in oldChunks)
                await chunkRepository.DeleteAsync(oldChunk.Id, ct);

            for (int i = 0; i < chunks.Count; i++)
            {
                var dbChunk = new DocumentChunk
                {
                    Id = Guid.NewGuid(),
                    DocumentId = document.Id,
                    TenantId = tenantId,
                    Content = chunks[i].Content,
                    ChunkIndex = chunks[i].ChunkIndex,
                    VectorId = chunkVectorIds[i],
                    Metadata = chunks[i].Metadata
                };
                await chunkRepository.AddAsync(dbChunk, ct);
            }

            // 更新 Document 狀態為 Indexed
            await documentRepository.UpdateStatusAsync(document.Id, DocumentStatus.Indexed, ct);

            // 8. 更新 SyncState Completed
            syncState.Status = SyncStatus.Completed;
            syncState.LastSyncedAt = DateTime.UtcNow;
            syncState.ErrorMessage = string.Empty;
            await syncStateRepository.UpsertAsync(syncState, ct);

            // 9. 清除相關 Cache
            await cache.RemoveByPatternAsync(CacheKeys.DocumentList(tenantId), ct);

            return Result.Ok();
        }
        catch (Exception ex)
        {
            // 11. 錯誤時更新 SyncState Failed
            syncState.Status = SyncStatus.Failed;
            syncState.ErrorMessage = ex.Message;
            await syncStateRepository.UpsertAsync(syncState, ct);

            if (await documentRepository.GetBySourceIdAsync(tenantId, config.ConnectorType, source.SourceId, ct) is { } doc)
                await documentRepository.UpdateStatusAsync(doc.Id, DocumentStatus.Failed, ct);

            return Result.Fail(ex.Message);
        }
    }

    public async Task<Result> IngestUploadedFileAsync(
        Guid tenantId, string filePath, string fileName, string mimeType, CancellationToken ct = default)
    {
        try
        {
            // Create a Pending record immediately so the UI shows the document right away
            var existingDoc = await documentRepository.GetBySourceIdAsync(tenantId, "upload", fileName, ct);
            if (existingDoc is null)
            {
                var doc = new Document
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    Title = Path.GetFileNameWithoutExtension(fileName),
                    FilePath = filePath,
                    FileSizeBytes = new FileInfo(filePath).Length,
                    MimeType = mimeType,
                    SourceType = "upload",
                    SourceId = fileName,
                    Status = DocumentStatus.Pending,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    VersionHash = string.Empty
                };
                await documentRepository.AddAsync(doc, ct);
            }
            else if (existingDoc.Status == DocumentStatus.Failed)
            {
                // Reset so the UI shows Pending while the job retries
                await documentRepository.UpdateStatusAsync(existingDoc.Id, DocumentStatus.Pending, ct);
            }

            // Enqueue background job for the heavy work (parse → embed → index)
            jobClient.Enqueue<UploadIngestJob>(job =>
                job.ProcessAsync(tenantId, filePath, fileName, mimeType, CancellationToken.None));

            await cache.RemoveByPatternAsync(CacheKeys.DocumentList(tenantId), ct);
            return Result.Ok();
        }
        catch (Exception ex)
        {
            return Result.Fail(ex.Message);
        }
    }

    internal static ConnectorConfig UploadConnectorConfig(Guid tenantId)
    {
        // Deterministic synthetic connector ID per tenant for upload source
        var bytes = tenantId.ToByteArray();
        bytes[15] ^= 0xAA;
        return new ConnectorConfig
        {
            Id = new Guid(bytes),
            TenantId = tenantId,
            ConnectorType = "upload",
            DisplayName = "Direct Upload",
            IsEnabled = true
        };
    }

    private async Task<bool> IsMarkdownModeAsync(Guid tenantId, CancellationToken ct)
    {
        var tenant = await tenantRepository.GetByIdAsync(tenantId, ct);
        return tenant?.Settings.RetrievalMode == RetrievalMode.Markdown;
    }

    private static async Task<string> ComputeHashAsync(Stream stream, CancellationToken ct)
    {
        stream.Position = 0;
        var hashBytes = await SHA256.HashDataAsync(stream, ct);
        stream.Position = 0;
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
