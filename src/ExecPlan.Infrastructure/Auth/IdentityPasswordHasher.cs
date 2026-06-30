using ExecPlan.Application.Auth;
using Microsoft.AspNetCore.Identity;

namespace ExecPlan.Infrastructure.Auth;

/// <summary>Wraps <see cref="PasswordHasher{TUser}"/> (PBKDF2, ASP.NET Core Identity's default). The generic
/// type parameter is irrelevant to the algorithm, so a plain <see cref="object"/> placeholder is used.</summary>
public sealed class IdentityPasswordHasher : IPasswordHasher
{
    private readonly PasswordHasher<object> _inner = new();
    private static readonly object Placeholder = new();

    public string Hash(string password) => _inner.HashPassword(Placeholder, password);

    public bool Verify(string hash, string password)
    {
        var result = _inner.VerifyHashedPassword(Placeholder, hash, password);
        return result is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;
    }
}
