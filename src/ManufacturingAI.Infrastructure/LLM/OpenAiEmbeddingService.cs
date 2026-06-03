using ManufacturingAI.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using OpenAI;
using OpenAI.Embeddings;
using System.ClientModel;

namespace ManufacturingAI.Infrastructure.LLM;

public class OpenAiEmbeddingService : IEmbeddingService
{
    private const int BatchSize = 100;

    private readonly IConfiguration _config;
    protected readonly LlmRuntimeConfig Runtime;

    public int Dimensions { get; }

    public OpenAiEmbeddingService(IConfiguration config, LlmRuntimeConfig runtime)
    {
        _config = config;
        Runtime = runtime;
        Dimensions = int.TryParse(config["LLM:EmbeddingDimensions"], out var d) ? d : 1536;
    }

    // No caching — always build from current runtime config so key/model changes take effect immediately.
    protected EmbeddingClient EmbeddingClient => CreateClient(_config);

    protected virtual EmbeddingClient CreateClient(IConfiguration config)
    {
        var apiKey = Runtime.EmbeddingApiKey.NullIfEmpty()
            ?? Runtime.ApiKey.NullIfEmpty()
            ?? config["OpenAI:ApiKey"]
            ?? string.Empty;

        if (string.IsNullOrEmpty(apiKey))
            throw new InvalidOperationException(
                "OpenAI API key is not configured. Please go to Settings → AI Model and enter your OpenAI API key.");

        var model = Runtime.EmbeddingModel.NullIfEmpty()
            ?? config["LLM:EmbeddingModel"]
            ?? "text-embedding-3-small";
        return new OpenAIClient(new ApiKeyCredential(apiKey)).GetEmbeddingClient(model);
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var result = await EmbeddingClient.GenerateEmbeddingAsync(text, cancellationToken: ct);
        return result.Value.ToFloats().ToArray();
    }

    public async Task<IEnumerable<float[]>> EmbedBatchAsync(
        IEnumerable<string> texts,
        CancellationToken ct = default)
    {
        var all = texts.ToList();
        var output = new List<float[]>(all.Count);

        for (var i = 0; i < all.Count; i += BatchSize)
        {
            var batch = all.Skip(i).Take(BatchSize).ToList();
            var result = await EmbeddingClient.GenerateEmbeddingsAsync(batch, cancellationToken: ct);
            output.AddRange(result.Value
                .OrderBy(e => e.Index)
                .Select(e => e.ToFloats().ToArray()));
        }

        return output;
    }
}
