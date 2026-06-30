using ExecPlan.Domain.Enums;

namespace ExecPlan.Application.Auth;

public record TokenPair(string AccessToken, string RefreshToken, DateTime AccessExpiresUtc, Guid UserId, UserRole Role, string FullName);

public record AppUserPrincipal(Guid UserId, UserRole Role, string FullName, string UserName);
