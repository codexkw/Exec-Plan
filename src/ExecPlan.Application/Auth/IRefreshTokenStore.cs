namespace ExecPlan.Application.Auth;

/// <summary>
/// Abstraction over refresh-token persistence. Lives in Application so <see cref="AuthService"/> never
/// references the Infrastructure-only <c>RefreshToken</c> entity directly. The Infrastructure
/// implementation (<c>RefreshTokenStore</c>) is backed by the shared <c>ExecPlanDbContext</c>/<c>IUnitOfWork</c>.
/// <see cref="IssueAsync"/> and <see cref="RevokeAsync"/> STAGE changes only — they do not call
/// <c>SaveChangesAsync</c>; the caller (<see cref="AuthService"/>) performs the single transaction.
/// </summary>
public interface IRefreshTokenStore
{
    Task IssueAsync(Guid userId, string tokenHash, DateTime expiresUtc, CancellationToken ct = default);

    Task<RefreshTokenRecord?> FindActiveAsync(string tokenHash, DateTime utcNow, CancellationToken ct = default);

    Task RevokeAsync(string tokenHash, string replacedByTokenHash, CancellationToken ct = default);
}

public sealed record RefreshTokenRecord(Guid UserId, DateTime ExpiresAtUtc);
