using System.Runtime.CompilerServices;

namespace ManufacturingAI.Core.Interfaces;

public record LLMMessage(string Role, string Content);

public record LLMRequest(
    string SystemPrompt,
    string UserMessage,
    List<LLMMessage>? History = null,
    double Temperature = 0.3,
    // Thinking models (e.g. Gemini 2.5) count internal reasoning tokens against this
    // budget, so a small value can leave no room for the visible answer. Keep it generous.
    int MaxTokens = 8192
);

public record LLMResponse(string Content, int InputTokens, int OutputTokens, bool IsFromFallback);

public interface ILLMService
{
    Task<LLMResponse> CompleteAsync(LLMRequest request, CancellationToken ct = default);

    // Default: non-streaming implementations yield the full response as one chunk.
    async IAsyncEnumerable<string> StreamAsync(
        LLMRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var response = await CompleteAsync(request, ct);
        yield return response.Content;
    }
}
