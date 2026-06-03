using ManufacturingAI.Core.Models;
using ManufacturingAI.Core.RAG.Reranking;
using ManufacturingAI.Core.RAG.Retrieval;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;
using System.Text.Json;

namespace ManufacturingAI.Unit.Tests.Reranking;

public class CohereRerankerTests
{
    // ── Fake HTTP handler ─────────────────────────────────────────────────────

    private sealed class FakeHttpHandler(Func<HttpResponseMessage> factory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(factory());
    }

    private static CohereReranker BuildReranker(Func<HttpResponseMessage> httpFactory)
    {
        var handler   = new FakeHttpHandler(httpFactory);
        var client    = new HttpClient(handler) { BaseAddress = new Uri("https://api.cohere.com/") };
        var fallback  = new CosineSimilarityReranker();
        var logger    = NullLogger<CohereReranker>.Instance;
        return new CohereReranker(client, fallback, logger);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static RetrievedChunk MakeChunk(string id, float vectorScore = 0.5f) =>
        new(ChunkId: id,
            Content: $"Content of chunk {id}",
            VectorScore: vectorScore,
            BM25Score: 0f,
            FusionScore: 0f,
            Metadata: new ChunkMetadata());

    /// <summary>Build a Cohere-style rerank response body.</summary>
    private static HttpResponseMessage CohereResponse(params (int Index, float Score)[] results)
    {
        var body = new
        {
            results = results.Select(r => new { index = r.Index, relevance_score = r.Score })
        };
        var json = JsonSerializer.Serialize(body);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    // ── Edge cases ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RerankAsync_EmptyInput_ReturnsEmptyWithoutHttpCall()
    {
        var httpCallCount = 0;
        var reranker = BuildReranker(() =>
        {
            httpCallCount++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var result = await reranker.RerankAsync("query", [], topK: 5);

        result.Should().BeEmpty();
        httpCallCount.Should().Be(0, "empty input must short-circuit before any HTTP call");
    }

    // ── Success path ──────────────────────────────────────────────────────────

    [Fact]
    public async Task RerankAsync_CohereSuccess_ReturnsChunksInCohereOrder()
    {
        // Three chunks: Cohere says index 2 is most relevant, then 0, then 1
        var chunks = new[]
        {
            MakeChunk("a"),   // index 0
            MakeChunk("b"),   // index 1
            MakeChunk("c"),   // index 2
        };

        var reranker = BuildReranker(() => CohereResponse((2, 0.95f), (0, 0.70f), (1, 0.40f)));
        var result   = (await reranker.RerankAsync("query", chunks, topK: 3)).ToList();

        // Use explicit array overload so "because" is not mistaken for a 4th expected item
        result.Select(c => c.ChunkId)
            .Should().Equal(new[] { "c", "a", "b" },
                because: "order must follow Cohere's relevance_score descending");
    }

    [Fact]
    public async Task RerankAsync_CohereSuccess_RespectsTopK()
    {
        var chunks = new[]
        {
            MakeChunk("a"),   // index 0
            MakeChunk("b"),   // index 1
            MakeChunk("c"),   // index 2
        };

        // CohereReranker sends TopN=2; fake simulates Cohere honouring that by returning only 2 results
        var reranker = BuildReranker(() => CohereResponse((2, 0.95f), (0, 0.70f)));
        var result   = (await reranker.RerankAsync("query", chunks, topK: 2)).ToList();

        result.Should().HaveCount(2);
        result[0].ChunkId.Should().Be("c");
        result[1].ChunkId.Should().Be("a");
    }

    // ── Fallback on HTTP failure ──────────────────────────────────────────────

    [Fact]
    public async Task RerankAsync_HttpServerError_FallsBackToCosineSimilarity()
    {
        var chunks = new[]
        {
            MakeChunk("low",  vectorScore: 0.30f),
            MakeChunk("high", vectorScore: 0.90f),
        };

        var reranker = BuildReranker(() => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var result   = (await reranker.RerankAsync("query", chunks, topK: 2)).ToList();

        // Cosine fallback should sort by VectorScore descending
        result[0].ChunkId.Should().Be("high");
        result[1].ChunkId.Should().Be("low");
    }

    [Fact]
    public async Task RerankAsync_HttpThrows_FallsBackToCosineSimilarity()
    {
        var chunks = new[]
        {
            MakeChunk("low",  vectorScore: 0.30f),
            MakeChunk("high", vectorScore: 0.90f),
        };

        var handler  = new FakeHttpHandler(() => throw new HttpRequestException("network error"));
        var client   = new HttpClient(handler) { BaseAddress = new Uri("https://api.cohere.com/") };
        var reranker = new CohereReranker(client, new CosineSimilarityReranker(), NullLogger<CohereReranker>.Instance);

        var result = (await reranker.RerankAsync("query", chunks, topK: 2)).ToList();

        result[0].ChunkId.Should().Be("high",
            "when HTTP throws, cosine fallback should rank by VectorScore");
    }

    // ── Fallback on empty Cohere response ─────────────────────────────────────

    [Fact]
    public async Task RerankAsync_EmptyCohereResults_FallsBackToCosineSimilarity()
    {
        var chunks = new[]
        {
            MakeChunk("low",  vectorScore: 0.30f),
            MakeChunk("high", vectorScore: 0.90f),
        };

        // Cohere returns a valid 200 but with an empty results array
        var reranker = BuildReranker(() =>
        {
            var json = JsonSerializer.Serialize(new { results = Array.Empty<object>() });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        var result = (await reranker.RerankAsync("query", chunks, topK: 2)).ToList();

        result[0].ChunkId.Should().Be("high",
            "empty Cohere results should trigger cosine fallback");
    }
}
