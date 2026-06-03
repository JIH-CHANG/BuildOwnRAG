using ManufacturingAI.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using OllamaSharp;
using OllamaSharp.Models;

namespace ManufacturingAI.Infrastructure.LLM;

public class OllamaEmbeddingService : IEmbeddingService
{
    private readonly OllamaApiClient _client;
    private readonly string _model;

    public int Dimensions { get; }

    public OllamaEmbeddingService(IConfiguration config)
    {
        var baseUrl = config["LLM:OllamaBaseUrl"] ?? "http://localhost:11434";
        _model = config["Embedding:OllamaModel"] ?? "nomic-embed-text";
        Dimensions = int.TryParse(config["Embedding:OllamaDimensions"], out var d) ? d : 768;
        _client = new OllamaApiClient(new Uri(baseUrl));
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var results = await EmbedBatchAsync([text], ct);
        return results.First();
    }

    public async Task<IEnumerable<float[]>> EmbedBatchAsync(
        IEnumerable<string> texts,
        CancellationToken ct = default)
    {
        var request = new EmbedRequest { Model = _model, Input = texts.ToList() };
        var response = await _client.EmbedAsync(request, ct);
        return response.Embeddings.Select(e => e.Select(v => (float)v).ToArray());
    }
}
