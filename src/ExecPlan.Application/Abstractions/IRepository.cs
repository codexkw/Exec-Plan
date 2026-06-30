using System.Linq.Expressions;
using ExecPlan.Domain.Common;

namespace ExecPlan.Application.Abstractions;

public interface IRepository<T> where T : BaseEntity
{
    Task<T?> GetByIdAsync(Guid id, CancellationToken ct = default);
    IQueryable<T> Query();                       // no-tracking queryable
    IQueryable<T> Tracking();                     // tracked queryable

    // Async filtered reads — Application services use these instead of sync-over-async LINQ over
    // Query()/Tracking(). System.Linq.Expressions is BCL, so this introduces no EF Core dependency
    // here; the EF Core async implementation lives only in Infrastructure's Repository<T>.
    Task<T?> FirstOrDefaultAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default);
    Task<List<T>> ListAsync(Expression<Func<T, bool>>? predicate = null, CancellationToken ct = default);

    Task AddAsync(T entity, CancellationToken ct = default);
    Task AddRangeAsync(IEnumerable<T> entities, CancellationToken ct = default);
    void Remove(T entity);
}
