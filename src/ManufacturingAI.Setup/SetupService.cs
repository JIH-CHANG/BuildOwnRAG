using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using ManufacturingAI.Core.Interfaces;
using ManufacturingAI.Core.Models;
using ManufacturingAI.Infrastructure.Persistence;
using ManufacturingAI.Infrastructure.VectorStore;
using ManufacturingAI.Setup.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Qdrant.Client;
using StackExchange.Redis;
using BcryptNet = BCrypt.Net.BCrypt;

namespace ManufacturingAI.Setup;

internal sealed class SetupService
{
    // ── Connection test ───────────────────────────────────────────────────────

    public async Task<List<ConnectionResult>> TestConnectionsAsync(TestConnectionsRequest req)
    {
        var results = new List<ConnectionResult>();

        // PostgreSQL
        string? pgErr = null;
        try
        {
            await using var conn = new NpgsqlConnection(BuildPgCs(req));
            await conn.OpenAsync();
        }
        catch (Exception ex) { pgErr = Trunc(ex.Message, 120); }
        results.Add(new ConnectionResult("PostgreSQL", pgErr is null, pgErr ?? ""));

        // Redis
        string? redisErr = null;
        try
        {
            using var mux = await ConnectionMultiplexer.ConnectAsync($"{req.RedisHost}:{req.RedisPort}");
            await mux.GetDatabase().PingAsync();
        }
        catch (Exception ex) { redisErr = Trunc(ex.Message, 120); }
        results.Add(new ConnectionResult("Redis", redisErr is null, redisErr ?? ""));

        // Qdrant
        string? qdrantErr = null;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var resp = await http.GetAsync($"http://{req.QdrantHost}:{req.QdrantHttpPort}/healthz");
            if (!resp.IsSuccessStatusCode)
                qdrantErr = $"HTTP {(int)resp.StatusCode}";
        }
        catch (Exception ex) { qdrantErr = Trunc(ex.Message, 120); }
        results.Add(new ConnectionResult("Qdrant", qdrantErr is null, qdrantErr ?? ""));

        return results;
    }

    // ── LLM test ──────────────────────────────────────────────────────────────

    public async Task<LlmTestResult> TestLlmAsync(TestLlmRequest req)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            switch (req.Provider.ToLowerInvariant())
            {
                case "openai":
                    http.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", req.ApiKey);
                    var r1 = await http.GetAsync("https://api.openai.com/v1/models");
                    if (!r1.IsSuccessStatusCode)
                        return new LlmTestResult(false, $"HTTP {(int)r1.StatusCode} — check API key");
                    break;

                case "azureopenai":
                    http.DefaultRequestHeaders.Add("api-key", req.ApiKey);
                    var azureUrl = $"{req.AzureEndpoint?.TrimEnd('/')}/openai/deployments?api-version=2024-02-01";
                    var r2 = await http.GetAsync(azureUrl);
                    if (!r2.IsSuccessStatusCode)
                        return new LlmTestResult(false, $"HTTP {(int)r2.StatusCode} — check endpoint and key");
                    break;

                case "gemini":
                    var geminiUrl = $"https://generativelanguage.googleapis.com/v1beta/models?key={req.ApiKey}";
                    var rg = await http.GetAsync(geminiUrl);
                    if (!rg.IsSuccessStatusCode)
                        return new LlmTestResult(false, $"HTTP {(int)rg.StatusCode} — check Gemini API key");
                    break;

                case "ollama":
                    var ollamaBase = (req.OllamaBaseUrl ?? "http://localhost:11434").TrimEnd('/');
                    var ro = await http.GetAsync($"{ollamaBase}/api/tags");
                    if (!ro.IsSuccessStatusCode)
                        return new LlmTestResult(false, $"HTTP {(int)ro.StatusCode} — check Ollama is running");
                    if (!string.IsNullOrWhiteSpace(req.Model))
                    {
                        var body = await ro.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(body);
                        var names = doc.RootElement.GetProperty("models")
                            .EnumerateArray()
                            .Select(m => m.GetProperty("name").GetString() ?? "")
                            .ToList();
                        if (!names.Any(n => n.Contains(req.Model, StringComparison.OrdinalIgnoreCase)))
                            return new LlmTestResult(false,
                                $"Model '{req.Model}' not found. Available: {string.Join(", ", names.Take(5))}");
                    }
                    break;

                case "claude":
                    http.DefaultRequestHeaders.Add("x-api-key", req.ApiKey);
                    http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
                    var rclaude = await http.GetAsync("https://api.anthropic.com/v1/models");
                    if (!rclaude.IsSuccessStatusCode)
                        return new LlmTestResult(false, $"HTTP {(int)rclaude.StatusCode} — check Anthropic API key");
                    break;

                default:
                    return new LlmTestResult(false, $"Unknown provider: {req.Provider}");
            }

            return new LlmTestResult(true, "");
        }
        catch (Exception ex)
        {
            return new LlmTestResult(false, Trunc(ex.Message, 200));
        }
    }

    // ── Install (SSE stream) ──────────────────────────────────────────────────

    public async IAsyncEnumerable<InstallProgress> InstallAsync(
        InstallRequest req,
        [EnumeratorCancellation] CancellationToken ct)
    {
        SetupState state;
        string? mapError = null;
        try { state = MapToState(req); }
        catch (Exception ex) { mapError = ex.Message; state = new SetupState(); }

        if (mapError is not null)
        {
            yield return new InstallProgress("error", "failed", $"Invalid request: {mapError}");
            yield break;
        }

        // Build temporary DI container
        var encKey = GenerateBase64(32);
        var encIv  = GenerateBase64(16);
        var configDict = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = state.PostgresConnectionString,
            ["ConnectionStrings:Redis"]    = state.RedisConnectionString,
            ["Qdrant:Host"] = state.QdrantHost,
            ["Qdrant:Port"] = state.QdrantGrpcPort.ToString(),
            ["Encryption:Key"] = encKey,
            ["Encryption:IV"]  = encIv,
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddDbContext<ApplicationDbContext>(opt =>
            opt.UseNpgsql(state.PostgresConnectionString));
        services.AddSingleton<QdrantClient>(_ =>
            new QdrantClient(state.QdrantHost, state.QdrantGrpcPort));
        services.AddSingleton<IVectorStore, QdrantVectorStore>();
        services.AddScoped<ITenantVectorService, TenantVectorService>();

        await using var sp    = services.BuildServiceProvider();
        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Step 1 — Migration
        yield return new InstallProgress("migration", "running", "Running EF Core Migration...");
        string? migErr = null;
        try { await db.Database.MigrateAsync(ct); }
        catch (Exception ex) { migErr = ex.Message; }
        if (migErr is not null) { yield return new InstallProgress("migration", "failed", migErr); yield break; }
        yield return new InstallProgress("migration", "done", "Migration complete");

        // Step 2 — Seed tenant + admin
        yield return new InstallProgress("seed", "running", "Creating admin account...");
        Guid tenantId = Guid.Empty;
        string? seedErr = null;
        try
        {
            if (!await db.Tenants.AnyAsync(ct))
            {
                tenantId = Guid.NewGuid();
                db.Tenants.Add(new Tenant
                {
                    Id = tenantId,
                    Name = state.OrgName,
                    Plan = TenantPlan.Starter,
                    CreatedAt = DateTime.UtcNow,
                    Settings = new TenantSettings
                    {
                        LLMProvider         = state.LlmProvider,
                        LLMModel            = state.LlmChatModel,
                        LLMApiKey           = state.LlmApiKey,
                        EmbeddingProvider   = state.EmbeddingProvider,
                        EmbeddingModel      = state.EmbeddingModel,
                        EmbeddingApiKey     = state.EmbeddingApiKey,
                        EmbeddingDimensions = state.EmbeddingDimensions,
                    }
                });
                await db.SaveChangesAsync(ct);
            }
            else
            {
                tenantId = (await db.Tenants.FirstAsync(ct)).Id;
            }

            if (!await db.AppUsers.AnyAsync(u => u.Email == state.AdminEmail, ct))
            {
                db.AppUsers.Add(new AppUser
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    Email = state.AdminEmail,
                    PasswordHash = BcryptNet.HashPassword(state.AdminPassword),
                    Role = UserRole.TenantAdmin,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                });
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex) { seedErr = ex.Message; }
        if (seedErr is not null) { yield return new InstallProgress("seed", "failed", seedErr); yield break; }
        yield return new InstallProgress("seed", "done", "Admin account created");

        // Step 3 — Qdrant collection
        yield return new InstallProgress("qdrant", "running", "Initializing vector store...");
        string? qdrantErr = null;
        try
        {
            var vectorSvc = scope.ServiceProvider.GetRequiredService<ITenantVectorService>();
            await vectorSvc.InitializeCollectionAsync(tenantId, state.EmbeddingDimensions);
        }
        catch (Exception ex) { qdrantErr = ex.Message; }
        if (qdrantErr is not null) { yield return new InstallProgress("qdrant", "failed", qdrantErr); yield break; }
        yield return new InstallProgress("qdrant", "done", "Vector store ready");

        // Step 4 — Write .env
        yield return new InstallProgress("env", "running", "Writing configuration...");
        string? envErr = null;
        try
        {
            state.JwtSecret     = GenerateBase64(48);
            state.EncryptionKey = encKey;
            state.EncryptionIv  = encIv;
            await EnvGenerator.WriteAsync(state, Path.GetFullPath(".env"));
        }
        catch (Exception ex) { envErr = ex.Message; }
        if (envErr is not null) { yield return new InstallProgress("env", "failed", envErr); yield break; }
        yield return new InstallProgress("env", "done", "Configuration written");

        yield return new InstallProgress("complete", "done", "Setup complete");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SetupState MapToState(InstallRequest req)
    {
        var state = new SetupState
        {
            PostgresHost     = req.PostgresHost,
            PostgresPort     = req.PostgresPort,
            PostgresDb       = req.PostgresDb,
            PostgresUser     = req.PostgresUser,
            PostgresPassword = req.PostgresPassword,
            RedisHost        = req.RedisHost,
            RedisPort        = req.RedisPort,
            QdrantHost       = req.QdrantHost,
            QdrantHttpPort   = req.QdrantHttpPort,
            LlmProvider      = req.LlmProvider.ToLowerInvariant(),
            LlmApiKey        = req.LlmApiKey,
            LlmChatModel     = req.LlmChatModel,
            AzureEndpoint    = req.AzureEndpoint ?? "",
            AzureDeployment  = req.AzureDeployment ?? "",
            OllamaBaseUrl    = req.OllamaBaseUrl ?? "http://localhost:11434",
            OllamaChatModel  = req.OllamaChatModel ?? "",
            GeminiApiKey     = req.GeminiApiKey ?? "",
            GeminiChatModel  = req.LlmChatModel,
            EmbeddingApiKey  = req.EmbeddingApiKey ?? "",
            OrgName          = req.OrgName,
            AdminEmail       = req.AdminEmail,
            AdminPassword    = req.AdminPassword,
        };

        if (state.LlmProvider == "gemini")
            state.LlmApiKey = "";

        ApplyEmbeddingDefaults(state, req);
        return state;
    }

    private static void ApplyEmbeddingDefaults(SetupState state, InstallRequest req)
    {
        if (!string.IsNullOrWhiteSpace(req.EmbeddingProvider))
        {
            state.EmbeddingProvider  = req.EmbeddingProvider;
            state.EmbeddingModel     = req.EmbeddingModel;
            state.EmbeddingDimensions = req.EmbeddingDimensions > 0 ? req.EmbeddingDimensions : 1536;
            state.OllamaEmbeddingModel = req.OllamaEmbeddingModel ?? "";
            state.GeminiEmbeddingModel = req.GeminiEmbeddingModel ?? "";
            return;
        }

        switch (state.LlmProvider)
        {
            case "openai":
            case "azureopenai":
                state.EmbeddingProvider  = state.LlmProvider;
                state.EmbeddingModel     = "text-embedding-3-small";
                state.EmbeddingDimensions = 1536;
                break;
            case "gemini":
                state.EmbeddingProvider   = "gemini";
                state.EmbeddingModel      = "text-embedding-004";
                state.GeminiEmbeddingModel = "text-embedding-004";
                state.EmbeddingDimensions  = 768;
                break;
            case "ollama":
                state.EmbeddingProvider   = "ollama";
                state.OllamaEmbeddingModel = "nomic-embed-text";
                state.EmbeddingModel      = "nomic-embed-text";
                state.EmbeddingDimensions  = 768;
                break;
            case "claude":
                // No native embeddings — setup wizard must send embeddingProvider explicitly.
                // Default to openai if the wizard didn't supply one.
                state.EmbeddingProvider   = "openai";
                state.EmbeddingModel      = "text-embedding-3-small";
                state.EmbeddingDimensions  = 1536;
                break;
        }
    }

    internal static string GenerateBase64(int bytes)
    {
        var buf = new byte[bytes];
        RandomNumberGenerator.Fill(buf);
        return Convert.ToBase64String(buf);
    }

    private static string BuildPgCs(TestConnectionsRequest req) =>
        $"Host={req.PostgresHost};Port={req.PostgresPort};Database={req.PostgresDb};Username={req.PostgresUser};Password={req.PostgresPassword}";

    private static string Trunc(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
