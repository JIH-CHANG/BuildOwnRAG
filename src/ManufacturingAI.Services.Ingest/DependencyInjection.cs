using Hangfire;
using Hangfire.PostgreSql;
using ManufacturingAI.Core.Models;
using ManufacturingAI.Core.Parser;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace ManufacturingAI.Services.Ingest;

public static class DependencyInjection
{
    public static IServiceCollection AddIngestServices(
        this IServiceCollection services,
        IConfiguration config)
    {
        // Hangfire + PostgreSQL storage
        services.AddHangfire(hangfire => hangfire
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(opts =>
                opts.UseNpgsqlConnection(
                    config.GetConnectionString("Postgres")
                    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured."))));

        services.AddHangfireServer(opts =>
        {
            opts.WorkerCount = 4;
            opts.Queues = ["default", "ingest", "testgen"];
        });

        // Redis Stream ingest queue — singleton matches IConnectionMultiplexer lifetime
        services.AddSingleton<IIngestQueue>(sp =>
            new RedisStreamIngestQueue(sp.GetRequiredService<IConnectionMultiplexer>()));

        services.AddCoreParsers();
        services.AddScoped<IIngestService, IngestService>();
        services.AddScoped<SyncSchedulerJob>();
        services.AddScoped<ConnectorSyncScheduler>();
        services.AddScoped<ReingestJob>();
        services.AddHostedService<FolderWatcherService>();
        services.AddHostedService<RedisStreamConsumerGroupInitializer>();

        return services;
    }

    /// <summary>
    /// Registers a per-connector recurring sync job for each enabled connector, using its
    /// configured <see cref="ConnectorConfig.SyncIntervalMinutes"/>. Also removes the legacy
    /// global "sync-all-connectors" job that this per-connector scheme supersedes.
    /// </summary>
    public static void RegisterRecurringJobs(
        IRecurringJobManager manager, IEnumerable<ConnectorConfig> enabledConnectors)
    {
        manager.RemoveIfExists("sync-all-connectors");

        var scheduler = new ConnectorSyncScheduler(manager);
        foreach (var connector in enabledConnectors)
            scheduler.Schedule(connector.Id, connector.SyncIntervalMinutes);
    }
}
