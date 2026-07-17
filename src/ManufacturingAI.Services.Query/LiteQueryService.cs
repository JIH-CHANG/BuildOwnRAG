using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using ManufacturingAI.Core;
using ManufacturingAI.Core.Common;
using ManufacturingAI.Core.Interfaces;
using ManufacturingAI.Core.Models;
using ManufacturingAI.Core.Observability;
using ManufacturingAI.Core.RAG.Memory;
using ManufacturingAI.Core.RAG.Orchestration;
using ManufacturingAI.Core.RAG.Retrieval;
using ManufacturingAI.Infrastructure.Repositories;
using Microsoft.Extensions.Logging;

namespace ManufacturingAI.Services.Query;

public interface ILiteQueryService
{
    Task<Result<QueryResult>> QueryAsync(QueryRequest request, CancellationToken ct = default);
    IAsyncEnumerable<QueryStreamEvent> StreamQueryAsync(QueryRequest request, CancellationToken ct = default);
}

// Lite (BM25-only) answering: keyword-prefilter + BM25 over Postgres chunks → LLM.
// One external call total (the LLM); no embeddings, no Qdrant.
public class LiteQueryService(
    ILiteRetriever retriever,
    ILLMService llm,
    IRepository<Tenant> tenantRepository,
    IQueryLogRepository queryLogRepository,
    IQaMemoryService qaMemory,
    ILogger<LiteQueryService> logger) : ILiteQueryService
{
    public async Task<Result<QueryResult>> QueryAsync(QueryRequest request, CancellationToken ct = default)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var chunks = await retriever.RetrieveAsync(request.TenantId, request.Question, ct);
            var systemPrompt = await ResolveSystemPromptAsync(request, ct);
            var resp = await llm.CompleteAsync(
                new LLMRequest(systemPrompt, BuildUserPrompt(request.Question, chunks)), ct);

            var confidence = chunks.Count > 0 ? 0.9 : 0.0;
            await SaveQueryLogAsync(request, resp.Content, chunks, confidence, sw.ElapsedMilliseconds, ct);
            await qaMemory.RecordAnswerAsync(request.TenantId, request.Question, resp.Content, ct);

            RecordQueryMetrics(confidence, sw.ElapsedMilliseconds);
            return Result<QueryResult>.Ok(new QueryResult(
                resp.Content, confidence, BuildSources(chunks, request.IncludeFullContext),
                false, resp.IsFromFallback, sw.ElapsedMilliseconds));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Lite query failed.");
            AppMetrics.Queries.Add(1, new("mode", "lite"), new("outcome", "error"));
            return Result<QueryResult>.Fail(ex.Message);
        }
    }

    public async IAsyncEnumerable<QueryStreamEvent> StreamQueryAsync(
        QueryRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var chunks = await retriever.RetrieveAsync(request.TenantId, request.Question, ct);
        var systemPrompt = await ResolveSystemPromptAsync(request, ct);

        var answer = new StringBuilder();
        await foreach (var token in llm.StreamAsync(
            new LLMRequest(systemPrompt, BuildUserPrompt(request.Question, chunks)), ct))
        {
            answer.Append(token);
            yield return QueryStreamEvent.OfToken(token);
        }

        // Persist a QueryLog so the streamed answer can receive feedback and feed analytics.
        var confidence = chunks.Count > 0 ? 0.9 : 0.0;
        var queryId = await SaveQueryLogAsync(
            request, answer.ToString(), chunks, confidence, sw.ElapsedMilliseconds, ct);
        await qaMemory.RecordAnswerAsync(request.TenantId, request.Question, answer.ToString(), ct);

        RecordQueryMetrics(confidence, sw.ElapsedMilliseconds);
        yield return QueryStreamEvent.Completed(BuildSources(chunks), queryId, confidence);
    }

    private static void RecordQueryMetrics(double confidence, long elapsedMs)
    {
        AppMetrics.Queries.Add(1,
            new("mode", "lite"), new("outcome", "ok"), new("low_confidence", confidence < 0.4));
        AppMetrics.QueryDuration.Record(elapsedMs, new KeyValuePair<string, object?>("mode", "lite"));
        AppMetrics.QueryConfidence.Record(confidence, new KeyValuePair<string, object?>("mode", "lite"));
    }

    // Use the tenant's custom instructions when set; otherwise the built-in
    // default. Appends the feedback-driven QA memory block when any entry
    // matches the incoming question.
    private async Task<string> ResolveSystemPromptAsync(QueryRequest request, CancellationToken ct)
    {
        var tenant = await tenantRepository.GetByIdAsync(request.TenantId, ct);
        var custom = tenant?.Settings.SystemPrompt;
        var instructions = string.IsNullOrWhiteSpace(custom) ? PromptDefaults.SystemPrompt : custom;

        var memory = await qaMemory.BuildMemoryContextAsync(request.TenantId, request.Question, ct);
        return string.IsNullOrEmpty(memory) ? instructions : $"{instructions}\n\n{memory}";
    }

    private static string BuildUserPrompt(string question, IReadOnlyList<DocumentChunk> chunks)
    {
        if (chunks.Count == 0)
            return $"Context:\n(no relevant content found)\n\nQuestion: {question}";

        var sb = new StringBuilder("Context:\n");
        foreach (var c in chunks)
        {
            var title = string.IsNullOrWhiteSpace(c.Metadata.SourceTitle) ? "" : $"[{c.Metadata.SourceTitle}] ";
            sb.Append(title).AppendLine(c.Content).AppendLine();
        }
        return sb.Append("Question: ").Append(question).ToString();
    }

    private static string Excerpt(string content) =>
        content.Length <= 200 ? content : content[..200];

    private static List<SourceReference> BuildSources(IReadOnlyList<DocumentChunk> chunks, bool includeFullContent = false)
        => chunks.Select(c => new SourceReference(
            c.DocumentId, c.Metadata.SourceTitle, c.Metadata.SourceType, c.Metadata.PageNumber, Excerpt(c.Content),
            includeFullContent ? c.Content : null))
            .ToList();

    private async Task<Guid> SaveQueryLogAsync(
        QueryRequest request,
        string answer,
        IReadOnlyList<DocumentChunk> chunks,
        double confidenceScore,
        long latencyMs,
        CancellationToken ct)
    {
        var log = new QueryLog
        {
            Id = Guid.NewGuid(),
            TenantId = request.TenantId,
            UserId = request.UserId,
            Question = request.Question,
            Answer = answer,
            SourceChunkIds = chunks.Select(c => c.Id.ToString()).ToList(),
            // Lite mode is BM25-only; record rank + content excerpt, leave scores null.
            RetrievedChunks = chunks.Select((c, i) => new RetrievedChunkLog
            {
                ChunkId = c.Id.ToString(),
                Rank = i + 1,
                SourceTitle = c.Metadata.SourceTitle,
                ContentExcerpt = c.Content.Length > 300 ? c.Content[..300] : c.Content
            }).ToList(),
            ConfidenceScore = confidenceScore,
            LatencyMs = latencyMs,
            CreatedAt = DateTime.UtcNow
        };

        await queryLogRepository.AddAsync(log, ct);
        return log.Id;
    }
}
