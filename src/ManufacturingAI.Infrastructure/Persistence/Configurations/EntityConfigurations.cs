using System.Text.Json;
using ManufacturingAI.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ManufacturingAI.Infrastructure.Persistence.Configurations;

public class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Name).HasMaxLength(256).IsRequired();
        builder.Property(t => t.LicenseKey).HasColumnType("varchar(64)").IsRequired();
        builder.Property(t => t.Plan).HasConversion<string>().HasMaxLength(32);
        builder.Property(t => t.CollectionVersion).HasDefaultValue(1);
        builder.OwnsOne(t => t.Settings, s =>
        {
            s.Property(x => x.LLMProvider).HasMaxLength(64);
            s.Property(x => x.LLMModel).HasMaxLength(128);
            s.Property(x => x.EmbeddingModel).HasMaxLength(128);
            s.Property(x => x.EmbeddingDimensions).HasDefaultValue(0);
            // Store as string; read legacy "Markdown" rows as Lite (the mode was renamed).
            s.Property(x => x.RetrievalMode)
                .HasConversion(
                    v => v == RetrievalMode.Lite ? "Lite" : "Hybrid",
                    v => v == "Hybrid" ? RetrievalMode.Hybrid : RetrievalMode.Lite)
                .HasMaxLength(16)
                .HasDefaultValue(RetrievalMode.Hybrid);
            s.Property(x => x.SystemPrompt).HasColumnType("text").HasDefaultValue(string.Empty);
        });
    }
}

public class AppUserConfiguration : IEntityTypeConfiguration<AppUser>
{
    public void Configure(EntityTypeBuilder<AppUser> builder)
    {
        builder.HasKey(u => u.Id);
        builder.HasIndex(u => u.TenantId);
        builder.HasIndex(u => u.Email).IsUnique();
        builder.Property(u => u.Email).HasMaxLength(256).IsRequired();
        builder.Property(u => u.PasswordHash).HasMaxLength(512).IsRequired();
        builder.Property(u => u.Role).HasConversion<string>().HasMaxLength(32);
    }
}

public class DocumentConfiguration : IEntityTypeConfiguration<Document>
{
    public void Configure(EntityTypeBuilder<Document> builder)
    {
        builder.HasKey(d => d.Id);
        builder.HasIndex(d => d.TenantId);
        builder.HasIndex(d => new { d.TenantId, d.SourceType, d.SourceId });
        builder.Property(d => d.SourceType).HasMaxLength(64).IsRequired();
        builder.Property(d => d.SourceId).HasMaxLength(512).IsRequired();
        builder.Property(d => d.Title).HasMaxLength(1024).IsRequired();
        builder.Property(d => d.FilePath).HasMaxLength(2048);
        builder.Property(d => d.MimeType).HasMaxLength(128);
        builder.Property(d => d.VersionHash).HasColumnType("varchar(64)");
        builder.Property(d => d.Status).HasConversion<string>().HasMaxLength(32);
    }
}

public class DocumentChunkConfiguration : IEntityTypeConfiguration<DocumentChunk>
{
    public void Configure(EntityTypeBuilder<DocumentChunk> builder)
    {
        builder.HasKey(c => c.Id);
        builder.HasIndex(c => c.TenantId);
        builder.HasIndex(c => c.DocumentId);
        builder.Property(c => c.Content).IsRequired();
        builder.Property(c => c.VectorId).HasMaxLength(128);
        builder.OwnsOne(c => c.Metadata, m =>
        {
            m.Property(x => x.SourceTitle).HasMaxLength(1024);
            m.Property(x => x.SectionTitle).HasMaxLength(512);
            m.Property(x => x.SourceType).HasMaxLength(64);
        });
    }
}

public class SyncStateConfiguration : IEntityTypeConfiguration<SyncState>
{
    public void Configure(EntityTypeBuilder<SyncState> builder)
    {
        builder.HasKey(s => s.Id);
        builder.HasIndex(s => s.TenantId);
        builder.HasIndex(s => new { s.ConnectorId, s.SourceId });
        builder.Property(s => s.SourceId).HasMaxLength(512).IsRequired();
        builder.Property(s => s.VersionHash).HasColumnType("varchar(64)");
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(s => s.ErrorMessage).HasMaxLength(2048);
    }
}

public class ConnectorConfigConfiguration : IEntityTypeConfiguration<ConnectorConfig>
{
    public void Configure(EntityTypeBuilder<ConnectorConfig> builder)
    {
        builder.HasKey(c => c.Id);
        builder.HasIndex(c => c.TenantId);
        builder.Property(c => c.ConnectorType).HasMaxLength(64).IsRequired();
        builder.Property(c => c.DisplayName).HasMaxLength(256).IsRequired();
        builder.Property(c => c.SettingsJson).HasColumnType("text"); // the content which is encrypted by AES-256
        builder.Property(c => c.SyncIntervalMinutes).HasDefaultValue(60);
    }
}

public class QueryLogConfiguration : IEntityTypeConfiguration<QueryLog>
{
    public void Configure(EntityTypeBuilder<QueryLog> builder)
    {
        builder.HasKey(q => q.Id);
        builder.HasIndex(q => q.TenantId);
        builder.HasIndex(q => new { q.TenantId, q.CreatedAt });
        builder.Property(q => q.Question).IsRequired();
        builder.Property(q => q.Answer).IsRequired();
        builder.Property(q => q.Feedback).HasConversion<string>().HasMaxLength(32);
        builder.Property(q => q.SourceChunkIds)
            .HasConversion(
                v => string.Join(',', v),
                v => v.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList())
            .Metadata.SetValueComparer(new ValueComparer<List<string>>(
                (a, b) => a != null && b != null && a.SequenceEqual(b),
                c => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
                c => c.ToList()));

        // Retrieval detail stored as JSON. Compared by serialized value so EF
        // change tracking detects edits to the list contents.
        builder.Property(q => q.RetrievedChunks)
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => JsonSerializer.Deserialize<List<RetrievedChunkLog>>(v, (JsonSerializerOptions?)null) ?? new List<RetrievedChunkLog>())
            .Metadata.SetValueComparer(new ValueComparer<List<RetrievedChunkLog>>(
                (a, b) => JsonSerializer.Serialize(a, (JsonSerializerOptions?)null) == JsonSerializer.Serialize(b, (JsonSerializerOptions?)null),
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null).GetHashCode(),
                v => JsonSerializer.Deserialize<List<RetrievedChunkLog>>(JsonSerializer.Serialize(v, (JsonSerializerOptions?)null), (JsonSerializerOptions?)null) ?? new List<RetrievedChunkLog>()));
    }
}

public class GeneratedTestScriptConfiguration : IEntityTypeConfiguration<GeneratedTestScript>
{
    public void Configure(EntityTypeBuilder<GeneratedTestScript> builder)
    {
        builder.HasKey(g => g.Id);
        builder.HasIndex(g => g.TenantId);
        builder.Property(g => g.ScriptType).HasMaxLength(64).IsRequired();
        builder.Property(g => g.BlobPath).HasMaxLength(2048);
        builder.Property(g => g.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(g => g.ErrorMessage).HasMaxLength(2048);
    }
}

public class AppRefreshTokenConfiguration : IEntityTypeConfiguration<AppRefreshToken>
{
    public void Configure(EntityTypeBuilder<AppRefreshToken> builder)
    {
        builder.HasKey(t => t.Id);
        builder.HasIndex(t => t.TokenHash).IsUnique();
        builder.HasIndex(t => new { t.UserId, t.IsRevoked, t.ExpiresAt });
        builder.Property(t => t.TokenHash).HasColumnType("varchar(64)").IsRequired();
        builder.Property(t => t.DeviceInfo).HasMaxLength(512);
    }
}

public class AppApiKeyConfiguration : IEntityTypeConfiguration<AppApiKey>
{
    public void Configure(EntityTypeBuilder<AppApiKey> builder)
    {
        builder.HasKey(k => k.Id);
        builder.HasIndex(k => k.KeyHash).IsUnique();
        builder.HasIndex(k => k.TenantId);
        builder.Property(k => k.KeyHash).HasColumnType("varchar(64)").IsRequired();
        builder.Property(k => k.Name).HasMaxLength(256).IsRequired();
    }
}

public class TenantLlmConfigConfiguration : IEntityTypeConfiguration<TenantLlmConfig>
{
    public void Configure(EntityTypeBuilder<TenantLlmConfig> builder)
    {
        builder.HasKey(c => c.Id);
        builder.HasIndex(c => c.TenantId);
        builder.Property(c => c.Provider).HasMaxLength(64).IsRequired();
        builder.Property(c => c.ModelName).HasMaxLength(128).IsRequired();
    }
}
