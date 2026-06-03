using ManufacturingAI.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ManufacturingAI.Infrastructure.LLM;

public sealed class EmbeddingProviderRouter(
    IServiceProvider sp,
    LlmRuntimeConfig runtime,
    IConfiguration config) : IEmbeddingService
{
    private readonly string _defaultProvider =
        (config["Embedding:Provider"].NullIfEmpty()
         ?? config["Llm:Provider"].NullIfEmpty()
         ?? "openai").ToLowerInvariant();

    private IEmbeddingService Inner
    {
        get
        {
            // EmbeddingProvider takes precedence; falls back to LLM Provider.
            // This allows Groq (LLM-only) to use a different provider for embeddings.
            var provider = runtime.EmbeddingProvider.NullIfEmpty()
                ?? runtime.Provider.NullIfEmpty()
                ?? _defaultProvider;

            if (string.IsNullOrEmpty(provider))
                throw new InvalidOperationException(
                    "No embedding provider configured. Go to Settings → AI Model and select an Embedding Provider.");

            if (sp.GetKeyedService<IEmbeddingService>(provider) is { } svc)
                return svc;

            throw new InvalidOperationException(
                $"Provider '{provider}' does not support embeddings. " +
                "Go to Settings → AI Model and select a separate Embedding Provider (e.g. OpenAI or Gemini).");
        }
    }

    public int Dimensions => Inner.Dimensions;

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        => Inner.EmbedAsync(text, ct);

    public Task<IEnumerable<float[]>> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken ct = default)
        => Inner.EmbedBatchAsync(texts, ct);
}
