using ManufacturingAI.Core.Interfaces;
using ManufacturingAI.Infrastructure.Caching;
using ManufacturingAI.Infrastructure.LLM;
using ManufacturingAI.Infrastructure.Persistence;
using ManufacturingAI.Infrastructure.Repositories;
using ManufacturingAI.Infrastructure.Security;
using ManufacturingAI.Infrastructure.Storage;
using ManufacturingAI.Infrastructure.VectorStore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Qdrant.Client;
using StackExchange.Redis;

namespace ManufacturingAI.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        // PostgreSQL / EF Core
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseNpgsql(config.GetConnectionString("Postgres")));

        // Repositories
        services.AddScoped(typeof(IRepository<>), typeof(Repository<>));
        services.AddScoped<IDocumentRepository, DocumentRepository>();
        services.AddScoped<IDocumentChunkRepository, DocumentChunkRepository>();
        services.AddScoped<ISyncStateRepository, SyncStateRepository>();
        services.AddScoped<IQueryLogRepository, QueryLogRepository>();
        services.AddScoped<IConnectorConfigRepository, ConnectorConfigRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<IApiKeyRepository, ApiKeyRepository>();
        services.AddScoped<IUserRepository, UserRepository>();

        // Redis Cache
        services.AddSingleton<IConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect(config.GetConnectionString("Redis")
                ?? throw new InvalidOperationException("ConnectionStrings:Redis is not configured.")));
        services.AddSingleton<ICacheService, RedisCacheService>();

        // Qdrant Vector Store
        services.AddSingleton<QdrantClient>(_ =>
        {
            var host = config["Qdrant:Host"] ?? "localhost";
            var port = int.Parse(config["Qdrant:Port"] ?? "6334");
            return new QdrantClient(host, port);
        });
        services.AddSingleton<IVectorStore, QdrantVectorStore>();
        services.AddScoped<ITenantVectorService, TenantVectorService>();

        // Blob Storage
        services.AddSingleton<IBlobStorage, LocalBlobStorage>();

        // HttpClient (required by GeminiEmbeddingService and future HTTP-based services)
        services.AddHttpClient();

        // Encryption
        services.AddSingleton<IEncryptionService, AesEncryptionService>();

        // ── Runtime LLM config (live-updatable by admin) ─────────────────────────
        services.AddSingleton<LlmRuntimeConfig>();

        // ── LLM providers (keyed) ────────────────────────────────────────────────
        services.AddKeyedSingleton<ILLMService, OpenAiLLMService>("openai");
        services.AddKeyedSingleton<ILLMService, AzureOpenAILLMService>("azureopenai");
        services.AddKeyedSingleton<ILLMService, OllamaLLMService>("ollama");
        services.AddKeyedSingleton<ILLMService, GeminiLLMService>("gemini");
        services.AddKeyedSingleton<ILLMService, ClaudeLLMService>("claude");
        services.AddKeyedSingleton<ILLMService, GroqLLMService>("groq");

        // Router: resolves the correct keyed provider at call time from LlmRuntimeConfig.
        services.AddSingleton<ILLMService, LlmProviderRouter>();

        // ── Embedding providers (keyed) ──────────────────────────────────────────
        services.AddKeyedSingleton<IEmbeddingService, OpenAiEmbeddingService>("openai");
        services.AddKeyedSingleton<IEmbeddingService, OllamaEmbeddingService>("ollama");
        services.AddKeyedSingleton<IEmbeddingService, GeminiEmbeddingService>("gemini");

        // Router: resolves the correct keyed embedding provider at call time from LlmRuntimeConfig.
        services.AddSingleton<IEmbeddingService, EmbeddingProviderRouter>();

        return services;
    }
}
