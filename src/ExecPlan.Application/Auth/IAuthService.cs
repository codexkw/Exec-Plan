namespace ExecPlan.Application.Auth;

public interface IAuthService
{
    Task<TokenPair> LoginAsync(string userName, string password, CancellationToken ct = default);

    Task<TokenPair> RefreshAsync(string refreshToken, CancellationToken ct = default);

    /// <summary>Same credential check as <see cref="LoginAsync"/> but returns a principal for cookie sign-in (MVC admin) instead of a token pair.</summary>
    Task<AppUserPrincipal> ValidateCredentialsAsync(string userName, string password, CancellationToken ct = default);
}
