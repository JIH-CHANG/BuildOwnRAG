using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Configuration;
using OpenAI.Chat;

namespace ManufacturingAI.Infrastructure.LLM;

// Extends OpenAiLLMService, overrides client creation with AzureOpenAIClient.
public class AzureOpenAILLMService : OpenAiLLMService
{
    public AzureOpenAILLMService(IConfiguration config, LlmRuntimeConfig runtime) : base(config, runtime) { }

    protected override ChatClient CreateChatClient(IConfiguration config)
    {
        var endpoint = config["LLM:AzureEndpoint"]
            ?? throw new InvalidOperationException("LLM:AzureEndpoint is required for azureopenai provider.");
        var apiKey = config["LLM:ApiKey"]
            ?? throw new InvalidOperationException("LLM:ApiKey is required for azureopenai provider.");
        var deployment = config["LLM:DeploymentName"] ?? config["LLM:ChatModel"] ?? "gpt-4o";

        return new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey))
            .GetChatClient(deployment);
    }
}
