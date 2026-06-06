using ManufacturingAI.Core.Common;
using ManufacturingAI.Core.Models;
using ManufacturingAI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;
using System.Reflection;

namespace ManufacturingAI.Infrastructure.Repositories;

public class DocumentRepository(ApplicationDbContext db) : Repository<Document>(db), IDocumentRepository
{
    public async Task<Document?> GetBySourceIdAsync(Guid tenantId, string sourceType, string sourceId, CancellationToken ct = default)
        => await DbSet.FirstOrDefaultAsync(d => d.TenantId == tenantId && d.SourceType == sourceType && d.SourceId == sourceId, ct);

    public async Task<IEnumerable<Document>> GetByStatusAsync(Guid tenantId, DocumentStatus status, CancellationToken ct = default)
        => await DbSet.Where(d => d.TenantId == tenantId && d.Status == status).ToListAsync(ct);

    public async Task<Result> UpdateStatusAsync(Guid id, DocumentStatus status, CancellationToken ct = default)
    {
        try
        {
            await DbSet.Where(d => d.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, status)
                                          .SetProperty(d => d.UpdatedAt, DateTime.UtcNow), ct);
            return Result.Ok();
        }
        catch (Exception ex)
        {
            return Result.Fail(ex.Message);
        }
    }
}

public class DocumentChunkRepository(ApplicationDbContext db) : Repository<DocumentChunk>(db), IDocumentChunkRepository
{
    public async Task<IEnumerable<DocumentChunk>> GetByDocumentIdAsync(Guid documentId, CancellationToken ct = default)
        => await DbSet.Where(c => c.DocumentId == documentId).OrderBy(c => c.ChunkIndex).ToListAsync(ct);

    public async Task<IEnumerable<DocumentChunk>> GetByIdsAsync(Guid tenantId, IEnumerable<string> vectorIds, CancellationToken ct = default)
    {
        var idList = vectorIds.ToList();
        return await DbSet.Where(c => c.TenantId == tenantId && idList.Contains(c.VectorId)).ToListAsync(ct);
    }

    public async Task<int> DeleteByDocumentIdAsync(Guid documentId, CancellationToken ct = default)
        => await DbSet.Where(c => c.DocumentId == documentId).ExecuteDeleteAsync(ct);

    // Coarse keyword prefilter: rows whose Content ILIKEs ANY query term. GIN index optional;
    // this caps the candidate set before in-memory BM25 ranking (Lite mode).
    public async Task<IReadOnlyList<DocumentChunk>> SearchByKeywordAsync(
        Guid tenantId, string query, int limit, CancellationToken ct = default)
    {
        var terms = Tokenize(query);
        if (terms.Count == 0) return [];
        return await DbSet.Where(BuildKeywordPredicate(tenantId, terms)).Take(limit).ToListAsync(ct);
    }

    // CJK text has no word boundaries, so whitespace/punctuation splitting yields one giant
    // token that ILIKE can never match. For CJK segments we emit overlapping bigrams (the
    // standard CJK n-gram prefilter); Latin segments keep whole-word tokens (length >= 2).
    private static List<string> Tokenize(string query)
    {
        var segments = query.Split(
            [' ', '\t', '\n', '\r', ',', '.', '，', '。', '、', '；', ';', '?', '？', '!', '！', ':', '：'],
            StringSplitOptions.RemoveEmptyEntries);

        var tokens = new List<string>();
        foreach (var raw in segments)
        {
            var s = raw.Trim();
            if (s.Length == 0) continue;

            if (ContainsCjk(s))
            {
                if (s.Length == 1)
                    tokens.Add(s);
                else
                    for (int i = 0; i < s.Length - 1; i++)
                        tokens.Add(s.Substring(i, 2));
            }
            else if (s.Length >= 2)
            {
                tokens.Add(s);
            }
        }

        return tokens
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(24)
            .ToList();
    }

    private static bool ContainsCjk(string s)
    {
        foreach (var ch in s)
            if ((ch >= 0x4E00 && ch <= 0x9FFF)   // CJK Unified Ideographs
                || (ch >= 0x3400 && ch <= 0x4DBF) // CJK Extension A
                || (ch >= 0xF900 && ch <= 0xFAFF)) // CJK Compatibility Ideographs
                return true;
        return false;
    }

    private static readonly MethodInfo ILikeMethod = typeof(NpgsqlDbFunctionsExtensions)
        .GetMethod(nameof(NpgsqlDbFunctionsExtensions.ILike), [typeof(DbFunctions), typeof(string), typeof(string)])!;

    // Build: c.TenantId == tenantId && (ILike(c.Content,'%t1%') || ILike(c.Content,'%t2%') || ...)
    private static Expression<Func<DocumentChunk, bool>> BuildKeywordPredicate(Guid tenantId, List<string> terms)
    {
        var c = Expression.Parameter(typeof(DocumentChunk), "c");
        Expression tenantEq = Expression.Equal(
            Expression.Property(c, nameof(DocumentChunk.TenantId)), Expression.Constant(tenantId));

        var content = Expression.Property(c, nameof(DocumentChunk.Content));
        var efFunctions = Expression.Constant(EF.Functions);
        Expression? ors = null;
        foreach (var term in terms)
        {
            var like = Expression.Call(ILikeMethod, efFunctions, content, Expression.Constant($"%{term}%"));
            ors = ors is null ? like : Expression.OrElse(ors, like);
        }

        return Expression.Lambda<Func<DocumentChunk, bool>>(Expression.AndAlso(tenantEq, ors!), c);
    }
}

public class SyncStateRepository(ApplicationDbContext db) : Repository<SyncState>(db), ISyncStateRepository
{
    public async Task<IEnumerable<SyncState>> GetByConnectorAsync(Guid connectorId, CancellationToken ct = default)
        => await DbSet.Where(s => s.ConnectorId == connectorId).ToListAsync(ct);

    public async Task<Result<SyncState>> UpsertAsync(SyncState entity, CancellationToken ct = default)
    {
        try
        {
            var existing = await DbSet.FirstOrDefaultAsync(
                s => s.ConnectorId == entity.ConnectorId && s.SourceId == entity.SourceId, ct);

            if (existing is null)
            {
                await DbSet.AddAsync(entity, ct);
            }
            else
            {
                existing.VersionHash = entity.VersionHash;
                existing.LastSyncedAt = entity.LastSyncedAt;
                existing.Status = entity.Status;
                existing.ErrorMessage = entity.ErrorMessage;
                DbSet.Update(existing);
                entity = existing;
            }

            await Db.SaveChangesAsync(ct);
            return Result<SyncState>.Ok(entity);
        }
        catch (Exception ex)
        {
            return Result<SyncState>.Fail(ex.Message);
        }
    }

    public async Task<int> DeleteBySourceAsync(Guid tenantId, string sourceId, CancellationToken ct = default)
        => await DbSet.Where(s => s.TenantId == tenantId && s.SourceId == sourceId).ExecuteDeleteAsync(ct);
}

public class QueryLogRepository(ApplicationDbContext db) : Repository<QueryLog>(db), IQueryLogRepository
{
    public async Task<(IEnumerable<QueryLog> Items, int Total)> GetByTenantAsync(Guid tenantId, int page, int pageSize, CancellationToken ct = default)
    {
        var query = DbSet.Where(q => q.TenantId == tenantId).OrderByDescending(q => q.CreatedAt);
        var total = await query.CountAsync(ct);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return (items, total);
    }

    public async Task<IEnumerable<QueryLog>> GetByRangeAsync(Guid tenantId, DateTime from, DateTime to, CancellationToken ct = default)
        => await DbSet
            .Where(q => q.TenantId == tenantId && q.CreatedAt >= from && q.CreatedAt <= to)
            .OrderBy(q => q.CreatedAt)
            .ToListAsync(ct);

    // Most-recent-first within the range, capped at top. Backs the Analytics query inspector.
    public async Task<IEnumerable<QueryLog>> GetRecentAsync(Guid tenantId, DateTime from, DateTime to, int top, CancellationToken ct = default)
        => await DbSet
            .Where(q => q.TenantId == tenantId && q.CreatedAt >= from && q.CreatedAt <= to)
            .OrderByDescending(q => q.CreatedAt)
            .Take(top)
            .ToListAsync(ct);

    public async Task<Result> UpdateFeedbackAsync(Guid id, QueryFeedback feedback, CancellationToken ct = default)
    {
        try
        {
            await DbSet.Where(q => q.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(q => q.Feedback, feedback), ct);
            return Result.Ok();
        }
        catch (Exception ex)
        {
            return Result.Fail(ex.Message);
        }
    }
}

public class ConnectorConfigRepository(ApplicationDbContext db) : Repository<ConnectorConfig>(db), IConnectorConfigRepository
{
    public async Task<IEnumerable<ConnectorConfig>> GetEnabledByTenantAsync(Guid tenantId, CancellationToken ct = default)
        => await DbSet.Where(c => c.TenantId == tenantId && c.IsEnabled).ToListAsync(ct);

    public async Task<IEnumerable<ConnectorConfig>> GetAllEnabledAsync(CancellationToken ct = default)
        => await DbSet.Where(c => c.IsEnabled).ToListAsync(ct);
}

public class RefreshTokenRepository(ApplicationDbContext db) : Repository<AppRefreshToken>(db), IRefreshTokenRepository
{
    public async Task<AppRefreshToken?> GetByTokenHashAsync(string tokenHash, CancellationToken ct = default)
        => await DbSet.FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);

    public async Task<List<AppRefreshToken>> GetActiveByUserAsync(Guid userId, CancellationToken ct = default)
        => await DbSet
            .Where(t => t.UserId == userId && !t.IsRevoked && t.ExpiresAt > DateTime.UtcNow)
            .OrderBy(t => t.CreatedAt)
            .ToListAsync(ct);
}

public class ApiKeyRepository(ApplicationDbContext db) : Repository<AppApiKey>(db), IApiKeyRepository
{
    public async Task<AppApiKey?> GetByKeyHashAsync(string keyHash, CancellationToken ct = default)
        => await DbSet.FirstOrDefaultAsync(k => k.KeyHash == keyHash && k.IsActive, ct);

    public async Task UpdateLastUsedAtAsync(Guid id, CancellationToken ct = default)
        => await DbSet
            .Where(k => k.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, DateTime.UtcNow), ct);
}

public class UserRepository(ApplicationDbContext db) : Repository<AppUser>(db), IUserRepository
{
    public async Task<AppUser?> FindByEmailAsync(string email, CancellationToken ct = default)
        => await DbSet.FirstOrDefaultAsync(u => u.Email == email, ct);
}
