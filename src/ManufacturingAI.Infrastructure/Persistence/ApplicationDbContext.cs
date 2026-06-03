using ManufacturingAI.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace ManufacturingAI.Infrastructure.Persistence;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentChunk> DocumentChunks => Set<DocumentChunk>();
    public DbSet<SyncState> SyncStates => Set<SyncState>();
    public DbSet<ConnectorConfig> ConnectorConfigs => Set<ConnectorConfig>();
    public DbSet<QueryLog> QueryLogs => Set<QueryLog>();
    public DbSet<GeneratedTestScript> GeneratedTestScripts => Set<GeneratedTestScript>();
    public DbSet<AppRefreshToken> AppRefreshTokens => Set<AppRefreshToken>();
    public DbSet<AppApiKey> AppApiKeys => Set<AppApiKey>();
    public DbSet<TenantLlmConfig> TenantLlmConfigs => Set<TenantLlmConfig>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);
    }
}
