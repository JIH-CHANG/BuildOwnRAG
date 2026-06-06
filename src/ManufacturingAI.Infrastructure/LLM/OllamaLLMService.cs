using ManufacturingAI.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using OllamaSharp;
using OllamaSharp.Models.Chat;
using System.Runtime.CompilerServices;

namespace ManufacturingAI.Infrastructure.LLM;

public class OllamaLLMService : ILLMService
{
    private readonly OllamaApiClient _client;
    private readonly string _defaultModel;
    private readonly string _keepAlive;
    private readonly LlmRuntimeConfig _runtime;

    public OllamaLLMService(IConfiguration config, LlmRuntimeConfig runtime)
    {
        var baseUrl = config["LLM:OllamaBaseUrl"] ?? "http://localhost:11434";
        _defaultModel = config["LLM:OllamaChatModel"] ?? "qwen2.5";
        // How long Ollama keeps the model loaded after a request. "10m" avoids a cold
        // reload on every question; "-1" keeps it loaded forever; "0" unloads immediately.
        _keepAlive = config["LLM:OllamaKeepAlive"].NullIfEmpty() ?? "10m";
        _runtime = runtime;
        _client = new OllamaApiClient(new Uri(baseUrl));
    }

    private string Model => _runtime.Model.NullIfEmpty() ?? _defaultModel;

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
            Model = Model,
            Stream = true,
            Messages = BuildMessages(request),
            KeepAlive = _keepAlive
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
