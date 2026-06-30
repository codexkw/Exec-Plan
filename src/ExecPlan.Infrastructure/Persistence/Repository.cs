using System.Linq.Expressions;
using ExecPlan.Application.Abstractions;
using ExecPlan.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace ExecPlan.Infrastructure.Persistence;

public class Repository<T> : IRepository<T> where T : BaseEntity
{
    private readonly ExecPlanDbContext _db;
    public Repository(ExecPlanDbContext db) => _db = db;

    public Task<T?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        _db.Set<T>().FirstOrDefaultAsync(x => x.Id == id, ct);

    public IQueryable<T> Query() => _db.Set<T>().AsNoTracking();

    public IQueryable<T> Tracking() => _db.Set<T>();

    public Task<T?> FirstOrDefaultAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default) =>
        _db.Set<T>().AsNoTracking().FirstOrDefaultAsync(predicate, ct);

    public Task<List<T>> ListAsync(Expression<Func<T, bool>>? predicate = null, CancellationToken ct = default)
    {
        var query = _db.Set<T>().AsNoTracking();
        if (predicate is not null)
        {
            query = query.Where(predicate);
        }

        return query.ToListAsync(ct);
    }

    public Task<T?> FirstOrDefaultTrackedAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default) =>
        _db.Set<T>().FirstOrDefaultAsync(predicate, ct);

    public Task<List<T>> ListTrackedAsync(Expression<Func<T, bool>>? predicate = null, CancellationToken ct = default)
    {
        var query = _db.Set<T>();
        if (predicate is not null)
        {
            return query.Where(predicate).ToListAsync(ct);
        }

        return query.ToListAsync(ct);
    }

    public async Task AddAsync(T e, CancellationToken ct = default) => await _db.Set<T>().AddAsync(e, ct);

    public async Task AddRangeAsync(IEnumerable<T> e, CancellationToken ct = default) => await _db.Set<T>().AddRangeAsync(e, ct);

    public void Remove(T e) => _db.Set<T>().Remove(e);
}
