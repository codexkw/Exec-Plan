using ExecPlan.Domain.Common;

namespace ExecPlan.Application.Abstractions;

public interface IUnitOfWork
{
    IRepository<T> Repo<T>() where T : BaseEntity;

    // A single SaveChangesAsync is the unit of atomicity (NFR-8): services stage all rows then save once,
    // which EF wraps in an implicit transaction. No explicit Begin/Commit is needed at single-instance scale.
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
