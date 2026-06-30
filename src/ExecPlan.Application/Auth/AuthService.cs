using System.Security.Cryptography;
using System.Text;
using ExecPlan.Application.Abstractions;
using ExecPlan.Application.Common;
using ExecPlan.Domain.Entities;

namespace ExecPlan.Application.Auth;

/// <summary>
/// Login + refresh-token rotation. Pure Application-layer service: depends only on <see cref="IUnitOfWork"/>,
/// <see cref="IPasswordHasher"/>, <see cref="IJwtTokenFactory"/>, <see cref="IRefreshTokenStore"/>, and
/// <see cref="IClock"/> — never on EF Core or the Infrastructure-only <c>RefreshToken</c> entity.
/// </summary>
public sealed class AuthService : IAuthService
{
    // Fixed dummy password whose PBKDF2 hash is computed once (lazily, via the injected
    // IPasswordHasher) and verified against on the "user not found" path of login, so that
    // unsuccessful logins take roughly the same time whether the username exists or not — this
    // closes a username-enumeration timing side-channel.
    private const string DummyPasswordForTimingSafety = "execplan-dummy-password-for-constant-time-auth-failure";

    private readonly IUnitOfWork _uow;
    private readonly IPasswordHasher _hasher;
    private readonly IJwtTokenFactory _jwtFactory;
    private readonly IRefreshTokenStore _refreshTokenStore;
    private readonly IClock _clock;
    private readonly int _refreshTokenDays;
    private readonly Lazy<string> _dummyPasswordHash;

    public AuthService(
        IUnitOfWork uow,
        IPasswordHasher hasher,
        IJwtTokenFactory jwtFactory,
        IRefreshTokenStore refreshTokenStore,
        IClock clock,
        int refreshTokenDays = 14)
    {
        _uow = uow;
        _hasher = hasher;
        _jwtFactory = jwtFactory;
        _refreshTokenStore = refreshTokenStore;
        _clock = clock;
        _refreshTokenDays = refreshTokenDays;
        _dummyPasswordHash = new Lazy<string>(() => _hasher.Hash(DummyPasswordForTimingSafety));
    }

    public async Task<TokenPair> LoginAsync(string userName, string password, CancellationToken ct = default)
    {
        var user = await AuthenticateAsync(userName, password, ct);
        return await IssueTokenPairAsync(user, ct);
    }

    public async Task<TokenPair> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var utcNow = _clock.UtcNow;
        var incomingHash = HashToken(refreshToken);

        var record = await _refreshTokenStore.FindActiveAsync(incomingHash, utcNow, ct);
        if (record is null)
        {
            throw AppException.Unauthorized("Refresh token is invalid, revoked, or expired.");
        }

        var user = await _uow.Repo<User>().GetByIdAsync(record.UserId, ct);
        if (user is null || !user.IsActive)
        {
            throw AppException.Unauthorized("User is no longer active.");
        }

        var newRefreshValue = GenerateRefreshTokenValue();
        var newHash = HashToken(newRefreshValue);

        // Rotation: revoke the old refresh token (recording what replaced it), issue the new one,
        // create the new access token, then a single SaveChanges commits the whole rotation atomically.
        await _refreshTokenStore.RevokeAsync(incomingHash, newHash, ct);

        var principal = ToPrincipal(user);
        var (accessToken, accessExpiresUtc) = _jwtFactory.Create(principal);

        await _refreshTokenStore.IssueAsync(user.Id, newHash, utcNow.AddDays(_refreshTokenDays), ct);

        await _uow.SaveChangesAsync(ct);

        return new TokenPair(accessToken, newRefreshValue, accessExpiresUtc, user.Id, user.Role, user.FullName);
    }

    public async Task<AppUserPrincipal> ValidateCredentialsAsync(string userName, string password, CancellationToken ct = default)
    {
        var user = await AuthenticateAsync(userName, password, ct);
        return ToPrincipal(user);
    }

    // Shared credential check for LoginAsync/ValidateCredentialsAsync. Null/empty inputs and an
    // unknown username both fail uniformly via AppException.Unauthorized — never an unhandled
    // exception, and never a signal (timing or otherwise) that distinguishes "no such user" from
    // "wrong password".
    private async Task<User> AuthenticateAsync(string userName, string password, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(userName) || string.IsNullOrEmpty(password))
        {
            throw AppException.Unauthorized("Invalid username or password.");
        }

        var user = await FindActiveUserByUserNameAsync(userName, ct);
        if (user is null)
        {
            // No such active user — still run a real password verification (against a fixed dummy
            // hash) so this path costs about the same as the "wrong password" path below.
            _hasher.Verify(_dummyPasswordHash.Value, password);
            throw AppException.Unauthorized("Invalid username or password.");
        }

        if (!_hasher.Verify(user.PasswordHash, password))
        {
            throw AppException.Unauthorized("Invalid username or password.");
        }

        return user;
    }

    private async Task<TokenPair> IssueTokenPairAsync(User user, CancellationToken ct)
    {
        var utcNow = _clock.UtcNow;
        var principal = ToPrincipal(user);
        var (accessToken, accessExpiresUtc) = _jwtFactory.Create(principal);

        var refreshValue = GenerateRefreshTokenValue();
        var refreshHash = HashToken(refreshValue);

        await _refreshTokenStore.IssueAsync(user.Id, refreshHash, utcNow.AddDays(_refreshTokenDays), ct);

        await _uow.SaveChangesAsync(ct);

        return new TokenPair(accessToken, refreshValue, accessExpiresUtc, user.Id, user.Role, user.FullName);
    }

    // IRepository<T>.FirstOrDefaultAsync runs as a genuine async EF query in Infrastructure (no
    // sync-over-async); Application stays EF-free since the predicate is just System.Linq.Expressions.
    private Task<User?> FindActiveUserByUserNameAsync(string userName, CancellationToken ct) =>
        _uow.Repo<User>().FirstOrDefaultAsync(u => u.UserName == userName && u.IsActive, ct);

    private static AppUserPrincipal ToPrincipal(User user) => new(user.Id, user.Role, user.FullName, user.UserName);

    private static string GenerateRefreshTokenValue()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string HashToken(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes);
    }
}
