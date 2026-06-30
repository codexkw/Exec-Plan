using ExecPlan.Application.Abstractions;
using ExecPlan.Application.Auth;
using ExecPlan.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ExecPlan.Infrastructure.Auth;

/// <summary>
/// <see cref="IRefreshTokenStore"/> backed by the shared <see cref="ExecPlanDbContext"/> via
/// <see cref="IUnitOfWork"/>/<see cref="Persistence.RefreshToken"/>. <see cref="IssueAsync"/> and
/// <see cref="RevokeAsync"/> only STAGE changes (add row / set RevokedAtUtc+ReplacedByTokenHash) — the
/// caller (<see cref="AuthService"/>) performs the single <c>SaveChangesAsync</c>.
/// </summary>
public sealed class RefreshTokenStore : IRefreshTokenStore
{
    private readonly IUnitOfWork _uow;
    private readonly IClock _clock;

    public RefreshTokenStore(IUnitOfWork uow, IClock clock)
    {
        _uow = uow;
        _clock = clock;
    }

    public Task IssueAsync(Guid userId, string tokenHash, DateTime expiresUtc, CancellationToken ct = default)
    {
        var token = new RefreshToken
        {
            UserId = userId,
            TokenHash = tokenHash,
            ExpiresAtUtc = expiresUtc,
        };

        return _uow.Repo<RefreshToken>().AddAsync(token, ct);
    }

    public async Task<RefreshTokenRecord?> FindActiveAsync(string tokenHash, DateTime utcNow, CancellationToken ct = default)
    {
        var match = await _uow.Repo<RefreshToken>().FirstOrDefaultAsync(
            t => t.TokenHash == tokenHash && t.RevokedAtUtc == null && t.ExpiresAtUtc > utcNow, ct);

        return match is null ? null : new RefreshTokenRecord(match.UserId, match.ExpiresAtUtc);
    }

    public async Task RevokeAsync(string tokenHash, string replacedByTokenHash, CancellationToken ct = default)
    {
        // Mutates the row, so it must come from the TRACKED set. IRepository<T> has no tracked async
        // finder, but Infrastructure may use EF Core directly: Tracking() returns the underlying
        // DbSet<T> (an IQueryable backed by EF's async query provider), so EF's FirstOrDefaultAsync
        // extension works on it directly.
        var existing = await _uow.Repo<RefreshToken>().Tracking()
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);

        if (existing is not null)
        {
            existing.RevokedAtUtc = _clock.UtcNow;
            existing.ReplacedByTokenHash = replacedByTokenHash;
        }

        // No SaveChangesAsync here — only stages the mutation; AuthService commits the single
        // transaction for the whole rotation.
    }
}
