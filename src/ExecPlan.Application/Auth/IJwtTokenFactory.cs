namespace ExecPlan.Application.Auth;

public interface IJwtTokenFactory
{
    (string token, DateTime expiresUtc) Create(AppUserPrincipal user);
}
