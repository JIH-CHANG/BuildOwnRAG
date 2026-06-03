using Hangfire;
using ManufacturingAI.Core.Interfaces;
using ManufacturingAI.Core.Models;
using ManufacturingAI.Infrastructure.Repositories;
using Microsoft.Extensions.Logging;
using System.Text;

namespace ManufacturingAI.Services.TestGen;

public interface ITestGenJob
{
    Task RunAsync(Guid scriptId, CancellationToken ct);
}

public class TestGenJob(
    IRepository<GeneratedTestScript> repository,
    IDocumentChunkRepository chunkRepository,
    IVectorStore vectorStore,
    IEmbeddingService embeddingService,
    ILLMService llmService,
    IBlobStorage blobStorage,
    ILogger<TestGenJob> logger) : ITestGenJob
{
    private const int TopK = 8;

    [AutomaticRetry(Attempts = 2, DelaysInSeconds = [60, 120])]
    public async Task RunAsync(Guid scriptId, CancellationToken ct)
    {
        var script = await repository.GetByIdAsync(scriptId, ct);
        if (script is null)
        {
            logger.LogWarning("TestGenJob: script {ScriptId} not found", scriptId);
            return;
        }

        script.Status = ScriptStatus.Generating;
        await repository.UpdateAsync(script, ct);

        try
        {
            var context = await FetchContextAsync(script, ct);
            var (systemPrompt, userMessage) = BuildPrompt(script.ScriptType, context);

            var llmResponse = await llmService.CompleteAsync(
                new LLMRequest(systemPrompt, userMessage, MaxTokens: 4096), ct);

            var blobPath = $"testgen/{script.TenantId}/{script.Id}{GetExtension(script.ScriptType)}";
            var bytes = Encoding.UTF8.GetBytes(llmResponse.Content);
            await blobStorage.SaveAsync(blobPath, new MemoryStream(bytes), ct);

            script.BlobPath = blobPath;
            script.Status = ScriptStatus.Completed;
            script.CompletedAt = DateTime.UtcNow;
            await repository.UpdateAsync(script, ct);

            logger.LogInformation("TestGenJob completed for script {ScriptId}", scriptId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "TestGenJob failed for script {ScriptId}", scriptId);
            script.Status = ScriptStatus.Failed;
            script.ErrorMessage = ex.Message;
            await repository.UpdateAsync(script, ct);
            throw; // let Hangfire retry
        }
    }

    private async Task<string> FetchContextAsync(GeneratedTestScript script, CancellationToken ct)
    {
        var query = BuildRetrievalQuery(script.ScriptType);
        var queryVector = await embeddingService.EmbedAsync(query, ct);

        var collectionName = $"tenant_{script.TenantId}";
        Dictionary<string, object>? filters = script.DocumentId.HasValue
            ? new() { ["documentId"] = script.DocumentId.Value.ToString() }
            : null;

        IEnumerable<VectorSearchResult> results;
        try
        {
            results = await vectorStore.SearchAsync(collectionName, queryVector, TopK, filters, ct);
        }
        catch (Exception ex)
        {
            // Collection may not exist yet (e.g. no documents indexed)
            logger.LogWarning(ex, "Vector search failed for tenant {TenantId}, proceeding without context", script.TenantId);
            return string.Empty;
        }

        var vectorIds = results.Select(r => r.Id).ToList();
        if (vectorIds.Count == 0)
            return string.Empty;

        var chunks = await chunkRepository.GetByIdsAsync(script.TenantId, vectorIds, ct);
        return string.Join("\n\n---\n\n", chunks.Select(c => c.Content));
    }

    private static string BuildRetrievalQuery(string scriptType) =>
        scriptType.ToLowerInvariant() switch
        {
            "robot" or "robotframework" => "test procedure steps parameters tolerance inspection",
            "python" => "test function parameters expected values assertions",
            "csv" => "test data parameters specifications tolerances measurements",
            _ => "quality test procedure parameters specifications"
        };

    private static (string SystemPrompt, string UserMessage) BuildPrompt(string scriptType, string context)
    {
        var format = scriptType.ToLowerInvariant() switch
        {
            "robot" or "robotframework" => "Robot Framework (.robot file)",
            "python" => "Python pytest",
            "csv" => "CSV test data table",
            _ => scriptType
        };

        var system = $"""
            You are an expert manufacturing test engineer.
            Given SOP/BOM/quality document excerpts, generate a {format} test script.
            Output only the raw script content with no markdown fences or additional explanation.
            """;

        var user = string.IsNullOrWhiteSpace(context)
            ? $"Generate a sample {format} test script for manufacturing quality inspection."
            : $"Based on the following document excerpts, generate a {format} test script:\n\n{context}";

        return (system, user);
    }

    private static string GetExtension(string scriptType) =>
        scriptType.ToLowerInvariant() switch
        {
            "robot" or "robotframework" => ".robot",
            "python" => ".py",
            "csv" => ".csv",
            _ => ".txt"
        };
}
