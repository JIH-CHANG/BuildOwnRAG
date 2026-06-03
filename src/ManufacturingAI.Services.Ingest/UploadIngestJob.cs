using Hangfire;
using ManufacturingAI.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace ManufacturingAI.Services.Ingest;

public class UploadIngestJob(IIngestService ingestService, ILogger<UploadIngestJob> logger)
{
    [AutomaticRetry(Attempts = 2, DelaysInSeconds = [30, 60])]
    public async Task ProcessAsync(Guid tenantId, string filePath, string fileName, string mimeType, CancellationToken ct)
    {
        logger.LogInformation("UploadIngestJob: processing '{File}' for tenant {TenantId}", fileName, tenantId);

        if (!File.Exists(filePath))
        {
            logger.LogError("UploadIngestJob: file not found at '{Path}' — aborting", filePath);
            return;
        }

        await using var stream = File.OpenRead(filePath);
        var source = new SourceDocument(
            SourceId: fileName,
            Title: Path.GetFileNameWithoutExtension(fileName),
            Content: stream,
            MimeType: mimeType,
            VersionHash: string.Empty,
            LastModified: File.GetLastWriteTimeUtc(filePath),
            Metadata: new Dictionary<string, string>()
        );

        var config = IngestService.UploadConnectorConfig(tenantId);
        var result = await ingestService.IngestDocumentAsync(tenantId, source, config, ct);

        if (!result.Success)
        {
            logger.LogError("UploadIngestJob: failed for '{File}': {Error}", fileName, result.Error);
            // Throw so Hangfire marks the job as Failed and applies AutomaticRetry
            throw new InvalidOperationException($"Ingest failed for '{fileName}': {result.Error}");
        }

        logger.LogInformation("UploadIngestJob: '{File}' indexed successfully", fileName);
    }
}
