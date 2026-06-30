using ExecPlan.Application.Abstractions;
using ExecPlan.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace ExecPlan.Infrastructure.Persistence;

public class UnitOfWork : IUnitOfWork
{
    private readonly ExecPlanDbContext _db;
    public UnitOfWork(ExecPlanDbContext db) => _db = db;

    public IRepository<T> Repo<T>() where T : BaseEntity => new Repository<T>(_db);

    public Task<int> SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);

    public async Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken ct = default)
    {
        if (_db.Database.IsRelational() && _db.Database.ProviderName?.Contains("Sqlite") != true)
        {
            return await _db.Database.BeginTransactionAsync(ct);
        }

        return new NoopTx(); // Sqlite in-memory shares one connection; rely on SaveChanges atomicity in tests
    }

    private sealed class NoopTx : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
