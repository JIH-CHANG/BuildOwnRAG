using Hangfire;
using ManufacturingAI.Core.Interfaces;
using ManufacturingAI.Core.Models;
using ManufacturingAI.Infrastructure.LLM;
using ManufacturingAI.Infrastructure.Persistence;
using ManufacturingAI.Infrastructure.Security;
using ManufacturingAI.Services.Ingest;
using Microsoft.EntityFrameworkCore;
using Polly;
using Polly.Retry;
using BcryptNet = BCrypt.Net.BCrypt;

namespace ManufacturingAI.API;

internal static class StartupInitializer
{
    internal static async Task RunAsync(WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        var config = app.Services.GetRequiredService<IConfiguration>();

        try
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var vectorStore = scope.ServiceProvider.GetRequiredService<IVectorStore>();

            await WaitForPostgresAsync(db, logger);
            await RunMigrationAsync(db, logger);
            RegisterRecurringJobs(scope.ServiceProvider, logger);
            await EnsureQdrantCollectionAsync(vectorStore, config, logger);
            var (adminEmail, isCustom) = await SeedDefaultDataAsync(db, config, logger);
            await InitLlmRuntimeConfigAsync(db, scope.ServiceProvider, logger);
            PrintBanner(config, adminEmail, isCustom);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Startup initialization failed — aborting");
            throw;
        }
    }

    // ── Step 1: Wait for PostgreSQL ──────────────────────────────

    private static async Task WaitForPostgresAsync(ApplicationDbContext db, ILogger logger)
    {
        const int maxAttempts = 20;

        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = maxAttempts - 1,
                Delay = TimeSpan.FromSeconds(3),
                BackoffType = DelayBackoffType.Constant,
                ShouldHandle = new PredicateBuilder().Handle<Exception>(),
                OnRetry = args =>
                {
                    logger.LogWarning(
                        "PostgreSQL not ready (attempt {Attempt}/{Max}): {Message}",
                        args.AttemptNumber + 1, maxAttempts, args.Outcome.Exception?.Message);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();

        try
        {
            await pipeline.ExecuteAsync(async ct =>
            {
                if (!await db.Database.CanConnectAsync(ct))
                    throw new InvalidOperationException("CanConnectAsync returned false");
            });
            logger.LogInformation("PostgreSQL is ready");
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "PostgreSQL did not become ready within 60 seconds");
            throw new TimeoutException("PostgreSQL connection timeout after 60 seconds.", ex);
        }
    }

    // ── Step 2b: Hangfire recurring jobs (after PostgreSQL is ready) ─

    private static void RegisterRecurringJobs(IServiceProvider services, ILogger logger)
    {
        try
        {
            var manager = services.GetRequiredService<IRecurringJobManager>();
            DependencyInjection.RegisterRecurringJobs(manager);
            logger.LogInformation("Hangfire recurring jobs registered");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to register Hangfire recurring jobs — continuing");
        }
    }

    // ── Step 2: EF Core Migration ────────────────────────────────

    private static async Task RunMigrationAsync(ApplicationDbContext db, ILogger logger)
    {
        var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
        if (pending.Count == 0)
        {
            logger.LogInformation("No pending migrations");
            return;
        }

        await db.Database.MigrateAsync();
        logger.LogInformation(
            "Migration complete, applied {Count} migration(s): {Names}",
            pending.Count, string.Join(", ", pending));
    }

    // ── Step 3: Qdrant Collection ────────────────────────────────

    private static async Task EnsureQdrantCollectionAsync(
        IVectorStore vectorStore, IConfiguration config, ILogger logger)
    {
        var name = config["Qdrant:CollectionName"] ?? "manufacturingai";
        var size = config.GetValue<int>("Qdrant:VectorSize", 1536);

        try
        {
            await vectorStore.EnsureCollectionAsync(name, size);
            logger.LogInformation(
                "Qdrant collection '{Name}' ({Dims}D, Cosine) ready", name, size);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to ensure Qdrant collection '{Name}' — continuing", name);
        }
    }

    // ── Step 4: Seed default data ────────────────────────────────

    private static async Task<(string Email, bool IsCustom)> SeedDefaultDataAsync(
        ApplicationDbContext db, IConfiguration config, ILogger logger)
    {
        var adminEmail = config["INIT_ADMIN_EMAIL"] ?? "admin@manufacturingai.com";
        var isCustom = config["INIT_ADMIN_EMAIL"] is not null;

        if (await db.Tenants.AnyAsync())
        {
            // Sync provider/model from config if tenant still has the default seed values
            var existing = await db.Tenants.FirstAsync();
            var configProvider = (config["Llm:Provider"] ?? "openai").ToLowerInvariant();
            var configModel   = config["Llm:Model"] ?? string.Empty;
            if (string.Equals(existing.Settings.LLMProvider, "openai", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(configProvider, "openai", StringComparison.OrdinalIgnoreCase))
            {
                existing.Settings.LLMProvider = configProvider;
                if (!string.IsNullOrEmpty(configModel) && string.IsNullOrEmpty(existing.Settings.LLMModel))
                    existing.Settings.LLMModel = configModel;
                await db.SaveChangesAsync();
                logger.LogInformation(
                    "Synced tenant LLM provider from config: {Provider}", configProvider);
            }
            logger.LogInformation("Seed skipped: tenant data already exists");
            return (adminEmail, isCustom);
        }

        var companyName = config["INIT_COMPANY_NAME"] ?? "ManufacturingAI Demo";
        var adminPassword = config["INIT_ADMIN_PASSWORD"] ?? "Admin@1234";

        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = companyName,
            LicenseKey = "TRIAL",
            LicenseExpiresAt = DateTime.UtcNow.AddYears(1),
            Plan = TenantPlan.Trial,
            CollectionVersion = 1,
            CreatedAt = DateTime.UtcNow,
            Settings = new TenantSettings
            {
                MaxUsers = 10,
                MaxDocuments = 1000,
                MaxConnectors = 5,
                LLMProvider = config["Llm:Provider"] ?? "openai",
                LLMModel = config["Llm:Model"] ?? "gpt-4o-mini",
                EmbeddingModel = "text-embedding-3-small",
                EmbeddingDimensions = 0
            }
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        logger.LogInformation("Seeded default tenant: {Name} ({Id})", tenant.Name, tenant.Id);

        if (!await db.AppUsers.AnyAsync(u => u.Email == adminEmail))
        {
            db.AppUsers.Add(new AppUser
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.Id,
                Email = adminEmail,
                PasswordHash = BcryptNet.HashPassword(adminPassword, workFactor: 12),
                Role = UserRole.TenantAdmin,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            logger.LogInformation("Seeded admin user: {Email}", adminEmail);
        }

        if (!await db.TenantLlmConfigs.AnyAsync())
        {
            db.TenantLlmConfigs.Add(new TenantLlmConfig
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.Id,
                Provider = "openai",
                ModelName = "gpt-4o-mini",
                IsActive = false,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            logger.LogInformation("Seeded default LLM config (openai / gpt-4o-mini, inactive)");
        }

        return (adminEmail, isCustom);
    }

    // ── Step 5: Initialize LlmRuntimeConfig from DB ──────────────

    private static async Task InitLlmRuntimeConfigAsync(
        ApplicationDbContext db, IServiceProvider sp, ILogger logger)
    {
        try
        {
            var tenant = await db.Tenants.FirstOrDefaultAsync();
            if (tenant is null) return;

            var cfg = sp.GetRequiredService<IConfiguration>();

            // API key stored as plaintext
            var rawApiKey = tenant.Settings.LLMApiKey;

            static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

            // Empty string from env means "not configured" — treat same as null
            var configProvider = (NullIfEmpty(cfg["Llm:Provider"]) ?? "openai").ToLowerInvariant();
            var configModel    = NullIfEmpty(cfg["Llm:Model"]) ?? string.Empty;

            // If admin has never saved an API key via UI, treat the DB settings
            // as the defaults (possibly the old seed) and prefer the env config instead.
            var hasDbKey = !string.IsNullOrEmpty(rawApiKey);

            var provider = hasDbKey
                ? NullIfEmpty(tenant.Settings.LLMProvider?.ToLowerInvariant()) ?? configProvider
                : configProvider;

            var model = hasDbKey
                ? NullIfEmpty(tenant.Settings.LLMModel) ?? configModel
                : NullIfEmpty(configModel) ?? tenant.Settings.LLMModel;

            var embeddingModel    = NullIfEmpty(tenant.Settings.EmbeddingModel)    ?? string.Empty;
            var embeddingProvider = NullIfEmpty(tenant.Settings.EmbeddingProvider) ?? string.Empty;
            var embeddingApiKey   = NullIfEmpty(tenant.Settings.EmbeddingApiKey)   ?? string.Empty;

            var runtimeConfig = sp.GetRequiredService<LlmRuntimeConfig>();
            runtimeConfig.Update(provider, rawApiKey, model, embeddingModel, embeddingProvider, embeddingApiKey);
            logger.LogInformation(
                "LlmRuntimeConfig initialized: provider={Provider}, model={Model}, embeddingProvider={EmbeddingProvider}, embeddingModel={EmbeddingModel}, hasKey={HasKey}",
                provider, model, embeddingProvider, embeddingModel, !string.IsNullOrEmpty(rawApiKey));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to initialize LlmRuntimeConfig — using config fallback");
        }
    }

    // ── Step 6: Startup banner ───────────────────────────────────

    private static void PrintBanner(IConfiguration config, string adminEmail, bool isCustom)
    {
        var passwordDisplay = isCustom ? "(custom)" : "Admin@1234";
        var port = ExtractPort(config["ASPNETCORE_URLS"] ?? "http://+:8080");

        const string h = "══════════════════════════════════════════";
        Console.WriteLine($"╔{h}╗");
        Console.WriteLine($"║{"     ManufacturingAI Started",-42}║");
        Console.WriteLine($"╠{h}╣");
        Console.WriteLine($"║{"  URL    : http://localhost:" + port,-42}║");
        Console.WriteLine($"║{"  Swagger: http://localhost:" + port + "/swagger",-42}║");
        Console.WriteLine($"╠{h}╣");
        Console.WriteLine($"║{"  Account: " + adminEmail,-42}║");
        Console.WriteLine($"║{"  Password: " + passwordDisplay,-42}║");
        Console.WriteLine($"║{"  !! Change password after first login !!",-42}║");
        Console.WriteLine($"╚{h}╝");
    }

    private static string ExtractPort(string aspNetCoreUrls)
    {
        var url = aspNetCoreUrls.Split(';')[0].Trim();
        var lastColon = url.LastIndexOf(':');
        return lastColon >= 0 ? url[(lastColon + 1)..] : "8080";
    }
}
