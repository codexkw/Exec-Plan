using ExecPlan.Domain.Common;

namespace ExecPlan.Application.Abstractions;

public interface IRepository<T> where T : BaseEntity
{
    Task<T?> GetByIdAsync(Guid id, CancellationToken ct = default);
    IQueryable<T> Query();                       // no-tracking queryable
    IQueryable<T> Tracking();                     // tracked queryable
    Task AddAsync(T entity, CancellationToken ct = default);
    Task AddRangeAsync(IEnumerable<T> entities, CancellationToken ct = default);
    void Remove(T entity);
}
