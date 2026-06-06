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

        var apiKey = Runtime.ApiKey.NullIfEmpty()
            ?? config["LLM:ApiKey"]
            ?? throw new InvalidOperationException(
                "Azure OpenAI API key is not configured. Please go to Settings → AI Model and enter your API key.");

        var deployment = Runtime.Model.NullIfEmpty()
            ?? config["LLM:DeploymentName"]
            ?? config["LLM:ChatModel"]
            ?? "gpt-4o";

        return new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey))
            .GetChatClient(deployment);
    }
}
