using ManufacturingAI.Core;
using ManufacturingAI.Core.Interfaces;
using ManufacturingAI.Core.Models;
using ManufacturingAI.Core.RAG.Reranking;
using ManufacturingAI.Core.RAG.Retrieval;
using ManufacturingAI.Infrastructure.Caching;
using ManufacturingAI.Infrastructure.Repositories;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace ManufacturingAI.Core.RAG.Orchestration;

public class QueryOrchestrator(
    IHybridRetriever retriever,
    IRerankerFactory rerankerFactory,
    ILLMService llmService,
    ICacheService cache,
    IRepository<Tenant> tenantRepository,
    IQueryLogRepository queryLogRepository) : IQueryOrchestrator
{
    private const string FallbackWarning = "\n\nNote: The confidence score for this answer is low. Please verify against the original documents.";

    // Combine the tenant's instructions (or the built-in default) with the retrieved context.
    private static string BuildSystemPrompt(Tenant tenant, string context)
    {
        var instructions = string.IsNullOrWhiteSpace(tenant.Settings.SystemPrompt)
            ? PromptDefaults.SystemPrompt
            : tenant.Settings.SystemPrompt;
        return $"{instructions}\n\nReference documents:\n{context}";
    }

    public async Task<QueryResult> QueryAsync(QueryRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        // 1. Compute query hash and check Redis cache
        var queryHash = ComputeHash(request.Question);
        var cacheKey = CacheKeys.QueryResult(request.TenantId, queryHash);
        var cached = await cache.GetAsync<QueryResult>(cacheKey, ct);
        if (cached is not null)
        {
            return cached with { IsFromCache = true, LatencyMs = sw.ElapsedMilliseconds };
        }

        // 2. Retrieve candidate chunks via HybridRetriever
        var retrieveRequest = new RetrieveRequest(
            TenantId: request.TenantId,
            Query: request.Question,
            TopK: 10);

        var candidates = await retriever.RetrieveAsync(retrieveRequest, ct);

        // 3. Select reranker based on TenantPlan (Free: Cosine, Professional+: Cohere)
        var tenant = await GetTenantCachedAsync(request.TenantId, ct);
        var reranker = rerankerFactory.GetReranker(tenant.Plan);
        var reranked = (await reranker.RerankAsync(request.Question, candidates, topK: 5, ct)).ToList();

        // 4. Build system prompt with context
        var context = BuildContext(reranked);
        var systemPrompt = BuildSystemPrompt(tenant, context);

        // 5. Call LLM
        var llmRequest = new LLMRequest(
            SystemPrompt: systemPrompt,
            UserMessage: request.Question,
            History: request.History,
            Temperature: 0.3,
            MaxTokens: 2048);

        var llmResponse = await llmService.CompleteAsync(llmRequest, ct);

        // 6. Compute confidence score
        float topRerankerScore = reranked.Count > 0 ? reranked[0].FusionScore : 0f;
        double lengthIndicator = Math.Min(llmResponse.Content.Length / 500.0, 1.0);
        double confidenceScore = topRerankerScore * 0.7 + lengthIndicator * 0.3;

        // 7. Append fallback warning when confidence is low
        bool isFromFallback = confidenceScore < 0.4;
        var answer = isFromFallback
            ? llmResponse.Content + FallbackWarning
            : llmResponse.Content;

        // 8. Build source references
        var sources = BuildSources(reranked);

        var result = new QueryResult(
            Answer: answer,
            ConfidenceScore: confidenceScore,
            Sources: sources,
            IsFromCache: false,
            IsFromFallback: isFromFallback || llmResponse.IsFromFallback,
            LatencyMs: sw.ElapsedMilliseconds);

        // 9. Store result in Redis cache (TTL 30 min)
        await cache.SetAsync(cacheKey, result, CacheKeys.QueryResultTtl, ct);

        // 10. Persist QueryLog
        await SaveQueryLogAsync(request, result, reranked, queryHash, ct);

        return result;
    }

    public async IAsyncEnumerable<QueryStreamEvent> StreamQueryAsync(
        QueryRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        // Retrieval + rerank run synchronously up front; only LLM generation streams.
        var retrieveRequest = new RetrieveRequest(request.TenantId, request.Question, TopK: 10);
        var candidates = await retriever.RetrieveAsync(retrieveRequest, ct);

        var tenant = await GetTenantCachedAsync(request.TenantId, ct);
        var reranker = rerankerFactory.GetReranker(tenant.Plan);
        var reranked = (await reranker.RerankAsync(request.Question, candidates, topK: 5, ct)).ToList();

        var context = BuildContext(reranked);
        var systemPrompt = BuildSystemPrompt(tenant, context);
        var llmRequest = new LLMRequest(systemPrompt, request.Question, request.History, 0.3, 2048);

        var answer = new StringBuilder();
        await foreach (var chunk in llmService.StreamAsync(llmRequest, ct))
        {
            answer.Append(chunk);
            yield return QueryStreamEvent.OfToken(chunk);
        }

        // Confidence and the low-confidence warning mirror the non-streaming path.
        float topRerankerScore = reranked.Count > 0 ? reranked[0].FusionScore : 0f;
        double lengthIndicator = Math.Min(answer.Length / 500.0, 1.0);
        double confidenceScore = topRerankerScore * 0.7 + lengthIndicator * 0.3;
        bool isFromFallback = confidenceScore < 0.4;
        if (isFromFallback)
        {
            answer.Append(FallbackWarning);
            yield return QueryStreamEvent.OfToken(FallbackWarning);
        }

        var sources = BuildSources(reranked);

        // Persist a QueryLog so analytics keeps working for streamed answers too.
        var result = new QueryResult(
            Answer: answer.ToString(),
            ConfidenceScore: confidenceScore,
            Sources: sources,
            IsFromCache: false,
            IsFromFallback: isFromFallback,
            LatencyMs: sw.ElapsedMilliseconds);
        var queryId = await SaveQueryLogAsync(request, result, reranked, ComputeHash(request.Question), ct);

        yield return QueryStreamEvent.Completed(sources, queryId, confidenceScore);
    }

    private async Task<Tenant> GetTenantCachedAsync(Guid tenantId, CancellationToken ct)
    {
        var cacheKey = CacheKeys.TenantSettings(tenantId);
        var cached = await cache.GetAsync<Tenant>(cacheKey, ct);
        if (cached is not null) return cached;

        var tenant = await tenantRepository.GetByIdAsync(tenantId, ct)
            ?? throw new InvalidOperationException($"Tenant {tenantId} not found.");

        await cache.SetAsync(cacheKey, tenant, CacheKeys.TenantSettingsTtl, ct);
        return tenant;
    }

    private static string BuildContext(List<RetrievedChunk> chunks)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < chunks.Count; i++)
        {
            var c = chunks[i];
            sb.AppendLine($"[{i + 1}] {c.Metadata.SourceTitle}");
            if (!string.IsNullOrEmpty(c.Metadata.SectionTitle))
                sb.AppendLine($"  Section: {c.Metadata.SectionTitle}");
            if (c.Metadata.PageNumber.HasValue)
                sb.AppendLine($"  Page: {c.Metadata.PageNumber}");
            sb.AppendLine(c.Content);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static List<SourceReference> BuildSources(List<RetrievedChunk> chunks)
        => chunks.Select(c =>
        {
            Guid.TryParse(c.Metadata.SourceTitle, out var docId);
            return new SourceReference(
                DocumentId: docId,
                Title: c.Metadata.SourceTitle,
                SourceType: c.Metadata.SourceType,
                PageNumber: c.Metadata.PageNumber,
                RelevantExcerpt: c.Content.Length > 200 ? c.Content[..200] + "…" : c.Content);
        }).ToList();

    private async Task<Guid> SaveQueryLogAsync(
        QueryRequest request,
        QueryResult result,
        List<RetrievedChunk> chunks,
        string queryHash,
        CancellationToken ct)
    {
        var log = new QueryLog
        {
            Id = Guid.NewGuid(),
            TenantId = request.TenantId,
            UserId = request.UserId,
            Question = request.Question,
            Answer = result.Answer,
            SourceChunkIds = chunks.Select(c => c.ChunkId).ToList(),
            ConfidenceScore = result.ConfidenceScore,
            LatencyMs = result.LatencyMs,
            CreatedAt = DateTime.UtcNow
        };

        await queryLogRepository.AddAsync(log, ct);
        return log.Id;
    }

    private static string ComputeHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}
