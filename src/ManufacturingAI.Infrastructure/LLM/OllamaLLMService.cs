using ManufacturingAI.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using OllamaSharp;
using OllamaSharp.Models.Chat;
using System.Runtime.CompilerServices;

namespace ManufacturingAI.Infrastructure.LLM;

public class OllamaLLMService : ILLMService
{
    private readonly OllamaApiClient _client;
    private readonly string _model;

    public OllamaLLMService(IConfiguration config)
    {
        var baseUrl = config["LLM:OllamaBaseUrl"] ?? "http://localhost:11434";
        _model = config["LLM:OllamaChatModel"] ?? "qwen2.5";
        _client = new OllamaApiClient(new Uri(baseUrl));
    }

    public async Task<LLMResponse> CompleteAsync(LLMRequest request, CancellationToken ct = default)
    {
        var content = string.Empty;
        await foreach (var chunk in StreamAsync(request, ct))
            content += chunk;

        return new LLMResponse(content, 0, 0, IsFromFallback: false);
    }

    public async IAsyncEnumerable<string> StreamAsync(
        LLMRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var chatRequest = new ChatRequest
        {
            Model = _model,
            Stream = true,
            Messages = BuildMessages(request)
        };

        await foreach (var chunk in _client.ChatAsync(chatRequest, ct))
        {
            var text = chunk?.Message?.Content;
            if (!string.IsNullOrEmpty(text))
                yield return text;
        }
    }

    private static IEnumerable<Message> BuildMessages(LLMRequest request)
    {
        yield return new Message { Role = ChatRole.System, Content = request.SystemPrompt };

        if (request.History is { Count: > 0 })
        {
            foreach (var msg in request.History)
                yield return new Message
                {
                    Role = msg.Role == "assistant" ? ChatRole.Assistant : ChatRole.User,
                    Content = msg.Content
                };
        }

        yield return new Message { Role = ChatRole.User, Content = request.UserMessage };
    }
}
