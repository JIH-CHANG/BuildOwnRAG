namespace ManufacturingAI.Core.Interfaces;

public interface IBlobStorage
{
    Task SaveAsync(string blobPath, Stream content, CancellationToken ct = default);
    Task<Stream> OpenReadAsync(string blobPath, CancellationToken ct = default);
    Task DeleteAsync(string blobPath, CancellationToken ct = default);
    Task<bool> ExistsAsync(string blobPath, CancellationToken ct = default);
}
