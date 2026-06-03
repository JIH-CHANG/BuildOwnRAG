using ManufacturingAI.Setup.Models;

namespace ManufacturingAI.Setup;

internal static class AutoSetup
{
    public static async Task<int> RunAsync()
    {
        Console.WriteLine("[Setup] SETUP_AUTO_CONFIRM=true — running headless install");

        var req = BuildRequestFromEnv();

        if (string.IsNullOrWhiteSpace(req.PostgresPassword))
        {
            Console.Error.WriteLine("[Setup] ERROR: POSTGRES_PASSWORD env var is required");
            return 1;
        }
        if (string.IsNullOrWhiteSpace(req.AdminEmail) || string.IsNullOrWhiteSpace(req.AdminPassword))
        {
            Console.Error.WriteLine("[Setup] ERROR: ADMIN_EMAIL and ADMIN_PASSWORD env vars are required");
            return 1;
        }

        var svc = new SetupService();
        var failed = false;

        await foreach (var p in svc.InstallAsync(req, CancellationToken.None))
        {
            Console.WriteLine($"[{p.Step}] {p.Status}: {p.Message}");
            if (p.Status == "failed")
            {
                failed = true;
                break;
            }
        }

        return failed ? 1 : 0;
    }

    private static InstallRequest BuildRequestFromEnv()
    {
        var provider = Env("LLM__PROVIDER", "openai").ToLowerInvariant();

        return new InstallRequest(
            PostgresHost: Env("POSTGRES_HOST", "postgres"),
            PostgresPort: int.Parse(Env("POSTGRES_PORT", "5432")),
            PostgresDb: Env("POSTGRES_DB", "manufacturingai"),
            PostgresUser: Env("POSTGRES_USER", "postgres"),
            PostgresPassword: Env("POSTGRES_PASSWORD", ""),
            RedisHost: Env("REDIS_HOST", "redis"),
            RedisPort: int.Parse(Env("REDIS_PORT", "6379")),
            QdrantHost: Env("QDRANT_HOST", "qdrant"),
            QdrantHttpPort: int.Parse(Env("QDRANT_HTTP_PORT", "6333")),
            LlmProvider: provider,
            LlmApiKey: Env("LLM__APIKEY", ""),
            LlmChatModel: Env("LLM__CHATMODEL", "gpt-4o-mini"),
            AzureEndpoint: EnvOpt("LLM__AZUREENDPOINT"),
            AzureDeployment: EnvOpt("LLM__DEPLOYMENTNAME"),
            OllamaBaseUrl: EnvOpt("LLM__OLLAMABASEURL"),
            OllamaChatModel: EnvOpt("LLM__OLLAMACHATMODEL"),
            GeminiApiKey: EnvOpt("GEMINI_API_KEY"),
            EmbeddingProvider: Env("EMBEDDING__PROVIDER", ""),
            EmbeddingModel: Env("LLM__EMBEDDINGMODEL", ""),
            EmbeddingDimensions: int.Parse(Env("LLM__EMBEDDINGDIMENSIONS", "0")),
            OllamaEmbeddingModel: EnvOpt("EMBEDDING__OLLAMAMODEL"),
            GeminiEmbeddingModel: EnvOpt("GEMINI_EMBEDDING_MODEL"),
            EmbeddingApiKey: EnvOpt("EMBEDDING__APIKEY"),
            OrgName: Env("ORG_NAME", "My Factory"),
            AdminEmail: Env("ADMIN_EMAIL", ""),
            AdminPassword: Env("ADMIN_PASSWORD", "")
        );
    }

    private static string Env(string key, string defaultValue) =>
        Environment.GetEnvironmentVariable(key) ?? defaultValue;

    private static string? EnvOpt(string key) =>
        Environment.GetEnvironmentVariable(key);
}
