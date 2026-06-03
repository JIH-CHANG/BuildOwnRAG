using ManufacturingAI.Core.Interfaces;
using Microsoft.Extensions.Configuration;

namespace ManufacturingAI.Infrastructure.Storage;

public class LocalBlobStorage(IConfiguration config) : IBlobStorage
{
    private readonly string _basePath = config["BlobStorage:LocalPath"] ?? "/app/data/blobs";

    public async Task SaveAsync(string blobPath, Stream content, CancellationToken ct = default)
    {
        var fullPath = FullPath(blobPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await using var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await content.CopyToAsync(fs, ct);
    }

    public Task<Stream> OpenReadAsync(string blobPath, CancellationToken ct = default)
    {
        var fullPath = FullPath(blobPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Blob not found: {blobPath}", fullPath);
        Stream stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string blobPath, CancellationToken ct = default)
    {
        var fullPath = FullPath(blobPath);
        if (File.Exists(fullPath))
            File.Delete(fullPath);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string blobPath, CancellationToken ct = default)
        => Task.FromResult(File.Exists(FullPath(blobPath)));

    // Prevent path traversal by canonicalising and asserting the result stays within _basePath.
    private string FullPath(string blobPath)
    {
        var full = Path.GetFullPath(Path.Combine(_basePath, blobPath));
        var baseNorm = Path.GetFullPath(_basePath);
        if (!full.StartsWith(baseNorm + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !full.Equals(baseNorm, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Invalid blob path: {blobPath}");
        return full;
    }
}
