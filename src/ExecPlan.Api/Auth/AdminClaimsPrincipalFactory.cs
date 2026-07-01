using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ExecPlan.Application.Auth;

namespace ExecPlan.Api.Auth;

/// <summary>
/// Builds the <see cref="ClaimsPrincipal"/> the MVC admin area signs into <see cref="AuthPolicies.AdminCookieScheme"/>.
/// Uses exactly the claim types <see cref="CurrentUser"/> reads (<see cref="JwtRegisteredClaimNames.Sub"/> for the
/// user id, <see cref="ClaimTypes.Role"/> for the role) so <c>ICurrentUser</c> behaves identically whether the
/// caller authenticated via the JWT API or the admin cookie.
/// </summary>
public static class AdminClaimsPrincipalFactory
{
    public static ClaimsPrincipal Create(AppUserPrincipal p)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, p.UserId.ToString()),
            new(ClaimTypes.NameIdentifier, p.UserId.ToString()),
            new(ClaimTypes.Role, p.Role.ToString()),
            new(JwtRegisteredClaimNames.Name, p.FullName),
        };
        var id = new ClaimsIdentity(claims, AuthPolicies.AdminCookieScheme,
            JwtRegisteredClaimNames.Name, ClaimTypes.Role);
        return new ClaimsPrincipal(id);
    }
}
