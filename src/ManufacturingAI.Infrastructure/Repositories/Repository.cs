using ManufacturingAI.Core.Common;
using ManufacturingAI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ManufacturingAI.Infrastructure.Repositories;

public class Repository<T>(ApplicationDbContext db) : IRepository<T> where T : class
{
    protected readonly ApplicationDbContext Db = db;
    protected readonly DbSet<T> DbSet = db.Set<T>();

    public async Task<T?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await DbSet.FindAsync([id], ct);

    public async Task<IEnumerable<T>> GetAllAsync(Guid tenantId, CancellationToken ct = default)
    {
        var prop = typeof(T).GetProperty("TenantId");
        if (prop is null) return await DbSet.ToListAsync(ct);
        return await DbSet.Where(e => EF.Property<Guid>(e, "TenantId") == tenantId).ToListAsync(ct);
    }

    public async Task<Result<T>> AddAsync(T entity, CancellationToken ct = default)
    {
        try
        {
            await DbSet.AddAsync(entity, ct);
            await Db.SaveChangesAsync(ct);
            return Result<T>.Ok(entity);
        }
        catch (Exception ex)
        {
            return Result<T>.Fail(ex.Message);
        }
    }

    public async Task<Result> UpdateAsync(T entity, CancellationToken ct = default)
    {
        try
        {
            DbSet.Update(entity);
            await Db.SaveChangesAsync(ct);
            return Result.Ok();
        }
        catch (Exception ex)
        {
            return Result.Fail(ex.Message);
        }
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var entity = await DbSet.FindAsync([id], ct);
            if (entity is null) return Result.Fail("Entity not found.");
            DbSet.Remove(entity);
            await Db.SaveChangesAsync(ct);
            return Result.Ok();
        }
        catch (Exception ex)
        {
            return Result.Fail(ex.Message);
        }
    }
}
