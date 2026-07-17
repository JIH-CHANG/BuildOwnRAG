using ManufacturingAI.Core;
using ManufacturingAI.Core.Interfaces;
using ManufacturingAI.Core.Models;
using ManufacturingAI.Core.Observability;
using ManufacturingAI.Core.RAG.Memory;
using ManufacturingAI.Core.RAG.Reranking;
using ManufacturingAI.Core.RAG.Retrieval;
using ManufacturingAI.Infrastructure.Caching;
using ManufacturingAI.Infrastructure.Repositories;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace ManufacturingAI.Core.RAG.Orchestration;

public class QueryOrchestrator(
    IHybridRetriever retriever,
    IRerankerFactory rerankerFactory,
    ILLMService llmService,
    ICacheService cache,
    IRepository<Tenant> tenantRepository,
    IQueryLogRepository queryLogRepository,
    IQaMemoryService qaMemory) : IQueryOrchestrator
{
    private const string FallbackWarning = "\n\nNote: The confidence score for this answer is low. Please verify against the original documents.";

    private const int RetrieveCandidates = 20;
    private const int RerankTopK = 5;
    private const double LowConfidenceThreshold = 0.4;

    // Confidence blends the top reranked chunk's relevance (RerankScore, 0-1:
    // cosine similarity or Cohere relevance_score) with answer length. RRF
    // FusionScore must not be used here — it is rank-based and tops out at ~0.033,
    // which would put every answer below the low-confidence threshold.
    private static double ComputeConfidence(List<RetrievedChunk> reranked, int answerLength)
    {
        float topRerankScore = reranked.Count > 0 ? reranked[0].RerankScore : 0f;
        double lengthIndicator = Math.Min(answerLength / 500.0, 1.0);
        return topRerankScore * 0.7 + lengthIndicator * 0.3;
    }

    // Combine the tenant's instructions (or the built-in default) with the
    // feedback-driven QA memory (when any entry matches) and the retrieved context.
    private static string BuildSystemPrompt(Tenant tenant, string context, string? memory)
    {
        var instructions = string.IsNullOrWhiteSpace(tenant.Settings.SystemPrompt)
            ? PromptDefaults.SystemPrompt
            : tenant.Settings.SystemPrompt;
        var memoryBlock = string.IsNullOrEmpty(memory) ? "" : $"\n\n{memory}";
        return $"{instructions}{memoryBlock}\n\nReference documents:\n{context}";
    }

    public async Task<QueryResult> QueryAsync(QueryRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        // 1. Compute query hash and check Redis cache. Evaluation requests
        // (IncludeFullContext) bypass the cache so results reflect real
        // retrieval and carry full chunk content rather than a cached excerpt.
        var queryHash = QueryHashing.Compute(request.Question);
        var cacheKey = CacheKeys.QueryResult(request.TenantId, queryHash);
        if (!request.IncludeFullContext)
        {
            var cached = await cache.GetAsync<QueryResult>(cacheKey, ct);
            if (cached is not null)
            {
                AppMetrics.Queries.Add(1, new("mode", "hybrid"), new("outcome", "cached"));
                return cached with { IsFromCache = true, LatencyMs = sw.ElapsedMilliseconds };
            }
        }

        // 2. Retrieve candidate chunks via HybridRetriever. A wider candidate pool
        // gives the reranker more to choose from (Cohere precision improves with
        // more candidates; the cosine path still takes the top 5 either way).
        var retrieveRequest = new RetrieveRequest(
            TenantId: request.TenantId,
            Query: request.Question,
            TopK: RetrieveCandidates);

        var candidates = await retriever.RetrieveAsync(retrieveRequest, ct);

        // 3. Select reranker based on TenantPlan (Free: Cosine, Professional+: Cohere)
        var tenant = await GetTenantCachedAsync(request.TenantId, ct);
        var reranker = rerankerFactory.GetReranker(tenant.Plan);
        var reranked = (await reranker.RerankAsync(request.Question, candidates, RerankTopK, ct)).ToList();

        // 4. Build system prompt with context + feedback-driven QA memory
        var context = BuildContext(reranked);
        var memory = await qaMemory.BuildMemoryContextAsync(request.TenantId, request.Question, ct);
        var systemPrompt = BuildSystemPrompt(tenant, context, memory);

        // 5. Call LLM
        var llmRequest = new LLMRequest(
            SystemPrompt: systemPrompt,
            UserMessage: request.Question,
            History: request.History,
            Temperature: 0.3,
            MaxTokens: 2048);

        var llmResponse = await llmService.CompleteAsync(llmRequest, ct);

        // 6. Compute confidence score
        double confidenceScore = ComputeConfidence(reranked, llmResponse.Content.Length);

        // 7. Append fallback warning when confidence is low
        bool isFromFallback = confidenceScore < LowConfidenceThreshold;
        var answer = isFromFallback
            ? llmResponse.Content + FallbackWarning
            : llmResponse.Content;

        // 8. Build source references
        var sources = BuildSources(reranked, request.IncludeFullContext);

        var result = new QueryResult(
            Answer: answer,
            ConfidenceScore: confidenceScore,
            Sources: sources,
            IsFromCache: false,
            IsFromFallback: isFromFallback || llmResponse.IsFromFallback,
            LatencyMs: sw.ElapsedMilliseconds);

        // 9. Store result in Redis cache (TTL 30 min). Skip for evaluation
        // requests so the cache is not polluted with full-context payloads
        // keyed only by query hash.
        if (!request.IncludeFullContext)
        {
            await cache.SetAsync(cacheKey, result, CacheKeys.QueryResultTtl, ct);
        }

        // 10. Persist QueryLog and record the answer into QA memory
        await SaveQueryLogAsync(request, result, reranked, queryHash, ct);
        await qaMemory.RecordAnswerAsync(request.TenantId, request.Question, result.Answer, ct);

        RecordQueryMetrics(confidenceScore, isFromFallback, sw.ElapsedMilliseconds);
        return result;
    }

    public async IAsyncEnumerable<QueryStreamEvent> StreamQueryAsync(
        QueryRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        // Retrieval + rerank run synchronously up front; only LLM generation streams.
        var retrieveRequest = new RetrieveRequest(request.TenantId, request.Question, TopK: RetrieveCandidates);
        var candidates = await retriever.RetrieveAsync(retrieveRequest, ct);

        var tenant = await GetTenantCachedAsync(request.TenantId, ct);
        var reranker = rerankerFactory.GetReranker(tenant.Plan);
        var reranked = (await reranker.RerankAsync(request.Question, candidates, RerankTopK, ct)).ToList();

        var context = BuildContext(reranked);
        var memory = await qaMemory.BuildMemoryContextAsync(request.TenantId, request.Question, ct);
        var systemPrompt = BuildSystemPrompt(tenant, context, memory);
        var llmRequest = new LLMRequest(systemPrompt, request.Question, request.History, 0.3, 2048);

        var answer = new StringBuilder();
        await foreach (var chunk in llmService.StreamAsync(llmRequest, ct))
        {
            answer.Append(chunk);
            yield return QueryStreamEvent.OfToken(chunk);
        }

        // Confidence and the low-confidence warning mirror the non-streaming path.
        double confidenceScore = ComputeConfidence(reranked, answer.Length);
        bool isFromFallback = confidenceScore < LowConfidenceThreshold;
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
        var queryId = await SaveQueryLogAsync(request, result, reranked, QueryHashing.Compute(request.Question), ct);
        await qaMemory.RecordAnswerAsync(request.TenantId, request.Question, result.Answer, ct);

        RecordQueryMetrics(confidenceScore, isFromFallback, sw.ElapsedMilliseconds);
        yield return QueryStreamEvent.Completed(sources, queryId, confidenceScore);
    }

    private static void RecordQueryMetrics(double confidence, bool lowConfidence, long elapsedMs)
    {
        AppMetrics.Queries.Add(1,
            new("mode", "hybrid"), new("outcome", "ok"), new("low_confidence", lowConfidence));
        AppMetrics.QueryDuration.Record(elapsedMs, new KeyValuePair<string, object?>("mode", "hybrid"));
        AppMetrics.QueryConfidence.Record(confidence, new KeyValuePair<string, object?>("mode", "hybrid"));
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

    private static List<SourceReference> BuildSources(List<RetrievedChunk> chunks, bool includeFullContent = false)
        => chunks.Select(c =>
        {
            Guid.TryParse(c.Metadata.SourceTitle, out var docId);
            return new SourceReference(
                DocumentId: docId,
                Title: c.Metadata.SourceTitle,
                SourceType: c.Metadata.SourceType,
                PageNumber: c.Metadata.PageNumber,
                RelevantExcerpt: c.Content.Length > 200 ? c.Content[..200] + "…" : c.Content,
                FullContent: includeFullContent ? c.Content : null);
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
            RetrievedChunks = chunks.Select((c, i) => new RetrievedChunkLog
            {
                ChunkId = c.ChunkId,
                Rank = i + 1,
                SourceTitle = c.Metadata.SourceTitle,
                ContentExcerpt = c.Content.Length > 300 ? c.Content[..300] : c.Content,
                VectorScore = c.VectorScore,
                BM25Score = c.BM25Score,
                FusionScore = c.FusionScore
            }).ToList(),
            ConfidenceScore = result.ConfidenceScore,
            LatencyMs = result.LatencyMs,
            CreatedAt = DateTime.UtcNow
        };

        await queryLogRepository.AddAsync(log, ct);
        return log.Id;
    }
}
