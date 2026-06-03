namespace ManufacturingAI.Core.Interfaces;

public record VectorDocument(string Id, float[] Vector, Dictionary<string, object> Payload);
public record VectorSearchResult(string Id, float Score, Dictionary<string, object> Payload);

public interface IVectorStore
{
    Task UpsertAsync(string collectionName, VectorDocument document, CancellationToken ct = default);
    Task<IEnumerable<VectorSearchResult>> SearchAsync(string collectionName, float[] vector, int topK, Dictionary<string, object>? filters = null, CancellationToken ct = default);
    Task DeleteByDocumentIdAsync(string collectionName, Guid documentId, CancellationToken ct = default);
    Task EnsureCollectionAsync(string collectionName, int dimensions, CancellationToken ct = default);
    Task<bool> CollectionExistsAsync(string collectionName, CancellationToken ct = default);
    Task DeleteCollectionAsync(string collectionName, CancellationToken ct = default);
}
