using ManufacturingAI.Core.Models;
using ManufacturingAI.Core.RAG.Reranking;
using ManufacturingAI.Core.RAG.Retrieval;

namespace ManufacturingAI.Unit.Tests.Reranking;

public class CosineSimilarityRerankerTests
{
    private readonly CosineSimilarityReranker _reranker = new();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static RetrievedChunk MakeChunk(string id, float vectorScore, float bm25Score = 0f) =>
        new(ChunkId: id,
            Content: $"Content of chunk {id}",
            VectorScore: vectorScore,
            BM25Score: bm25Score,
            FusionScore: 0f,
            Metadata: new ChunkMetadata());

    // ── Edge cases ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RerankAsync_EmptyInput_ReturnsEmpty()
    {
        var result = await _reranker.RerankAsync("query", [], topK: 5);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task RerankAsync_SingleChunk_ReturnsThatChunk()
    {
        var chunk = MakeChunk("a", vectorScore: 0.9f);
        var result = (await _reranker.RerankAsync("query", [chunk], topK: 5)).ToList();

        result.Should().HaveCount(1);
        result[0].ChunkId.Should().Be("a");
    }

    // ── TopK behaviour ────────────────────────────────────────────────────────

    [Fact]
    public async Task RerankAsync_TopKLimitsOutput()
    {
        var chunks = Enumerable.Range(1, 10)
            .Select(i => MakeChunk($"c{i}", vectorScore: i * 0.1f))
            .ToList();

        var result = await _reranker.RerankAsync("query", chunks, topK: 3);

        result.Should().HaveCount(3);
    }

    [Fact]
    public async Task RerankAsync_TopKLargerThanInput_ReturnsAll()
    {
        var chunks = new[] { MakeChunk("a", 0.5f), MakeChunk("b", 0.6f) };
        var result = await _reranker.RerankAsync("query", chunks, topK: 100);

        result.Should().HaveCount(2);
    }

    // ── Ordering ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task RerankAsync_OrdersByVectorScoreDescending()
    {
        // Arrange: deliberately shuffled order
        var chunks = new[]
        {
            MakeChunk("low",    vectorScore: 0.50f),
            MakeChunk("high",   vectorScore: 0.95f),
            MakeChunk("medium", vectorScore: 0.70f),
        };

        var result = (await _reranker.RerankAsync("any query", chunks, topK: 3)).ToList();

        result[0].ChunkId.Should().Be("high");
        result[1].ChunkId.Should().Be("medium");
        result[2].ChunkId.Should().Be("low");
    }

    [Fact]
    public async Task RerankAsync_TopKTakesHighestVectorScores()
    {
        var chunks = new[]
        {
            MakeChunk("rank1", vectorScore: 0.95f),
            MakeChunk("rank2", vectorScore: 0.80f),
            MakeChunk("rank3", vectorScore: 0.60f),
            MakeChunk("rank4", vectorScore: 0.40f),
        };

        var result = (await _reranker.RerankAsync("query", chunks, topK: 2)).ToList();

        result.Select(c => c.ChunkId).Should().Equal("rank1", "rank2");
    }

    // ── BM25 score must NOT affect ordering ───────────────────────────────────

    [Fact]
    public async Task RerankAsync_HighBM25ButLowVectorScore_DoesNotRankHigher()
    {
        // "bm25winner" has a great BM25 score but a poor VectorScore
        // "vectorwinner" has zero BM25 but a high VectorScore
        // CosineSimilarityReranker must rank by VectorScore only
        var chunks = new[]
        {
            MakeChunk("bm25winner",    vectorScore: 0.30f, bm25Score: 99f),
            MakeChunk("vectorwinner",  vectorScore: 0.90f, bm25Score:  0f),
        };

        var result = (await _reranker.RerankAsync("query", chunks, topK: 2)).ToList();

        result[0].ChunkId.Should().Be("vectorwinner",
            "cosine reranker must rank by VectorScore, ignoring BM25Score");
    }
}
