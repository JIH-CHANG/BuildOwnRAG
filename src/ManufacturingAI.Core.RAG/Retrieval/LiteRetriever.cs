using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;
using ManufacturingAI.Core.Configuration;
using ManufacturingAI.Core.Models;
using ManufacturingAI.Infrastructure.Repositories;

namespace ManufacturingAI.Core.RAG.Retrieval;

// BM25-only retrieval for Lite mode: SQL keyword prefilter → in-memory Lucene BM25 → top K.
// No embeddings, no Qdrant.
public interface ILiteRetriever
{
    Task<IReadOnlyList<DocumentChunk>> RetrieveAsync(Guid tenantId, string query, CancellationToken ct = default);
}

public class LiteRetriever(
    IDocumentChunkRepository chunkRepository,
    LiteModeOptions options) : ILiteRetriever
{
    public async Task<IReadOnlyList<DocumentChunk>> RetrieveAsync(
        Guid tenantId, string query, CancellationToken ct = default)
    {
        // 1. Coarse prefilter from Postgres (caps candidate set; keeps BM25 cheap).
        var candidates = await chunkRepository.SearchByKeywordAsync(tenantId, query, options.PrefilterLimit, ct);
        if (candidates.Count == 0) return [];

        // 2. BM25 rank the candidates in memory.
        var scores = ComputeBM25Scores(query, candidates);

        return candidates
            .OrderByDescending(c => scores.TryGetValue(c.Id.ToString(), out var s) ? s : 0f)
            .Take(options.TopK)
            .ToList();
    }

    // Lucene.Net RAMDirectory BM25 over the candidate chunks (same approach as HybridRetriever).
    private static Dictionary<string, float> ComputeBM25Scores(string query, IReadOnlyList<DocumentChunk> chunks)
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
                writer.AddDocument(new Lucene.Net.Documents.Document
                {
                    new StringField("id", chunk.Id.ToString(), Field.Store.YES),
                    new TextField("content", chunk.Content, Field.Store.NO)
                });
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
                scores[searcher.Doc(hit.Doc).Get("id")] = hit.Score;
        }
        catch
        {
            // Query syntax error: leave scores empty → caller keeps prefilter order.
        }

        return scores;
    }
}
