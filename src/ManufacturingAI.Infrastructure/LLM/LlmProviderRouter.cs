using ManufacturingAI.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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

    private ILLMService Inner
    {
        get
        {
            // Keyed providers are registered lower-case; normalize so a saved
            // value like "Gemini" still resolves the "gemini" service.
            var provider = (runtime.Provider.NullIfEmpty() ?? _defaultProvider).ToLowerInvariant();
            if (string.IsNullOrEmpty(provider))
                throw new InvalidOperationException(
                    "No LLM provider configured. Go to Settings → AI Model and select a provider.");

            if (sp.GetKeyedService<ILLMService>(provider) is { } svc)
                return svc;

            throw new InvalidOperationException(
                $"Provider '{provider}' is not supported as an LLM provider. " +
                "Go to Settings → AI Model and select a supported provider (OpenAI, AzureOpenAI, Gemini, Ollama, Groq, or Claude).");
        }
    }

    public Task<LLMResponse> CompleteAsync(LLMRequest request, CancellationToken ct = default)
        => Inner.CompleteAsync(request, ct);

    public IAsyncEnumerable<string> StreamAsync(LLMRequest request, CancellationToken ct = default)
        => Inner.StreamAsync(request, ct);
}
