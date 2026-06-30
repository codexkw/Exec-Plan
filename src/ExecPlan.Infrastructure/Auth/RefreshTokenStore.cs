using ExecPlan.Application.Abstractions;
using ExecPlan.Application.Auth;
using ExecPlan.Infrastructure.Persistence;

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

    public Task<RefreshTokenRecord?> FindActiveAsync(string tokenHash, DateTime utcNow, CancellationToken ct = default)
    {
        var match = _uow.Repo<RefreshToken>().Query()
            .FirstOrDefault(t => t.TokenHash == tokenHash && t.RevokedAtUtc == null && t.ExpiresAtUtc > utcNow);

        var record = match is null ? null : new RefreshTokenRecord(match.UserId, match.ExpiresAtUtc);
        return Task.FromResult(record);
    }

    public Task RevokeAsync(string tokenHash, string replacedByTokenHash, CancellationToken ct = default)
    {
        var existing = _uow.Repo<RefreshToken>().Tracking()
            .FirstOrDefault(t => t.TokenHash == tokenHash);

        if (existing is not null)
        {
            existing.RevokedAtUtc = _clock.UtcNow;
            existing.ReplacedByTokenHash = replacedByTokenHash;
        }

        return Task.CompletedTask;
    }
}
