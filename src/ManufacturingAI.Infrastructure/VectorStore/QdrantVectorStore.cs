using ManufacturingAI.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace ManufacturingAI.Infrastructure.VectorStore;

public class QdrantVectorStore(QdrantClient client, ILogger<QdrantVectorStore> logger) : IVectorStore
{
    public async Task<bool> CollectionExistsAsync(string collectionName, CancellationToken ct = default)
    {
        var collections = await client.ListCollectionsAsync(ct);
        return collections.Any(c => c == collectionName);
    }

    public async Task EnsureCollectionAsync(string collectionName, int dimensions, CancellationToken ct = default)
    {
        if (await CollectionExistsAsync(collectionName, ct))
        {
            logger.LogDebug("Qdrant collection '{Name}' already exists — skipping creation", collectionName);
            return;
        }

        logger.LogInformation(
            "Creating Qdrant collection '{Name}' ({Dims} dims, Cosine distance)", collectionName, dimensions);

        await client.CreateCollectionAsync(
            collectionName,
            new VectorParams { Size = (ulong)dimensions, Distance = Distance.Cosine },
            cancellationToken: ct);
    }

    public async Task DeleteCollectionAsync(string collectionName, CancellationToken ct = default)
    {
        if (!await CollectionExistsAsync(collectionName, ct))
        {
            logger.LogWarning("Qdrant collection '{Name}' not found — nothing to delete", collectionName);
            return;
        }

        logger.LogInformation("Deleting Qdrant collection '{Name}'", collectionName);
        await client.DeleteCollectionAsync(collectionName, cancellationToken: ct);
    }

    public async Task UpsertAsync(string collectionName, Core.Interfaces.VectorDocument document, CancellationToken ct = default)
    {
        var point = new PointStruct
        {
            Id = new PointId { Uuid = document.Id },
            Vectors = document.Vector,
        };
        foreach (var (k, v) in document.Payload.ToDictionary(kvp => kvp.Key, kvp => ToValue(kvp.Value)))
            point.Payload[k] = v;

        await client.UpsertAsync(collectionName, [point], cancellationToken: ct);
    }

    public async Task<IEnumerable<Core.Interfaces.VectorSearchResult>> SearchAsync(
        string collectionName, float[] vector, int topK,
        Dictionary<string, object>? filters = null, CancellationToken ct = default)
    {
        Filter? qdrantFilter = null;
        if (filters is { Count: > 0 })
        {
            var conditions = filters.Select(kvp => new Condition
            {
                Field = new FieldCondition
                {
                    Key = kvp.Key,
                    Match = new Match { Keyword = kvp.Value?.ToString() ?? string.Empty }
                }
            });
            qdrantFilter = new Filter();
            qdrantFilter.Must.AddRange(conditions);
        }

        var results = await client.SearchAsync(
            collectionName, vector,
            limit: (ulong)topK, filter: qdrantFilter,
            cancellationToken: ct);

        return results.Select(r => new Core.Interfaces.VectorSearchResult(
            r.Id.Uuid,
            r.Score,
            r.Payload.ToDictionary(kvp => kvp.Key, kvp => (object)kvp.Value.ToString())));
    }

    public async Task DeleteByDocumentIdAsync(string collectionName, Guid documentId, CancellationToken ct = default)
    {
        if (!await CollectionExistsAsync(collectionName, ct))
        {
            logger.LogWarning(
                "Qdrant collection '{Name}' not found — no vectors to delete for document {DocumentId}",
                collectionName, documentId);
            return;
        }

        var filter = new Filter();
        filter.Must.Add(new Condition
        {
            Field = new FieldCondition
            {
                Key = "documentId",
                Match = new Match { Keyword = documentId.ToString() }
            }
        });
        await client.DeleteAsync(collectionName, filter, cancellationToken: ct);
    }

    private static Value ToValue(object? obj) => obj switch
    {
        string s => new Value { StringValue = s },
        int i => new Value { IntegerValue = i },
        long l => new Value { IntegerValue = l },
        double d => new Value { DoubleValue = d },
        bool b => new Value { BoolValue = b },
        _ => new Value { StringValue = obj?.ToString() ?? string.Empty }
    };
}
