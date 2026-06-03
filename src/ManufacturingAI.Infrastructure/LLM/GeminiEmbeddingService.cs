using ManufacturingAI.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace ManufacturingAI.Infrastructure.LLM;

public sealed class GeminiEmbeddingService : IEmbeddingService
{
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models";
    private const int BatchSize = 100;

    private readonly HttpClient _http;
    private readonly LlmRuntimeConfig _runtime;
    private readonly string _fallbackApiKey;
    private readonly string _fallbackEmbeddingModel;

    public int Dimensions { get; }

    public GeminiEmbeddingService(IConfiguration config, IHttpClientFactory httpClientFactory, LlmRuntimeConfig runtime)
    {
        _runtime = runtime;
        _fallbackApiKey = config["GEMINI_API_KEY"] ?? string.Empty;
        _fallbackEmbeddingModel = config["GEMINI_EMBEDDING_MODEL"] ?? "gemini-embedding-exp";
        Dimensions = 768;
        _http = httpClientFactory.CreateClient(nameof(GeminiEmbeddingService));
    }

    // Use dedicated embedding key first, then fall back to the shared LLM key
    private string ApiKey => _runtime.EmbeddingApiKey.NullIfEmpty()
        ?? _runtime.ApiKey.NullIfEmpty()
        ?? _fallbackApiKey;
    private string EmbeddingModel => _runtime.EmbeddingModel.NullIfEmpty() ?? _fallbackEmbeddingModel;

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/{EmbeddingModel}:embedContent?key={ApiKey}";
        var body = new { content = new { parts = new[] { new { text } } } };

        using var response = await PostWithRetryAsync(url, body, ct);
        await EnsureGeminiSuccessAsync(response, EmbeddingModel, ct);

        var result = await response.Content
            .ReadFromJsonAsync<EmbedContentResponse>(cancellationToken: ct);
        return result?.Embedding?.Values ?? [];
    }

    public async Task<IEnumerable<float[]>> EmbedBatchAsync(
        IEnumerable<string> texts,
        CancellationToken ct = default)
    {
        var all = texts.ToList();
        var output = new List<float[]>(all.Count);

        for (var i = 0; i < all.Count; i += BatchSize)
        {
            var batch = all.Skip(i).Take(BatchSize);
            var url = $"{BaseUrl}/{EmbeddingModel}:batchEmbedContents?key={ApiKey}";
            var body = new
            {
                requests = batch.Select(t => new
                {
                    model = $"models/{EmbeddingModel}",
                    content = new { parts = new[] { new { text = t } } }
                }).ToArray()
            };

            using var response = await PostWithRetryAsync(url, body, ct);
            await EnsureGeminiSuccessAsync(response, EmbeddingModel, ct);

            var result = await response.Content
                .ReadFromJsonAsync<BatchEmbedContentsResponse>(cancellationToken: ct);
            if (result?.Embeddings is not null)
                output.AddRange(result.Embeddings.Select(e => e.Values));
        }

        return output;
    }

    // Retries on 429 with exponential backoff (2s → 4s → 8s)
    private async Task<HttpResponseMessage> PostWithRetryAsync(string url, object body, CancellationToken ct)
    {
        HttpResponseMessage response = null!;
        for (int attempt = 0; attempt <= 3; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
            response = await _http.PostAsJsonAsync(url, body, ct);
            if (response.StatusCode != System.Net.HttpStatusCode.TooManyRequests)
                break;
        }
        return response;
    }

    private static async Task EnsureGeminiSuccessAsync(HttpResponseMessage response, string model, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync(ct);
        throw response.StatusCode switch
        {
            System.Net.HttpStatusCode.NotFound =>
                new InvalidOperationException(
                    $"Gemini model '{model}' not found. Go to Settings → AI Model and select a valid model."),
            System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden =>
                new InvalidOperationException(
                    "Invalid Gemini API key. Go to Settings → AI Model and update your API key."),
            System.Net.HttpStatusCode.TooManyRequests =>
                new InvalidOperationException("Gemini rate limit exceeded. Please try again later."),
            _ =>
                new InvalidOperationException(
                    $"Gemini API error {(int)response.StatusCode}: {body}")
        };
    }

    private sealed record EmbedContentResponse(
        [property: JsonPropertyName("embedding")] EmbeddingValues? Embedding);

    private sealed record BatchEmbedContentsResponse(
        [property: JsonPropertyName("embeddings")] List<EmbeddingValues>? Embeddings);

    private sealed record EmbeddingValues(
        [property: JsonPropertyName("values")] float[] Values);
}
