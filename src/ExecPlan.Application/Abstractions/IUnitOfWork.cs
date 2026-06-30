using ExecPlan.Domain.Common;

namespace ExecPlan.Application.Abstractions;

public interface IUnitOfWork
{
    IRepository<T> Repo<T>() where T : BaseEntity;
    Task<int> SaveChangesAsync(CancellationToken ct = default);
    Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken ct = default); // no-op-able on Sqlite
}
