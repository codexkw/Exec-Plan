using ExecPlan.Application.Abstractions;
using ExecPlan.Domain.Common;

namespace ExecPlan.Infrastructure.Persistence;

public class UnitOfWork : IUnitOfWork
{
    private readonly ExecPlanDbContext _db;
    public UnitOfWork(ExecPlanDbContext db) => _db = db;

    public IRepository<T> Repo<T>() where T : BaseEntity => new Repository<T>(_db);

    // One SaveChanges = one implicit EF transaction (NFR-8). Services stage all rows then save once.
    public Task<int> SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}
