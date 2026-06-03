using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using ManufacturingAI.Core;
using ManufacturingAI.Core.Common;
using ManufacturingAI.Core.Interfaces;
using ManufacturingAI.Core.Models;
using ManufacturingAI.Core.RAG.Orchestration;
using ManufacturingAI.Core.RAG.Retrieval;
using ManufacturingAI.Infrastructure.Repositories;
using Microsoft.Extensions.Logging;

namespace ManufacturingAI.Services.Query;

public interface IMarkdownQueryService
{
    Task<Result<QueryResult>> QueryAsync(QueryRequest request, CancellationToken ct = default);
    IAsyncEnumerable<string> StreamQueryAsync(QueryRequest request, CancellationToken ct = default);
}

// Markdown (BM25-only) answering: keyword-prefilter + BM25 over Postgres chunks → LLM.
// One external call total (the LLM); no embeddings, no Qdrant.
public class MarkdownQueryService(
    IMarkdownRetriever retriever,
    ILLMService llm,
    IRepository<Tenant> tenantRepository,
    ILogger<MarkdownQueryService> logger) : IMarkdownQueryService
{
    public async Task<Result<QueryResult>> QueryAsync(QueryRequest request, CancellationToken ct = default)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var chunks = await retriever.RetrieveAsync(request.TenantId, request.Question, ct);
            var systemPrompt = await ResolveSystemPromptAsync(request.TenantId, ct);
            var resp = await llm.CompleteAsync(
                new LLMRequest(systemPrompt, BuildUserPrompt(request.Question, chunks)), ct);

            var sources = chunks.Select(c => new SourceReference(
                c.DocumentId, c.Metadata.SourceTitle, c.Metadata.SourceType, c.Metadata.PageNumber, Excerpt(c.Content)))
                .ToList();

            return Result<QueryResult>.Ok(new QueryResult(
                resp.Content, chunks.Count > 0 ? 0.9 : 0.0, sources, false, resp.IsFromFallback, sw.ElapsedMilliseconds));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Markdown query failed.");
            return Result<QueryResult>.Fail(ex.Message);
        }
    }

    public async IAsyncEnumerable<string> StreamQueryAsync(
        QueryRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var chunks = await retriever.RetrieveAsync(request.TenantId, request.Question, ct);
        var systemPrompt = await ResolveSystemPromptAsync(request.TenantId, ct);
        await foreach (var token in llm.StreamAsync(
            new LLMRequest(systemPrompt, BuildUserPrompt(request.Question, chunks)), ct))
            yield return token;
    }

    // Use the tenant's custom instructions when set; otherwise the built-in default.
    private async Task<string> ResolveSystemPromptAsync(Guid tenantId, CancellationToken ct)
    {
        var tenant = await tenantRepository.GetByIdAsync(tenantId, ct);
        var custom = tenant?.Settings.SystemPrompt;
        return string.IsNullOrWhiteSpace(custom) ? PromptDefaults.SystemPrompt : custom;
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
}
