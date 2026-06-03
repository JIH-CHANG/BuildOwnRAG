using Microsoft.Extensions.Configuration;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;

namespace ManufacturingAI.Infrastructure.LLM;

public sealed class GroqLLMService(IConfiguration config, LlmRuntimeConfig runtime)
    : OpenAiLLMService(config, runtime)
{
    private static readonly Uri GroqBaseUri = new("https://api.groq.com/openai/v1/");

    protected override ChatClient CreateChatClient(IConfiguration cfg)
    {
        var apiKey = Runtime.ApiKey.NullIfEmpty()
            ?? cfg["Groq:ApiKey"]
            ?? string.Empty;

        if (string.IsNullOrEmpty(apiKey))
            throw new InvalidOperationException(
                "Groq API key is not configured. Please go to Settings → AI Model and enter your Groq API key.");

        var model = Runtime.Model.NullIfEmpty()
            ?? cfg["Llm:Model"]
            ?? "llama-3.3-70b-versatile";

        var options = new OpenAIClientOptions { Endpoint = GroqBaseUri };
        return new OpenAIClient(new ApiKeyCredential(apiKey), options).GetChatClient(model);
    }
}
