using ManufacturingAI.Core.Interfaces;
using ManufacturingAI.Core.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.CompilerServices;

namespace ManufacturingAI.Infrastructure.LLM;

/// <summary>
/// Routes LLM calls to the correct keyed service based on the current LlmRuntimeConfig.Provider.
/// Wraps that service with a circuit breaker for resilience.
/// Resolves the keyed service on each call so provider switches take effect immediately.
/// </summary>
public sealed class LlmProviderRouter(
    IServiceProvider sp,
    LlmRuntimeConfig runtime,
    IConfiguration config) : ILLMService
{
    // Empty string from config means "not configured" — treat as null and fall back to "openai"
    private readonly string _defaultProvider =
        (config["Llm:Provider"].NullIfEmpty() ?? "openai").ToLowerInvariant();

    private (ILLMService Service, string Provider) Resolve()
    {
        // Keyed providers are registered lower-case; normalize so a saved
        // value like "Gemini" still resolves the "gemini" service.
        var provider = (runtime.Provider.NullIfEmpty() ?? _defaultProvider).ToLowerInvariant();
        if (string.IsNullOrEmpty(provider))
            throw new InvalidOperationException(
                "No LLM provider configured. Go to Settings → AI Model and select a provider.");

        if (sp.GetKeyedService<ILLMService>(provider) is { } svc)
            return (svc, provider);

        throw new InvalidOperationException(
            $"Provider '{provider}' is not supported as an LLM provider. " +
            "Go to Settings → AI Model and select a supported provider (OpenAI, AzureOpenAI, Gemini, Ollama, Groq, or Claude).");
    }

    public async Task<LLMResponse> CompleteAsync(LLMRequest request, CancellationToken ct = default)
    {
        var (svc, provider) = Resolve();
        try
        {
            var response = await svc.CompleteAsync(request, ct);
            Record(provider, "complete", response.IsFromFallback ? "fallback" : "ok");
            return response;
        }
        catch
        {
            Record(provider, "complete", "error");
            throw;
        }
    }

    public async IAsyncEnumerable<string> StreamAsync(
        LLMRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var (svc, provider) = Resolve();
        // Manual enumeration: C# forbids yield inside try-catch, and a failure
        // mid-stream is exactly what this metric needs to capture.
        await using var e = svc.StreamAsync(request, ct).GetAsyncEnumerator(ct);
        while (true)
        {
            bool hasNext;
            try
            {
                hasNext = await e.MoveNextAsync();
            }
            catch
            {
                Record(provider, "stream", "error");
                throw;
            }
            if (!hasNext) break;
            yield return e.Current;
        }
        Record(provider, "stream", "ok");
    }

    private static void Record(string provider, string operation, string outcome)
        => AppMetrics.ProviderCalls.Add(1,
            new("kind", "llm"), new("provider", provider),
            new("operation", operation), new("outcome", outcome));
}
