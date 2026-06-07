using ManufacturingAI.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using OllamaSharp;
using OllamaSharp.Models;

namespace ManufacturingAI.Infrastructure.LLM;

public class OllamaEmbeddingService : IEmbeddingService
{
    private readonly OllamaApiClient _client;
    private readonly string _model;
    private readonly long _keepAliveSeconds;

    public int Dimensions { get; }

    public OllamaEmbeddingService(IConfiguration config)
    {
        var baseUrl = config["LLM:OllamaBaseUrl"] ?? "http://localhost:11434";
        _model = config["Embedding:OllamaModel"] ?? "nomic-embed-text";
        Dimensions = int.TryParse(config["Embedding:OllamaDimensions"], out var d) ? d : 768;
        // Keep the embedding model loaded between requests (default 10 minutes); -1 = forever.
        _keepAliveSeconds = ParseKeepAliveSeconds(config["LLM:OllamaKeepAlive"]);
        _client = new OllamaApiClient(new Uri(baseUrl));
    }

    // Converts an Ollama keep_alive value ("10m", "30s", "1h", "-1", or plain seconds)
    // into the seconds form expected by EmbedRequest.KeepAlive. Defaults to 600 (10m).
    private static long ParseKeepAliveSeconds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 600;
        value = value.Trim();
        if (long.TryParse(value, out var seconds)) return seconds;
        if (long.TryParse(value[..^1], out var n))
        {
            return char.ToLowerInvariant(value[^1]) switch
            {
                's' => n,
                'm' => n * 60,
                'h' => n * 3600,
                _ => 600
            };
        }
        return 600;
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
        var request = new EmbedRequest { Model = _model, Input = texts.ToList(), KeepAlive = _keepAliveSeconds };
        var response = await _client.EmbedAsync(request, ct);
        return response.Embeddings.Select(e => e.Select(v => (float)v).ToArray());
    }
}
