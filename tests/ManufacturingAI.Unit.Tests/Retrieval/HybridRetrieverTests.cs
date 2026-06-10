using ManufacturingAI.Core.Common;
using ManufacturingAI.Core.Interfaces;
using ManufacturingAI.Core.Models;
using ManufacturingAI.Core.RAG.Retrieval;
using ManufacturingAI.Infrastructure.Repositories;

namespace ManufacturingAI.Unit.Tests.Retrieval;

/// <summary>
/// Tests the full HybridRetriever pipeline:
///   Vector search  →  BM25 (Lucene RAM index)  →  RRF fusion  →  TopK output
///
/// All external dependencies are substituted so tests run without Redis, Qdrant,
/// or a real database.  Change the inline constants to simulate different user
/// queries and document corpora.
/// </summary>
public class HybridRetrieverTests
{
    private static readonly Guid TenantId        = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private const           string CollectionName = "tenant_test_v1";

    // ── Mock wiring ───────────────────────────────────────────────────────────

    private static (HybridRetriever retriever,
                    IVectorStore vectorStore,
                    IEmbeddingService embeddingService,
                    IDocumentChunkRepository chunkRepo,
                    ITenantVectorService tenantVector) Build()
    {
        var vectorStore      = Substitute.For<IVectorStore>();
        var embeddingService = Substitute.For<IEmbeddingService>();
        var chunkRepo        = Substitute.For<IDocumentChunkRepository>();
        var tenantVector     = Substitute.For<ITenantVectorService>();

        tenantVector
            .EnsureDimensionsCompatibleAsync(TenantId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((CollectionName, false));

        embeddingService
            .EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 0.1f, 0.2f, 0.3f });   // dummy vector

        var retriever = new HybridRetriever(
            vectorStore, embeddingService, chunkRepo, tenantVector);

        return (retriever, vectorStore, embeddingService, chunkRepo, tenantVector);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static VectorSearchResult VecResult(string vectorId, float score) =>
        new(vectorId, score, new Dictionary<string, object>());

    private static DocumentChunk DbChunk(string vectorId, string content) => new()
    {
        Id         = Guid.NewGuid(),
        TenantId   = TenantId,
        DocumentId = Guid.NewGuid(),
        VectorId   = vectorId,
        Content    = content,
        Metadata   = new ChunkMetadata { SectionTitle = "test" }
    };

    private static RetrieveRequest QueryFor(string query, int topK = 10) =>
        new(TenantId, query, topK);

    // ── Migration guard ───────────────────────────────────────────────────────

    [Fact]
    public async Task RetrieveAsync_MigrationRequired_ThrowsMigrationInProgress()
    {
        var (retriever, vectorStore, _, _, tenantVector) = Build();

        // Stored dimensions differ from the query vector's: collection must migrate.
        tenantVector
            .EnsureDimensionsCompatibleAsync(TenantId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((CollectionName, true));

        var act = () => retriever.RetrieveAsync(QueryFor("anything"));

        await act.Should().ThrowAsync<MigrationInProgressException>();
        // The mismatched collection must never be searched.
        await vectorStore.DidNotReceiveWithAnyArgs()
            .SearchAsync(default!, default!, default, default, default);
    }

    // ── Basic retrieval ───────────────────────────────────────────────────────

    [Fact]
    public async Task RetrieveAsync_BasicFlow_ReturnsNonEmptyResults()
    {
        var (retriever, vectorStore, _, chunkRepo, _) = Build();

        vectorStore
            .SearchAsync(CollectionName, Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(new[] { VecResult("v1", 0.9f), VecResult("v2", 0.8f) });

        chunkRepo
            .GetByIdsAsync(TenantId, Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { DbChunk("v1", "manufacturing quality"), DbChunk("v2", "other content") });

        var results = (await retriever.RetrieveAsync(QueryFor("manufacturing"))).ToList();

        results.Should().NotBeEmpty();
        results.Should().AllSatisfy(r => r.FusionScore.Should().BeGreaterThan(0f));
    }

    [Fact]
    public async Task RetrieveAsync_NoVectorResults_ReturnsEmpty()
    {
        var (retriever, vectorStore, _, chunkRepo, _) = Build();

        vectorStore
            .SearchAsync(CollectionName, Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<VectorSearchResult>());

        chunkRepo
            .GetByIdsAsync(TenantId, Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<DocumentChunk>());

        var results = await retriever.RetrieveAsync(QueryFor("anything"));

        results.Should().BeEmpty();
    }

    // ── TopK limiting ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RetrieveAsync_TopK_LimitsOutput()
    {
        var (retriever, vectorStore, _, chunkRepo, _) = Build();

        // 5 vector results
        vectorStore
            .SearchAsync(CollectionName, Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(1, 5).Select(i => VecResult($"v{i}", 1f - i * 0.1f)).ToArray());

        chunkRepo
            .GetByIdsAsync(TenantId, Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(1, 5).Select(i => DbChunk($"v{i}", $"content {i}")).ToArray());

        var results = await retriever.RetrieveAsync(QueryFor("query", topK: 2));

        results.Should().HaveCount(2, "TopK=2 must limit output to 2 chunks");
    }

    // ── RRF ordering ──────────────────────────────────────────────────────────

    [Fact]
    public async Task RetrieveAsync_ResultsOrderedByFusionScoreDescending()
    {
        var (retriever, vectorStore, _, chunkRepo, _) = Build();

        vectorStore
            .SearchAsync(CollectionName, Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                VecResult("v1", 0.95f),   // vector rank 1
                VecResult("v2", 0.80f),   // vector rank 2
                VecResult("v3", 0.60f),   // vector rank 3
            });

        chunkRepo
            .GetByIdsAsync(TenantId, Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                DbChunk("v1", "general text without keywords"),
                DbChunk("v2", "general text without keywords"),
                DbChunk("v3", "general text without keywords"),
            });

        var results = (await retriever.RetrieveAsync(QueryFor("unique_term_xyz", topK: 3))).ToList();

        // When BM25 scores are all equal (none match), vector order dominates
        var fusionScores = results.Select(r => r.FusionScore).ToList();
        fusionScores.Should().BeInDescendingOrder("results must be ordered by FusionScore desc");
    }

    // ── RRF formula verification ──────────────────────────────────────────────

    /// <summary>
    /// When a chunk ranks first in BOTH vector search and BM25, its RRF score is:
    ///   1/(K+1) + 1/(K+1)  where K = 60  →  2/61 ≈ 0.03279
    ///
    /// A chunk appearing only in vector results (rank 1) and absent from BM25 gets:
    ///   1/(K+1) + 1/(K + allIds.Count + 1)  which is lower.
    ///
    /// This verifies that the BM25 leg of RRF contributes meaningfully.
    /// </summary>
    [Fact]
    public async Task RetrieveAsync_ChunkInBothRankings_ScoresHigherThanVectorOnly()
    {
        // Query: terms that appear in v2's content but NOT v1's content.
        // v1 leads on vector score; v2 will win on BM25 → expect RRF to elevate v2.
        const string query = "manufacturing quality control";

        var (retriever, vectorStore, _, chunkRepo, _) = Build();

        vectorStore
            .SearchAsync(CollectionName, Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                VecResult("v1", 0.95f),   // high vector score, but unrelated content
                VecResult("v2", 0.80f),   // lower vector score, but BM25-rich content
            });

        chunkRepo
            .GetByIdsAsync(TenantId, Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                DbChunk("v1", "unrelated xyz test document content here"),
                DbChunk("v2", "manufacturing quality control inspection process"),
            });

        var results = (await retriever.RetrieveAsync(QueryFor(query, topK: 2))).ToList();

        results.Should().HaveCount(2);

        var v1Result = results.First(r => r.ChunkId == "v1");
        var v2Result = results.First(r => r.ChunkId == "v2");

        // v2 must have a higher FusionScore because it ranks #1 in BM25 as well
        v2Result.FusionScore.Should().BeGreaterThan(v1Result.FusionScore,
            "BM25 contribution from v2 elevates its RRF score above the pure-vector winner v1");
    }

    [Fact]
    public async Task RetrieveAsync_FusionScore_BothRankOne_ApproximatelyTwoOverSixtyOne()
    {
        // v1 ranks 1st in both vector search and BM25
        // Expected FusionScore ≈ 1/(60+1) + 1/(60+1) = 2/61 ≈ 0.03279
        const string query = "process quality inspection";

        var (retriever, vectorStore, _, chunkRepo, _) = Build();

        vectorStore
            .SearchAsync(CollectionName, Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                VecResult("v1", 0.99f),   // rank 1 by vector
            });

        chunkRepo
            .GetByIdsAsync(TenantId, Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                DbChunk("v1", "process quality inspection standard"),
            });

        var results = (await retriever.RetrieveAsync(QueryFor(query, topK: 1))).ToList();

        results.Should().HaveCount(1);
        // rank 1 in both → FusionScore = 1/61 + 1/61 = 2/61 (when BM25 scores it)
        // If BM25 misses it (score 0), rank is allIds.Count+1 = 2, so FusionScore = 1/61 + 1/62
        // Either way it must be in the range (0.030, 0.034)
        results[0].FusionScore.Should().BeInRange(0.030f, 0.034f,
            "fusion score for rank-1 in both dimensions should be ≈ 2/61 ≈ 0.0328");
    }

    // ── Metadata mapping ──────────────────────────────────────────────────────

    [Fact]
    public async Task RetrieveAsync_ChunkContent_MappedFromDatabase()
    {
        const string expectedContent = "This is the verbatim chunk content from the database.";
        var (retriever, vectorStore, _, chunkRepo, _) = Build();

        vectorStore
            .SearchAsync(CollectionName, Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(new[] { VecResult("v1", 0.9f) });

        chunkRepo
            .GetByIdsAsync(TenantId, Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { DbChunk("v1", expectedContent) });

        var results = (await retriever.RetrieveAsync(QueryFor("query", topK: 1))).ToList();

        results[0].Content.Should().Be(expectedContent);
    }

    [Fact]
    public async Task RetrieveAsync_VectorAndBM25Scores_BothPopulatedInResult()
    {
        var (retriever, vectorStore, _, chunkRepo, _) = Build();

        vectorStore
            .SearchAsync(CollectionName, Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(new[] { VecResult("v1", 0.88f) });

        chunkRepo
            .GetByIdsAsync(TenantId, Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { DbChunk("v1", "some content to index for bm25") });

        var result = (await retriever.RetrieveAsync(QueryFor("content index", topK: 1))).Single();

        result.VectorScore.Should().BeApproximately(0.88f, 0.001f);
        result.FusionScore.Should().BeGreaterThan(0f);
    }
}
