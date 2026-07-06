using ManufacturingAI.Core.Interfaces;
using ManufacturingAI.Core.Observability;
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

    private (IEmbeddingService Service, string Provider) Resolve()
    {
        // EmbeddingProvider takes precedence; falls back to LLM Provider.
        // This allows Groq (LLM-only) to use a different provider for embeddings.
        // Keyed providers are registered lower-case; normalize so a saved
        // value like "Gemini" still resolves the "gemini" service.
        var provider = (runtime.EmbeddingProvider.NullIfEmpty()
            ?? runtime.Provider.NullIfEmpty()
            ?? _defaultProvider).ToLowerInvariant();

        if (string.IsNullOrEmpty(provider))
            throw new InvalidOperationException(
                "No embedding provider configured. Go to Settings → AI Model and select an Embedding Provider.");

        if (sp.GetKeyedService<IEmbeddingService>(provider) is { } svc)
            return (svc, provider);

        throw new InvalidOperationException(
            $"Provider '{provider}' does not support embeddings. " +
            "Go to Settings → AI Model and select a separate Embedding Provider (e.g. OpenAI or Gemini).");
    }

    public int Dimensions => Resolve().Service.Dimensions;

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var (svc, provider) = Resolve();
        try
        {
            var vector = await svc.EmbedAsync(text, ct);
            Record(provider, "embed", "ok");
            return vector;
        }
        catch
        {
            Record(provider, "embed", "error");
            throw;
        }
    }

    public async Task<IEnumerable<float[]>> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken ct = default)
    {
        var (svc, provider) = Resolve();
        try
        {
            var vectors = await svc.EmbedBatchAsync(texts, ct);
            Record(provider, "embed_batch", "ok");
            return vectors;
        }
        catch
        {
            Record(provider, "embed_batch", "error");
            throw;
        }
    }

    private static void Record(string provider, string operation, string outcome)
        => AppMetrics.ProviderCalls.Add(1,
            new("kind", "embedding"), new("provider", provider),
            new("operation", operation), new("outcome", outcome));
}
