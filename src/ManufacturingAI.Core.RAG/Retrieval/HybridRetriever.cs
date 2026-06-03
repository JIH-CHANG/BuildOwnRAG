using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;
using ManufacturingAI.Core.Interfaces;
using ManufacturingAI.Core.Models;
using ManufacturingAI.Infrastructure.Repositories;

namespace ManufacturingAI.Core.RAG.Retrieval;

public class HybridRetriever(
    IVectorStore vectorStore,
    IEmbeddingService embeddingService,
    IDocumentChunkRepository chunkRepository,
    ITenantVectorService tenantVectorService) : IHybridRetriever
{
    private const int RrfK = 60;

    public async Task<IEnumerable<RetrievedChunk>> RetrieveAsync(
        RetrieveRequest request,
        CancellationToken ct = default)
    {
        // 1. Embed query first (gives us the vector dimension)
        var queryVector = await embeddingService.EmbedAsync(request.Query, ct);

        // Ensure the collection exists with the correct dimensions.
        // On first ever query this creates the collection; on subsequent calls it is a no-op.
        var (collectionName, _) = await tenantVectorService.EnsureDimensionsCompatibleAsync(
            request.TenantId, queryVector.Length, ct);

        var filters = request.Filters ?? new Dictionary<string, object> { ["tenantId"] = request.TenantId.ToString() };
        var vectorResults = (await vectorStore.SearchAsync(collectionName, queryVector, request.TopK * 2, filters, ct)).ToList();

        // 2. Fetch chunk content from DB for BM25 index
        var chunkIds = vectorResults.Select(r => r.Id).ToHashSet();
        var dbChunks = await chunkRepository.GetByIdsAsync(request.TenantId, chunkIds, ct);
        var dbChunkMap = dbChunks.ToDictionary(c => c.VectorId, c => c);

        // 3. BM25 search (Lucene.Net RAM Directory)
        var bm25Scores = ComputeBM25Scores(request.Query, [.. dbChunks]);

        // 4. RRF fusion
        var vectorRanks = vectorResults
            .Select((r, i) => (Id: r.Id, Rank: i + 1, VectorScore: r.Score))
            .ToDictionary(x => x.Id);

        var bm25Ranks = bm25Scores
            .OrderByDescending(kvp => kvp.Value)
            .Select((kvp, i) => (Id: kvp.Key, Rank: i + 1, BM25Score: kvp.Value))
            .ToDictionary(x => x.Id);

        var allIds = vectorRanks.Keys.Union(bm25Ranks.Keys).ToList();

        var fused = allIds.Select(id =>
        {
            float vectorScore = vectorRanks.TryGetValue(id, out var vr) ? vr.VectorScore : 0f;
            float bm25Score = bm25Ranks.TryGetValue(id, out var br) ? br.BM25Score : 0f;
            int vRank = vectorRanks.TryGetValue(id, out var vRankEntry) ? vRankEntry.Rank : allIds.Count + 1;
            int bRank = bm25Ranks.TryGetValue(id, out var bRankEntry) ? bRankEntry.Rank : allIds.Count + 1;
            float fusionScore = (float)(1.0 / (RrfK + vRank) + 1.0 / (RrfK + bRank));

            dbChunkMap.TryGetValue(id, out var chunk);
            return new RetrievedChunk(
                ChunkId: id,
                Content: chunk?.Content ?? string.Empty,
                VectorScore: vectorScore,
                BM25Score: bm25Score,
                FusionScore: fusionScore,
                Metadata: chunk?.Metadata ?? new ChunkMetadata());
        })
        .OrderByDescending(c => c.FusionScore)
        .Take(request.TopK)
        .ToList();

        return fused;
    }

    private static Dictionary<string, float> ComputeBM25Scores(string query, List<DocumentChunk> chunks)
    {
        var scores = new Dictionary<string, float>();
        if (chunks.Count == 0) return scores;

        using var ramDirectory = new RAMDirectory();
        var analyzer = new StandardAnalyzer(LuceneVersion.LUCENE_48);
        var indexConfig = new IndexWriterConfig(LuceneVersion.LUCENE_48, analyzer);

        using (var writer = new IndexWriter(ramDirectory, indexConfig))
        {
            foreach (var chunk in chunks)
            {
                var doc = new Lucene.Net.Documents.Document
                {
                    new StringField("id", chunk.VectorId, Field.Store.YES),
                    new TextField("content", chunk.Content, Field.Store.NO)
                };
                writer.AddDocument(doc);
            }
            writer.Commit();
        }

        using var reader = DirectoryReader.Open(ramDirectory);
        var searcher = new IndexSearcher(reader);
        var queryParser = new Lucene.Net.QueryParsers.Classic.QueryParser(LuceneVersion.LUCENE_48, "content", analyzer);

        try
        {
            var luceneQuery = queryParser.Parse(Lucene.Net.QueryParsers.Classic.QueryParser.Escape(query));
            var hits = searcher.Search(luceneQuery, chunks.Count);

            foreach (var hit in hits.ScoreDocs)
            {
                var doc = searcher.Doc(hit.Doc);
                var id = doc.Get("id");
                scores[id] = hit.Score;
            }
        }
        catch
        {
            // Query syntax error: skip BM25 and fall back to vector results only
        }

        return scores;
    }
}
