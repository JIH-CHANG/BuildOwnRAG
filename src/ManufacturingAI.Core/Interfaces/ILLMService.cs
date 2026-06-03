using System.Runtime.CompilerServices;

namespace ManufacturingAI.Core.Interfaces;

public record LLMMessage(string Role, string Content);

public record LLMRequest(
    string SystemPrompt,
    string UserMessage,
    List<LLMMessage>? History = null,
    double Temperature = 0.3,
    int MaxTokens = 2048
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
