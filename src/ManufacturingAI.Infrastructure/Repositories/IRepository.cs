using ManufacturingAI.Core.Common;

namespace ManufacturingAI.Infrastructure.Repositories;

public interface IRepository<T> where T : class
{
    Task<T?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IEnumerable<T>> GetAllAsync(Guid tenantId, CancellationToken ct = default);
    Task<Result<T>> AddAsync(T entity, CancellationToken ct = default);
    Task<Result> UpdateAsync(T entity, CancellationToken ct = default);
    Task<Result> DeleteAsync(Guid id, CancellationToken ct = default);
}
