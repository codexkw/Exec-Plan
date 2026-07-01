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

    // TRACKED variants: identical filtering, but the returned entities are change-tracked so that
    // mutating them is persisted by the next SaveChanges. Services that MUTATE many rows (e.g.
    // escalation) must use these — the no-tracking ListAsync/FirstOrDefaultAsync above would silently
    // drop the edits. System.Linq.Expressions is BCL, so this stays EF-free in the Application layer.
    Task<T?> FirstOrDefaultTrackedAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default);
    Task<List<T>> ListTrackedAsync(Expression<Func<T, bool>>? predicate = null, CancellationToken ct = default);

    // Server-side count (no rows materialized) — for read-only aggregates like the admin dashboard,
    // where loading whole tables via ListAsync just to call .Count would be wasteful. Expression is BCL,
    // so this stays EF-free here; the EF CountAsync implementation lives in Infrastructure's Repository<T>.
    Task<int> CountAsync(Expression<Func<T, bool>>? predicate = null, CancellationToken ct = default);

    // No-tracking "top N by <orderByDescending>" — ordered AND limited SERVER-SIDE (ORDER BY … DESC / TOP),
    // so a bounded "recent N" read (e.g. the admin dashboard's recent-activations list) never materializes a
    // whole, ever-growing table just to keep a handful of rows. Expression is BCL → stays EF-free here.
    Task<List<T>> ListRecentAsync<TKey>(
        Expression<Func<T, TKey>> orderByDescending,
        int take,
        Expression<Func<T, bool>>? predicate = null,
        CancellationToken ct = default);

    Task AddAsync(T entity, CancellationToken ct = default);
    Task AddRangeAsync(IEnumerable<T> entities, CancellationToken ct = default);
    void Remove(T entity);
}
