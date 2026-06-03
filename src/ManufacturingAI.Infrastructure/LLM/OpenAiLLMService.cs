using ManufacturingAI.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.Runtime.CompilerServices;

namespace ManufacturingAI.Infrastructure.LLM;

public class OpenAiLLMService : ILLMService
{
    private readonly IConfiguration _config;
    protected readonly LlmRuntimeConfig Runtime;

    public OpenAiLLMService(IConfiguration config, LlmRuntimeConfig runtime)
    {
        _config = config;
        Runtime = runtime;
    }

    // No caching — always build from current runtime config so key/model changes take effect immediately.
    protected ChatClient ChatClient => CreateChatClient(_config);

    protected virtual ChatClient CreateChatClient(IConfiguration config)
    {
        var apiKey = Runtime.ApiKey.NullIfEmpty()
            ?? config["OpenAI:ApiKey"]
            ?? string.Empty;

        if (string.IsNullOrEmpty(apiKey))
            throw new InvalidOperationException(
                "OpenAI API key is not configured. Please go to Settings → AI Model and enter your OpenAI API key.");

        var model = Runtime.Model.NullIfEmpty()
            ?? config["Llm:Model"]
            ?? "gpt-4o-mini";

        return new OpenAIClient(new ApiKeyCredential(apiKey)).GetChatClient(model);
    }

    public async Task<LLMResponse> CompleteAsync(LLMRequest request, CancellationToken ct = default)
    {
        var messages = BuildMessages(request);
        var options = new ChatCompletionOptions
        {
            Temperature = (float)request.Temperature,
            MaxOutputTokenCount = request.MaxTokens
        };

        var completion = await ChatClient.CompleteChatAsync(messages, options, ct);
        var value = completion.Value;
        return new LLMResponse(
            value.Content[0].Text,
            value.Usage.InputTokenCount,
            value.Usage.OutputTokenCount,
            IsFromFallback: false);
    }

    public async IAsyncEnumerable<string> StreamAsync(
        LLMRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var messages = BuildMessages(request);
        var options = new ChatCompletionOptions
        {
            Temperature = (float)request.Temperature,
            MaxOutputTokenCount = request.MaxTokens
        };

        await foreach (var update in ChatClient.CompleteChatStreamingAsync(messages, options, ct))
        {
            foreach (var part in update.ContentUpdate)
            {
                if (!string.IsNullOrEmpty(part.Text))
                    yield return part.Text;
            }
        }
    }

    protected static List<ChatMessage> BuildMessages(LLMRequest request)
    {
        var messages = new List<ChatMessage> { new SystemChatMessage(request.SystemPrompt) };

        if (request.History is { Count: > 0 })
        {
            foreach (var msg in request.History)
            {
                messages.Add(msg.Role == "assistant"
                    ? new AssistantChatMessage(msg.Content)
                    : new UserChatMessage(msg.Content));
            }
        }

        messages.Add(new UserChatMessage(request.UserMessage));
        return messages;
    }
}
